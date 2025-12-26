using System;
using System.Collections.Generic;
using Steamworks;
using Steamworks.Data;

namespace Crawlspace2MP
{
    /// <summary>
    /// Steam-based network transport using Facepunch.Steamworks
    /// Uses Steam lobbies for matchmaking and P2P for data transfer
    /// </summary>
    public class SteamTransport
    {
        public bool IsRunning { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsJoining { get; private set; }  // True while join is in progress
        public bool IsConnected => _connectedPeers.Count > 0;
        public int ConnectedPeerCount => _connectedPeers.Count;
        public bool IsLobbyLocked { get; private set; }  // True during gameplay (no joins allowed)
        
        public Lobby CurrentLobby { get; private set; }
        public bool IsInLobby => CurrentLobby.Id.Value != 0;
        
        // Ping tracking
        public int Ping { get; private set; }  // Estimated ping in ms
        private float _lastPingTime;
        private float _pingInterval = 2f;
        private Dictionary<SteamId, float> _pingSentTimes = new Dictionary<SteamId, float>();
        private Dictionary<SteamId, int> _peerPings = new Dictionary<SteamId, int>();
        
        // Internal packet types for transport-level messages
        private const byte TRANSPORT_PING = 250;
        private const byte TRANSPORT_PONG = 251;
        private const byte TRANSPORT_VERSION_CHECK = 252;
        private const byte TRANSPORT_VERSION_MISMATCH = 253;
        private const byte TRANSPORT_HOST_MIGRATION = 254;
        
        // Events
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
        public event Action<int, PacketReader> OnDataReceived;
        public event Action<string> OnVersionMismatch;  // Remote player has wrong version
        public event Action OnBecameHost;  // This client became host due to migration
        
        // Steam-specific events
        public event Action<Lobby> OnLobbyCreated;
        public event Action<Lobby> OnLobbyJoined;
        public event Action<string> OnJoinFailed;  // Reason for failure
        public event Action OnLobbyLeft;
        public event Action<Friend> OnPlayerJoined;  // With Steam friend info
        public event Action<Friend> OnPlayerLeft;    // With Steam friend info
        
        // Map SteamId to simple int peer IDs for compatibility
        private Dictionary<SteamId, int> _steamIdToPeerId = new Dictionary<SteamId, int>();
        private Dictionary<int, SteamId> _peerIdToSteamId = new Dictionary<int, SteamId>();
        private HashSet<SteamId> _connectedPeers = new HashSet<SteamId>();
        private int _nextPeerId = 1;
        
        private bool _initialized = false;
        
        public SteamTransport()
        {
        }
        
        public bool Initialize()
        {
            if (_initialized) return true;
            
            try
            {
                // Check if Steam is already running (game should have initialized it)
                // Facepunch checks SteamClient.IsValid internally
                if (!SteamClient.IsValid)
                {
                    // Game hasn't initialized Steam, or we need to do it ourselves
                    // Try to find the game's App ID from steam_appid.txt or use a fallback
                    uint appId = GetSteamAppId();
                    Plugin.Log.LogInfo($"Initializing Steam with App ID: {appId}");
                    
                    try
                    {
                        SteamClient.Init(appId, false);
                    }
                    catch (Exception initEx)
                    {
                        Plugin.Log.LogError($"SteamClient.Init failed: {initEx.Message}");
                        Plugin.Log.LogInfo("Make sure Steam is running and you own the game!");
                        return false;
                    }
                }
                
                if (!SteamClient.IsValid)
                {
                    Plugin.Log.LogError("Steam client is not valid after init attempt");
                    return false;
                }
                
                // Subscribe to Steam callbacks
                SteamMatchmaking.OnLobbyCreated += HandleLobbyCreated;
                SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
                SteamMatchmaking.OnLobbyMemberJoined += HandleLobbyMemberJoined;
                SteamMatchmaking.OnLobbyMemberLeave += HandleLobbyMemberLeave;
                SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;
                
                _initialized = true;
                Plugin.Log.LogInfo($"Steam transport initialized! User: {SteamClient.Name} ({SteamClient.SteamId})");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to initialize Steam: {ex.Message}");
                Plugin.Log.LogError("Ensure steam_api64.dll is in the game folder and Steam is running");
                return false;
            }
        }
        
        private const uint CRAWLSPACE2_APP_ID = 2258670;
        
        private uint GetSteamAppId()
        {
            // Get game root folder (where the .exe is)
            string gameRoot = System.IO.Path.GetDirectoryName(
                System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location)));
            
