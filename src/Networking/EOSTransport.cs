using System;
using System.Collections.Generic;

namespace Crawlspace2MP
{
    /// <summary>
    /// EOS (Epic Online Services) transport implementation for Quest crossplay.
    /// Uses EOS P2P for networking and EOS Lobbies for matchmaking.
    /// 
    /// Prerequisites:
    ///   - Epic Developer Portal account with a Product configured
    ///   - EOS C# SDK DLLs (Epic.OnlineServices.dll) in lib/
    ///   - Product ID, Sandbox ID, Deployment ID, Client ID, Client Secret
    ///
    /// EOS concepts mapped to our transport:
    ///   - ProductUserId  → peer identity (replaces SteamId)
    ///   - EOS Lobby      → matchmaking lobby (replaces Steam Lobby)
    ///   - EOS P2P        → packet send/receive (replaces Steam P2P)
    ///   - Device ID auth → anonymous login for Quest standalone
    ///   - Connect auth   → cross-platform identity layer
    /// </summary>
    public class EOSTransport : INetworkTransport
    {
        // --- State ---
        public bool IsRunning { get; private set; }
        public bool IsHost { get; private set; }
        public bool IsJoining { get; private set; }
        public bool IsConnected => _connectedPeerCount > 0;
        public bool IsInLobby => !string.IsNullOrEmpty(_currentLobbyId);
        public bool IsLobbyLocked { get; private set; }
        public int ConnectedPeerCount => _connectedPeerCount;
        public int Ping { get; private set; }

        // --- Events ---
        public event Action<int> OnPeerConnected;
        public event Action<int> OnPeerDisconnected;
        public event Action<int, PacketReader> OnDataReceived;
        public event Action<string> OnLobbyCreated;
        public event Action<string> OnLobbyJoined;
        public event Action<string> OnJoinFailed;
        public event Action OnLobbyLeft;
        public event Action<string> OnPlayerJoined;
        public event Action<string> OnPlayerLeft;
        public event Action<string> OnVersionMismatch;
        public event Action OnBecameHost;

        // --- Internal state ---
        private int _connectedPeerCount;
        private string _currentLobbyId;
        private int _nextPeerId = 1;
        private string _localPlayerName = "Player";

        // EOS peer tracking: ProductUserId string → int peer ID
        private Dictionary<string, int> _eosIdToPeerId = new Dictionary<string, int>();
        private Dictionary<int, string> _peerIdToEosId = new Dictionary<int, string>();

        // TODO: EOS SDK handles (uncomment when EOS SDK is added)
        // private Epic.OnlineServices.Platform.PlatformInterface _platform;
        // private Epic.OnlineServices.P2P.P2PInterface _p2p;
        // private Epic.OnlineServices.Lobby.LobbyInterface _lobby;
        // private Epic.OnlineServices.Connect.ConnectInterface _connect;
        // private Epic.OnlineServices.ProductUserId _localUserId;

        // EOS credentials from Epic Developer Portal
        private const string PRODUCT_ID = "7ccbd0a43a654e07b82502fdf44ebb7b";
        private const string SANDBOX_ID = "3b0f51364d8a4d789e84d552aab4a71e";
        private const string DEPLOYMENT_ID = "78a4c405cfc84aa0a83de16457acbb65";
        private const string CLIENT_ID = "xyza7891NJCjU3dn9lUoJwzDahEqTiez";
        private const string CLIENT_SECRET = "NVHdPBnWshzE3elrDVrqZcSSuSQWOEVscDnGx1CdZzo";

        // Transport-level packet types (same as SteamTransport for compatibility)
        private const byte TRANSPORT_PING = 250;
        private const byte TRANSPORT_PONG = 251;
        private const byte TRANSPORT_VERSION_CHECK = 252;
        private const byte TRANSPORT_VERSION_MISMATCH = 253;
        private const byte TRANSPORT_HOST_MIGRATION = 254;

        // P2P socket name for all mod connections
        private const string SOCKET_NAME = "Crawlspace2MP";

        public EOSTransport()
        {
        }

        // =====================================================================
        // LIFECYCLE
        // =====================================================================

        public bool Initialize()
        {
            // TODO: Initialize EOS SDK Platform Interface
            //
            // Steps:
            // 1. Create PlatformInterface with product/sandbox/deployment IDs
            // 2. Authenticate via Connect Interface (Device ID for Quest, 
            //    or Steam auth token for PC crossplay bridge)
            // 3. Cache P2P and Lobby interface handles
            // 4. Register connection request notification handler
            // 5. Register connection closed notification handler
            //
            // var options = new Epic.OnlineServices.Platform.Options
            // {
            //     ProductId = PRODUCT_ID,
            //     SandboxId = SANDBOX_ID,
            //     DeploymentId = DEPLOYMENT_ID,
            //     ClientCredentials = new Epic.OnlineServices.Platform.ClientCredentials
            //     {
            //         ClientId = CLIENT_ID,
            //         ClientSecret = CLIENT_SECRET
            //     }
            // };
            // _platform = Epic.OnlineServices.Platform.PlatformInterface.Create(options);
            // _p2p = _platform.GetP2PInterface();
            // _lobby = _platform.GetLobbyInterface();
            // _connect = _platform.GetConnectInterface();

            Plugin.Log.LogWarning("[EOS] EOSTransport.Initialize() — not yet implemented");
            return false;
        }