            string appIdPath = System.IO.Path.Combine(gameRoot, "steam_appid.txt");
            
            // Try to read existing file
            try
            {
                if (System.IO.File.Exists(appIdPath))
                {
                    string content = System.IO.File.ReadAllText(appIdPath).Trim();
                    if (uint.TryParse(content, out uint id))
                    {
                        Plugin.Log.LogInfo($"Found steam_appid.txt with ID: {id}");
                        return id;
                    }
                }
            }
            catch { }
            
            // Auto-create steam_appid.txt if it doesn't exist
            try
            {
                System.IO.File.WriteAllText(appIdPath, CRAWLSPACE2_APP_ID.ToString());
                Plugin.Log.LogInfo($"Created steam_appid.txt with App ID: {CRAWLSPACE2_APP_ID}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not create steam_appid.txt: {ex.Message}");
            }
            
            return CRAWLSPACE2_APP_ID;
        }

        public void StartHost(int port = 0)
        {
            if (!Initialize()) return;
            
            IsHost = true;
            IsRunning = true;
            CreateLobbyAsync();
        }
        
        public void Connect(string address, int port)
        {
            // For Steam, "address" is actually a lobby ID or we join via Steam overlay
            if (!Initialize()) return;
            
            IsHost = false;
            IsRunning = true;
            
            // If address looks like a Steam ID, try to join that lobby
            if (ulong.TryParse(address, out ulong lobbyId))
            {
                JoinLobbyAsync(lobbyId);
            }
            else
            {
                Plugin.Log.LogWarning("Steam transport: Use lobby ID or join via Steam friends");
            }
        }
        
        public void Disconnect()
        {
            LeaveLobby();
        }
        
        public void Shutdown()
        {
            LeaveLobby();
            ClearRichPresence();
            _connectedPeers.Clear();
            _steamIdToPeerId.Clear();
            _peerIdToSteamId.Clear();
            IsRunning = false;
            IsHost = false;
            IsJoining = false;
        }
        
        public void Update()
        {
            if (!_initialized || !IsRunning) return;
            
            SteamClient.RunCallbacks();
            ReceiveP2PMessages();
            
            // Send ping periodically
            if (IsConnected && UnityEngine.Time.realtimeSinceStartup - _lastPingTime > _pingInterval)
            {
                _lastPingTime = UnityEngine.Time.realtimeSinceStartup;
                SendPing();
            }
        }
        
        private void SendPing()
        {
            var writer = new PacketWriter(16);
            writer.Put(TRANSPORT_PING);
            writer.Put(UnityEngine.Time.realtimeSinceStartup);
            
            foreach (var peer in _connectedPeers)
            {
                _pingSentTimes[peer] = UnityEngine.Time.realtimeSinceStartup;
                SendToSteamId(peer, writer.GetBytes(), false);  // Unreliable for ping
            }
        }
        
        private void HandlePing(SteamId from, float sentTime)
        {
            // Send pong back
            var writer = new PacketWriter(16);
            writer.Put(TRANSPORT_PONG);
            writer.Put(sentTime);
            SendToSteamId(from, writer.GetBytes(), false);
        }
        
        private void HandlePong(SteamId from, float originalSentTime)
        {
            float rtt = UnityEngine.Time.realtimeSinceStartup - originalSentTime;
            int pingMs = (int)(rtt * 1000f / 2f);  // One-way estimate
            _peerPings[from] = pingMs;
            
            // Update overall ping (average or max of all peers)
            int maxPing = 0;
            foreach (var p in _peerPings.Values)
            {
                if (p > maxPing) maxPing = p;
            }
            Ping = maxPing;
        }
        
        /// <summary>
        /// Send version check to a peer
        /// </summary>
        private void SendVersionCheck(SteamId target)
        {
            var writer = new PacketWriter(64);
            writer.Put(TRANSPORT_VERSION_CHECK);
            writer.Put(PluginInfo.PLUGIN_VERSION);
            SendToSteamId(target, writer.GetBytes(), true);
            Plugin.Log.LogInfo($"Sent version check ({PluginInfo.PLUGIN_VERSION}) to {target}");
        }
        
        private void HandleVersionCheck(SteamId from, string remoteVersion)
        {
            Plugin.Log.LogInfo($"Received version {remoteVersion} from {from}");
            
            if (remoteVersion != PluginInfo.PLUGIN_VERSION)
            {
                Plugin.Log.LogWarning($"Version mismatch! Local: {PluginInfo.PLUGIN_VERSION}, Remote: {remoteVersion}");
                
                // Send mismatch notification
                var writer = new PacketWriter(64);
                writer.Put(TRANSPORT_VERSION_MISMATCH);
                writer.Put(PluginInfo.PLUGIN_VERSION);
                SendToSteamId(from, writer.GetBytes(), true);
                
                // Notify UI
                if (_steamIdToPeerId.TryGetValue(from, out int peerId))
                {
                    OnVersionMismatch?.Invoke($"Player has v{remoteVersion} (you have v{PluginInfo.PLUGIN_VERSION})");
                }
            }
            else
            {
                // Send our version back as acknowledgment
                SendVersionCheck(from);
            }
        }
        
        private void HandleVersionMismatch(SteamId from, string hostVersion)
        {
            OnVersionMismatch?.Invoke($"Version mismatch! Host has v{hostVersion}, you have v{PluginInfo.PLUGIN_VERSION}");
        }
        
        /// <summary>
        /// Lock the lobby so no new players can join (call when starting a night)
        /// </summary>
        public void LockLobby()
        {
            if (!IsInLobby || !IsHost) return;
            
            IsLobbyLocked = true;
            CurrentLobby.SetJoinable(false);
            Plugin.Log.LogInfo("Lobby locked - no new joins allowed");
        }
        
        /// <summary>
        /// Unlock the lobby to allow new players (call when returning to menu)
        /// </summary>
        public void UnlockLobby()
        {
            if (!IsInLobby || !IsHost) return;
            
            IsLobbyLocked = false;
            CurrentLobby.SetJoinable(true);
            Plugin.Log.LogInfo("Lobby unlocked - joins allowed");
        }
        
        #region Lobby Management
        