        public void Shutdown()
        {
            // TODO: 
            // 1. Close all P2P connections
            // 2. Leave lobby if in one
            // 3. Remove all notification handlers
            // 4. Release PlatformInterface

            _connectedPeerCount = 0;
            _currentLobbyId = null;
            _eosIdToPeerId.Clear();
            _peerIdToEosId.Clear();
            IsRunning = false;
            IsHost = false;
        }

        public void Update()
        {
            // TODO:
            // 1. Call _platform.Tick() — EOS requires this every frame
            // 2. Poll for received packets via P2P.ReceivePacket in a loop
            // 3. For each received packet, resolve sender to peerId and fire OnDataReceived
            // 4. Handle transport-level packets (ping/pong/version/migration)

            // if (_platform == null) return;
            // _platform.Tick();
            // ReceiveAllPackets();
        }

        // =====================================================================
        // HOSTING / JOINING
        // =====================================================================

        public void StartHost(int port = 0)
        {
            // TODO:
            // 1. Authenticate if not already
            // 2. Create EOS Lobby (max 4 players, public)
            // 3. Set lobby attributes (game version, etc.)
            // 4. Accept incoming P2P connections on SOCKET_NAME
            // 5. Set IsHost = true, IsRunning = true
            // 6. Fire OnLobbyCreated with lobby ID string

            Plugin.Log.LogWarning("[EOS] StartHost() — not yet implemented");
        }

        public void Connect(string address, int port)
        {
            // For EOS, "address" would be a lobby ID string
            JoinLobby(address);
        }

        public void JoinLobby(string lobbyId)
        {
            // TODO:
            // 1. Authenticate if not already
            // 2. Join EOS Lobby by ID
            // 3. On success: get lobby members, set up P2P connections
            // 4. Accept P2P connections from existing members
            // 5. Set IsHost = false, IsRunning = true
            // 6. Fire OnLobbyJoined

            Plugin.Log.LogWarning($"[EOS] JoinLobby({lobbyId}) — not yet implemented");
        }

        public void Disconnect()
        {
            // TODO:
            // 1. Close all P2P connections
            // 2. Leave EOS lobby
            // 3. Fire OnLobbyLeft
            // 4. Reset state

            IsRunning = false;
            IsHost = false;
            _currentLobbyId = null;
            _connectedPeerCount = 0;
        }

        public void LockLobby()
        {
            // TODO: Update lobby attributes to prevent new joins
            IsLobbyLocked = true;
        }

        public void UnlockLobby()
        {
            // TODO: Update lobby attributes to allow joins
            IsLobbyLocked = false;
        }

        public void ProcessPendingJoin()
        {
            // TODO: Check if there's a pending lobby join from invite/overlay
        }

        public void BecomeHost()
        {
            // TODO: Promote this client to lobby owner via EOS Lobby API
            IsHost = true;
            OnBecameHost?.Invoke();
        }

        // =====================================================================
        // SENDING DATA
        // =====================================================================

        public void SendToAll(byte[] data, bool reliable = true)
        {
            // TODO: Send packet to all connected peers via EOS P2P
            //
            // foreach peer in _peerIdToEosId:
            //   SendToEosPeer(eosUserId, data, reliable)

            foreach (var kvp in _peerIdToEosId)
            {
                SendTo(kvp.Key, data, reliable);
            }
        }

        public void SendTo(int peerId, byte[] data, bool reliable = true)
        {
            // TODO: Send packet to specific peer via EOS P2P
            //
            // if (!_peerIdToEosId.TryGetValue(peerId, out string eosId)) return;
            //
            // var sendOptions = new Epic.OnlineServices.P2P.SendPacketOptions
            // {
            //     LocalUserId = _localUserId,
            //     RemoteUserId = ProductUserId.FromString(eosId),
            //     SocketId = new SocketId { SocketName = SOCKET_NAME },
            //     Data = data,
            //     Reliability = reliable 
            //         ? PacketReliability.ReliableOrdered 
            //         : PacketReliability.UnreliableUnordered,
            //     AllowDelayedDelivery = true
            // };
            // _p2p.SendPacket(sendOptions);
        }

        // =====================================================================
        // INFO
        // =====================================================================

        public string GetLobbyId()
        {
            return _currentLobbyId ?? "";
        }

        public string GetPlayerName()
        {
            // TODO: EOS doesn't have display names by default with Device ID auth.
            // Options: use EOS UserInfo if Epic account linked, or generate a name.
            return _localPlayerName;
        }

        public List<string> GetConnectedPlayerNames()
        {
            // TODO: Return display names of connected peers
            var names = new List<string>();
            foreach (var kvp in _peerIdToEosId)
            {
                names.Add($"Player_{kvp.Key}");
            }
            return names;
        }

        public void InviteFriends()
        {
            // TODO: Open EOS Social Overlay for invites, or show lobby code
            Plugin.Log.LogWarning("[EOS] InviteFriends() — not yet implemented");
        }

        // =====================================================================
        // INTERNAL HELPERS (to be implemented with EOS SDK)
        // =====================================================================

        // private void AuthenticateWithDeviceId() { }
        // private void AuthenticateWithSteamToken() { }  // For PC crossplay bridge
        // private void OnConnectionRequest(/* EOS callback info */) { }
        // private void OnConnectionClosed(/* EOS callback info */) { }
        // private void ReceiveAllPackets() { }
        // private int AssignPeerId(string eosProductUserId) { }
        // private void RemovePeer(string eosProductUserId) { }
    }
}