        private async void CreateLobbyAsync(int maxPlayers = 4)
        {
            try
            {
                Plugin.Log.LogInfo($"Creating Steam lobby for {maxPlayers} players...");
                var lobby = await SteamMatchmaking.CreateLobbyAsync(maxPlayers);
                
                if (lobby.HasValue)
                {
                    CurrentLobby = lobby.Value;
                    CurrentLobby.SetData("game", "Crawlspace2MP");
                    CurrentLobby.SetData("version", PluginInfo.PLUGIN_VERSION);
                    CurrentLobby.SetFriendsOnly();
                    CurrentLobby.SetJoinable(true);
                    
                    Plugin.Log.LogInfo($"Lobby created! ID: {CurrentLobby.Id}");
                    UpdateRichPresence();
                    OnLobbyCreated?.Invoke(CurrentLobby);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to create lobby: {ex}");
            }
        }
        
        private async void JoinLobbyAsync(ulong lobbyId)
        {
            IsJoining = true;
            
            try
            {
                Plugin.Log.LogInfo($"Joining lobby {lobbyId}...");
                
                // Create a timeout task (5 seconds)
                var joinTask = SteamMatchmaking.JoinLobbyAsync(lobbyId);
                var timeoutTask = System.Threading.Tasks.Task.Delay(5000);
                
                var completedTask = await System.Threading.Tasks.Task.WhenAny(joinTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    // Timed out
                    IsRunning = false;
                    IsHost = false;
                    IsJoining = false;
                    Plugin.Log.LogWarning($"Join lobby timed out for {lobbyId}");
                    OnJoinFailed?.Invoke("Lobby not found (timed out)");
                    return;
                }
                
                var lobby = await joinTask;
                
                if (lobby.HasValue && lobby.Value.Id.Value != 0)
                {
                    CurrentLobby = lobby.Value;
                    IsJoining = false;
                    Plugin.Log.LogInfo($"Joined lobby! Host: {CurrentLobby.Owner.Name}");
                }
                else
                {
                    // Lobby not found or join failed
                    IsRunning = false;
                    IsHost = false;
                    IsJoining = false;
                    Plugin.Log.LogWarning($"Lobby {lobbyId} not found or no longer exists");
                    OnJoinFailed?.Invoke("Lobby not found");
                }
            }
            catch (Exception ex)
            {
                IsRunning = false;
                IsHost = false;
                IsJoining = false;
                Plugin.Log.LogError($"Failed to join lobby: {ex}");
                OnJoinFailed?.Invoke("Failed to join lobby");
            }
        }
        
        public void JoinLobby(SteamId lobbyId)
        {
            JoinLobbyAsync(lobbyId);
        }
        
        private void LeaveLobby()
        {
            if (!IsInLobby) return;
            
            // Close P2P sessions
            foreach (var peer in _connectedPeers)
            {
                SteamNetworking.CloseP2PSessionWithUser(peer);
            }
            
            CurrentLobby.Leave();
            CurrentLobby = default;
            _connectedPeers.Clear();
            OnLobbyLeft?.Invoke();
        }
        
        #endregion

        #region P2P Messaging
        
        public void SendToAll(byte[] data, bool reliable = true)
        {
            foreach (var peer in _connectedPeers)
            {
                SendToSteamId(peer, data, reliable);
            }
        }
        
        public void SendTo(int peerId, byte[] data, bool reliable = true)
        {
            if (_peerIdToSteamId.TryGetValue(peerId, out var steamId))
            {
                SendToSteamId(steamId, data, reliable);
            }
        }
        
        private void SendToSteamId(SteamId target, byte[] data, bool reliable)
        {
            var sendType = reliable ? P2PSend.Reliable : P2PSend.Unreliable;
            SteamNetworking.SendP2PPacket(target, data, data.Length, 0, sendType);
        }
        
        private void ReceiveP2PMessages()
        {
            while (SteamNetworking.IsP2PPacketAvailable(0))
            {
                var packet = SteamNetworking.ReadP2PPacket(0);
                if (packet.HasValue)
                {
                    var steamId = packet.Value.SteamId;
                    var data = packet.Value.Data;
                    
                    // Auto-accept P2P from lobby members
                    if (!_connectedPeers.Contains(steamId) && IsLobbyMember(steamId))
                    {
                        AcceptPeer(steamId);
                    }
                    
                    // Check for transport-level packets first
                    if (data.Length > 0)
                    {
                        byte packetType = data[0];
                        
                        // Handle transport-level messages internally
                        if (packetType >= 250)
                        {
                            var reader = new PacketReader(data);
                            reader.GetByte();  // Skip packet type
                            
                            switch (packetType)
                            {
                                case TRANSPORT_PING:
                                    HandlePing(steamId, reader.GetFloat());
                                    break;
                                case TRANSPORT_PONG:
                                    HandlePong(steamId, reader.GetFloat());
                                    break;
                                case TRANSPORT_VERSION_CHECK:
                                    HandleVersionCheck(steamId, reader.GetString());
                                    break;
                                case TRANSPORT_VERSION_MISMATCH:
                                    HandleVersionMismatch(steamId, reader.GetString());
                                    break;
                                case TRANSPORT_HOST_MIGRATION:
                                    HandleHostMigration(steamId);
                                    break;
                            }
                            continue;  // Don't pass to game layer
                        }
                    }
                    
                    // Pass to game layer
                    if (_steamIdToPeerId.TryGetValue(steamId, out int peerId))
                    {
                        var reader = new PacketReader(data);
                        OnDataReceived?.Invoke(peerId, reader);
                    }
                }
            }
        }
        
        private void HandleHostMigration(SteamId newHostId)
        {
            if (newHostId == SteamClient.SteamId)
            {
                Plugin.Log.LogInfo("Host migration: We are now the host!");
                IsHost = true;
                OnBecameHost?.Invoke();
            }
            else
            {
                Plugin.Log.LogInfo($"Host migration: New host is {newHostId}");
            }
        }
        
        private bool IsLobbyMember(SteamId id)
        {
            if (!IsInLobby) return false;
            foreach (var member in CurrentLobby.Members)
            {
                if (member.Id == id) return true;
            }
            return false;
        }
        
        private void AcceptPeer(SteamId steamId)
        {
            SteamNetworking.AcceptP2PSessionWithUser(steamId);
            _connectedPeers.Add(steamId);
            
            int peerId = _nextPeerId++;
            _steamIdToPeerId[steamId] = peerId;
            _peerIdToSteamId[peerId] = steamId;
            
            Plugin.Log.LogInfo($"Accepted P2P from {steamId} as peer {peerId}");
            
            // Send version check
            SendVersionCheck(steamId);
            
            OnPeerConnected?.Invoke(peerId);
        }
        
        private void RemovePeer(SteamId steamId)
        {
            if (_steamIdToPeerId.TryGetValue(steamId, out int peerId))
            {
                _steamIdToPeerId.Remove(steamId);
                _peerIdToSteamId.Remove(peerId);
                _connectedPeers.Remove(steamId);
                _peerPings.Remove(steamId);
                _pingSentTimes.Remove(steamId);
                SteamNetworking.CloseP2PSessionWithUser(steamId);
                
                OnPeerDisconnected?.Invoke(peerId);
            }
        }
        
        #endregion
        
        #region Steam Callbacks
        
        private void HandleLobbyCreated(Result result, Lobby lobby)
        {
            Plugin.Log.LogInfo($"Lobby created callback: {result}");
        }
        
        private void HandleLobbyEntered(Lobby lobby)
        {
            Plugin.Log.LogInfo($"Entered lobby: {lobby.Id}");
            CurrentLobby = lobby;
            
            // Add existing members
            foreach (var member in lobby.Members)
            {
                if (member.Id != SteamClient.SteamId && !_connectedPeers.Contains(member.Id))
                {
                    AcceptPeer(member.Id);
                }
            }
            
            UpdateRichPresence();
            OnLobbyJoined?.Invoke(lobby);
        }
        
        private void HandleLobbyMemberJoined(Lobby lobby, Friend friend)
        {
            if (friend.Id == SteamClient.SteamId) return;
            
            Plugin.Log.LogInfo($"Player joined lobby: {friend.Name}");
            if (!_connectedPeers.Contains(friend.Id))
            {
                AcceptPeer(friend.Id);
            }
            UpdateRichPresence();
            OnPlayerJoined?.Invoke(friend);
        }
        
        private void HandleLobbyMemberLeave(Lobby lobby, Friend friend)
        {
            Plugin.Log.LogInfo($"Player left lobby: {friend.Name}");
            
            bool wasHost = friend.Id == lobby.Owner.Id;
            
            OnPlayerLeft?.Invoke(friend);
            RemovePeer(friend.Id);
            UpdateRichPresence();
            
            // Host migration: if the host left and we're still in the lobby
            if (wasHost && IsInLobby && !IsHost)
            {
                // Check if we should become the new host
                // Steam automatically assigns a new owner, check if it's us
                // Need to re-fetch lobby data
                CheckHostMigration();
            }
        }
        
        private async void CheckHostMigration()
        {
            // Small delay to let Steam update lobby ownership
            await System.Threading.Tasks.Task.Delay(500);
            
            if (!IsInLobby) return;
            
            // Re-check lobby owner
            if (CurrentLobby.Owner.Id == SteamClient.SteamId && !IsHost)
            {
                Plugin.Log.LogInfo("Host migration: We are now the lobby owner!");
                IsHost = true;
                
                // Notify other peers
                var writer = new PacketWriter(16);
                writer.Put(TRANSPORT_HOST_MIGRATION);
                SendToAll(writer.GetBytes(), true);
                
                OnBecameHost?.Invoke();
            }
        }
        
        private void HandleGameLobbyJoinRequested(Lobby lobby, SteamId friendId)
        {
            // Player clicked "Join Game" from Steam overlay
            Plugin.Log.LogInfo($"Join requested via Steam overlay");
            if (!IsRunning)
            {
                IsRunning = true;
                IsHost = false;
            }
            JoinLobby(lobby.Id);
        }
        
        #endregion
        
        // Helper to get lobby ID for sharing
        public string GetLobbyId()
        {
            return IsInLobby ? CurrentLobby.Id.Value.ToString() : "";
        }
        
        /// <summary>
        /// Called when this client becomes the host (via migration)
        /// </summary>
        public void BecomeHost()
        {
            IsHost = true;
            Plugin.Log.LogInfo("BecomeHost called - we are now the host");
        }
        
        /// <summary>
        /// Opens Steam overlay to invite friends to the current lobby
        /// </summary>
        public void InviteFriends()
        {
            if (!IsInLobby)
            {
                Plugin.Log.LogWarning("Cannot invite friends - not in a lobby");
                return;
            }
            
            // Open Steam overlay with invite dialog
            SteamFriends.OpenGameInviteOverlay(CurrentLobby.Id);
            Plugin.Log.LogInfo("Opened Steam invite overlay");
        }
        
        /// <summary>
        /// Get the current Steam user's name
        /// </summary>
        public string GetPlayerName()
        {
            return SteamClient.IsValid ? SteamClient.Name : "Unknown";
        }
        
        /// <summary>
        /// Get connected player names
        /// </summary>
        public List<string> GetConnectedPlayerNames()
        {
            var names = new List<string>();
            if (!IsInLobby) return names;
            
            foreach (var member in CurrentLobby.Members)
            {
                if (member.Id != SteamClient.SteamId)
                {
                    names.Add(member.Name);
                }
            }
            return names;
        }
        
        #region Rich Presence
        
        /// <summary>
        /// Update Steam Rich Presence to show multiplayer status
        /// </summary>
        public void UpdateRichPresence()
        {
            if (!_initialized) return;
            
            try
            {
                if (IsInLobby)
                {
                    int playerCount = _connectedPeers.Count + 1;
                    string status = IsHost ? "Hosting" : "In";
                    
                    // Basic rich presence - shows in Steam friends list
                    // Note: Full rich presence requires Steamworks configuration for the game
                    SteamFriends.SetRichPresence("status", $"{status} Co-op ({playerCount}P)");
                    SteamFriends.SetRichPresence("steam_player_group", CurrentLobby.Id.ToString());
                    SteamFriends.SetRichPresence("steam_player_group_size", playerCount.ToString());
                    
                    Plugin.Log.LogInfo($"Rich presence: {status} Co-op ({playerCount}P)");
                }
                else
                {
                    ClearRichPresence();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to update rich presence: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear rich presence when not in multiplayer
        /// </summary>
        public void ClearRichPresence()
        {
            if (!_initialized) return;
            try
            {
                SteamFriends.ClearRichPresence();
            }
            catch { }
        }
        
        #endregion
    }
}
