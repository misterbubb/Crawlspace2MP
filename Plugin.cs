using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Crawlspace2MP
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log { get; private set; }
        public static GameObject HelmetPrefab { get; private set; }
        
        /// <summary>
        /// Log only in debug builds - use for verbose/spammy messages
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void LogDebug(string message)
        {
            Log?.LogInfo(message);
        }

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"Crawlspace 2 Multiplayer v{PluginInfo.PLUGIN_VERSION} loading...");
            
            LoadCustomAssets();
            
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            
            var manager = new GameObject("Crawlspace2MP_Manager");
            manager.AddComponent<MPManager>();
            DontDestroyOnLoad(manager);
            manager.hideFlags = HideFlags.HideAndDontSave;
            
            Log.LogInfo("Multiplayer mod loaded!");
        }
        
        private void LoadCustomAssets()
        {
            string pluginPath = System.IO.Path.GetDirectoryName(Info.Location);
            string bundlePath = System.IO.Path.Combine(pluginPath, "mpassets");
            
            if (!System.IO.File.Exists(bundlePath)) return;
            
            try
            {
                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null) return;
                
                // Load the helmet and visor by name
                var helmet = bundle.LoadAsset<GameObject>("assets/sm_kaska_lp.fbx");
                var visor = bundle.LoadAsset<GameObject>("assets/vr.dae");
                
                if (helmet != null)
                {
                    HelmetPrefab = helmet;
                    Log.LogInfo("Loaded helmet: SM_Kaska_LP");
                    
                    if (visor != null)
                    {
                        var visorInstance = Instantiate(visor);
                        visorInstance.transform.SetParent(helmet.transform, false);
                        // Position from Unity: (0, -0.11, 0.211)
                        visorInstance.transform.localPosition = new Vector3(0f, -0.11f, 0.211f);
                        visorInstance.transform.localRotation = Quaternion.identity;
                        visorInstance.name = "VR_Visor";
                        Log.LogInfo("Loaded and attached visor: VR");
                    }
                }
                else if (visor != null)
                {
                    HelmetPrefab = visor;
                    Log.LogInfo("Loaded visor only: VR");
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Error loading custom assets: {ex.Message}");
            }
        }
    }

    public class MPManager : MonoBehaviour
    {
        public static MPManager Instance { get; private set; }
        
        public PlayerSync PlayerSync { get; private set; }
        public SteamTransport Steam { get; private set; }
        public VoiceChat VoiceChat { get; private set; }
        public SpectateSystem Spectate { get; private set; }
        
        // State shortcuts
        public bool IsHost => Steam?.IsHost ?? false;
        public bool IsConnected => Steam?.IsConnected ?? false;
        public bool IsRunning => Steam?.IsRunning ?? false;
        public bool IsJoining => Steam?.IsJoining ?? false;
        
        private string _lobbyIdInput = "";
        private string _statusMessage = "Initializing Steam...";
        private string _connectedPlayerName = "";
        private Rect _windowRect = new Rect(10, 10, 320, 480);
        private float _lastLog = 0;
        private bool _steamInitialized = false;
        private float _copiedTime = -10f;  // Time when copy was clicked (-10 so it starts as "Copy")
        private bool _uiHidden = false;  // Toggle with Insert key for streamers
        private bool _showLobbyCode = false;  // Hidden by default for streamers
        private bool _showFriendsList = false;  // Toggle friends list
        private Vector2 _friendsScrollPos = Vector2.zero;  // Scroll position for friends list
        private float _lastFriendsRefresh = 0f;  // Last time friends list was refreshed
        private List<SteamTransport.FriendGameInfo> _cachedFriends = new List<SteamTransport.FriendGameInfo>();

        private void Awake()
        {
            Instance = this;
            
            // Create Steam networking
            Steam = new SteamTransport();
            
            PlayerSync = new PlayerSync();
            PlayerSync.Initialize(Steam);
            
            // Create voice chat
            VoiceChat = new VoiceChat();
            VoiceChat.Initialize(Steam);
            
            // Create spectate system
            Spectate = new SpectateSystem();
            Spectate.Initialize(Steam);
            
            // Subscribe to Steam events for UI updates
            Steam.OnLobbyCreated += OnSteamLobbyCreated;
            Steam.OnLobbyJoined += OnSteamLobbyJoined;
            Steam.OnJoinFailed += OnJoinFailed;
            Steam.OnPeerConnected += OnPeerConnected;
            Steam.OnPeerDisconnected += OnPeerDisconnected;
            Steam.OnPlayerJoined += OnPlayerJoined;
            Steam.OnPlayerLeft += OnPlayerLeft;
            Steam.OnVersionMismatch += OnVersionMismatch;
            Steam.OnBecameHost += OnBecameHost;
            
            // Subscribe to scene changes for lobby locking
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoadedForLobby;
            
            // Initialize Steam
            if (Steam.Initialize())
            {
                _steamInitialized = true;
                _statusMessage = $"Welcome, {SteamClient.Name}!";
                Plugin.Log.LogInfo($"Steam initialized as {SteamClient.Name}");
            }
            else
            {
                _statusMessage = "Steam init failed! Is Steam running?";
                Plugin.Log.LogError("Failed to initialize Steam");
            }
            
            Plugin.Log.LogInfo("MPManager initialized (Steam-only)");
        }
        
        private void OnSteamLobbyCreated(Steamworks.Data.Lobby lobby)
        {
            _lobbyIdInput = lobby.Id.Value.ToString();
            _statusMessage = "Lobby created! Code auto-copied";
            
            // Auto-copy lobby ID to clipboard
            GUIUtility.systemCopyBuffer = _lobbyIdInput;
            _copiedTime = Time.realtimeSinceStartup;
            
            Plugin.Log.LogInfo($"Lobby created: {_lobbyIdInput} (copied to clipboard)");
        }
        
        private void OnSteamLobbyJoined(Steamworks.Data.Lobby lobby)
        {
            _connectedPlayerName = lobby.Owner.Name;
            _statusMessage = $"Joined {lobby.Owner.Name}'s game!";
        }
        
        private void OnJoinFailed(string reason)
        {
            _statusMessage = reason;
            Plugin.Log.LogWarning($"Join failed: {reason}");
        }
        
        private void OnPlayerJoined(Friend friend)
        {
            _connectedPlayerName = friend.Name;
            _statusMessage = $"{friend.Name} joined!";
            Plugin.Log.LogInfo($"Player joined: {friend.Name}");
        }
        
        private void OnPlayerLeft(Friend friend)
        {
            _statusMessage = $"{friend.Name} left";
            if (Steam.ConnectedPeerCount == 0)
            {
                _connectedPlayerName = "";
                _statusMessage = IsHost ? "Waiting for players..." : "Disconnected";
            }
            Plugin.Log.LogInfo($"Player left: {friend.Name}");
        }
        
        private void OnPeerConnected(int peerId)
        {
            Plugin.Log.LogInfo($"Peer connected: {peerId}");
        }
        
        private void OnPeerDisconnected(int peerId)
        {
            if (Steam.ConnectedPeerCount == 0)
            {
                _connectedPlayerName = "";
                _statusMessage = IsHost ? "Waiting for players..." : "Partner disconnected";
            }
            Plugin.Log.LogInfo($"Peer disconnected: {peerId}");
        }
        
        private void OnVersionMismatch(string message)
        {
            _statusMessage = $"⚠️ {message}";
            Plugin.Log.LogWarning($"Version mismatch: {message}");
        }
        
        private void OnBecameHost()
        {
            _statusMessage = "You are now the host!";
            Plugin.Log.LogInfo("Host migration complete - we are now host");
        }
        
        private void OnSceneLoadedForLobby(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (Steam == null || !Steam.IsInLobby) return;
            
            // Lock lobby when entering a night level, unlock when in Home/Intro
            bool isLobbyScene = scene.name.Equals("Home", System.StringComparison.OrdinalIgnoreCase) ||
                                scene.name.IndexOf("Intro", System.StringComparison.OrdinalIgnoreCase) >= 0;
            
            if (isLobbyScene)
            {
                Steam.UnlockLobby();
            }
            else if (scene.name.Contains("Night"))
            {
                Steam.LockLobby();
            }
        }

        private void Update()
        {
            // Log every 10 seconds
            if (Time.time - _lastLog > 10f)
            {
                _lastLog = Time.time;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                Plugin.Log.LogInfo($"[MPManager] Scene={scene}, Running={IsRunning}, Connected={IsConnected}");
            }
            
            // Insert key toggles UI visibility (for streamers/recording)
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.insertKey.wasPressedThisFrame)
            {
                _uiHidden = !_uiHidden;
                Plugin.Log.LogInfo($"UI {(_uiHidden ? "hidden" : "visible")} (Insert key)");
            }
            
            // Poll Steam events
            Steam?.Update();
            PlayerSync?.Update();
            VoiceChat?.Update();
            Spectate?.Update();
        }

        private void OnGUI()
        {
            // Skip all UI rendering if hidden (Insert key toggle)
            if (_uiHidden) return;
            
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            
            // Show small connection indicator when connected
            if (IsRunning && IsConnected)
            {
                int peerCount = Steam.ConnectedPeerCount;
                string role = IsHost ? "Host" : "Client";
                int ping = Steam.Ping;
                
                // Check if we're a ghost
                bool isGhost = PlayerSync?.IsLocalGhost ?? false;
                
                // Color ping based on quality
                string pingColor = ping < 50 ? "green" : (ping < 100 ? "yellow" : "red");
                
                // Adjust box height if ghost
                float boxHeight = isGhost ? 85f : 65f;
                
                GUI.backgroundColor = isGhost ? new UnityEngine.Color(0.3f, 0.3f, 0.5f, 0.8f) : new UnityEngine.Color(0f, 0.5f, 0f, 0.8f);
                GUI.contentColor = UnityEngine.Color.white;
                GUI.Box(new Rect(Screen.width - 160, 10, 150, boxHeight), "");
                GUI.Label(new Rect(Screen.width - 155, 15, 140, 20), $"MP: {role} ({peerCount + 1}P)");
                
                // Ping display with color
                if (ping < 50)
                    GUI.contentColor = new UnityEngine.Color(0.5f, 1f, 0.5f);
                else if (ping < 100)
                    GUI.contentColor = new UnityEngine.Color(1f, 1f, 0.5f);
                else
                    GUI.contentColor = new UnityEngine.Color(1f, 0.5f, 0.5f);
                GUI.Label(new Rect(Screen.width - 155, 32, 140, 20), $"Ping: {ping}ms");
                GUI.contentColor = UnityEngine.Color.white;
                
                float remoteBattery = PlayerSync.GetRemoteBatteryCharge();
                if (remoteBattery >= 0)
                {
                    GUI.Label(new Rect(Screen.width - 155, 49, 140, 20), $"Partner: {remoteBattery:F0}%");
                }
                
                // Ghost indicator
                if (isGhost)
                {
                    GUI.contentColor = new UnityEngine.Color(0.7f, 0.7f, 1f);
                    GUI.Label(new Rect(Screen.width - 155, 66, 140, 20), "👻 GHOST MODE");
                    GUI.contentColor = UnityEngine.Color.white;
                }
            }
            
            // Show full UI in Home/Intro scene or when not connected
            bool isLobbyScene = currentScene.Equals("Home", System.StringComparison.OrdinalIgnoreCase) ||
                                currentScene.IndexOf("Intro", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isLobbyScene && IsConnected)
                return;
            
            GUI.backgroundColor = new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.95f);
            GUI.contentColor = UnityEngine.Color.white;
            _windowRect = GUI.Window(12345, _windowRect, DrawWindow, "Crawlspace 2 MP (Steam)");
        }

        private void DrawWindow(int windowId)
        {
            GUILayout.BeginVertical();
            
            // Check scene state - Home and Intro are both "lobby" areas
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool isLobbyScene = currentScene.Equals("Home", System.StringComparison.OrdinalIgnoreCase) ||
                                currentScene.IndexOf("Intro", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool isInGame = IsRunning && IsConnected && !isLobbyScene;
            
            // Show Steam username at top
            if (_steamInitialized)
            {
                GUI.contentColor = new UnityEngine.Color(0.7f, 0.9f, 1f);
                GUILayout.Label($"👤 {Steam.GetPlayerName()}");
                GUI.contentColor = UnityEngine.Color.white;
            }
            
            // Status with color coding
            if (_statusMessage.Contains("joined") || _statusMessage.Contains("Connected") || _statusMessage.Contains("created"))
                GUI.contentColor = new UnityEngine.Color(0.5f, 1f, 0.5f);
            else if (_statusMessage.Contains("failed") || _statusMessage.Contains("left") || _statusMessage.Contains("Disconnected") || _statusMessage.Contains("not found") || _statusMessage.Contains("Return"))
                GUI.contentColor = new UnityEngine.Color(1f, 0.5f, 0.5f);
            else if (_statusMessage.Contains("Joining") || _statusMessage.Contains("Creating"))
                GUI.contentColor = new UnityEngine.Color(1f, 0.9f, 0.5f);
            else
                GUI.contentColor = UnityEngine.Color.white;
            
            GUILayout.Label($"Status: {_statusMessage}");
            GUI.contentColor = UnityEngine.Color.white;
            
            if (!_steamInitialized)
            {
                GUILayout.Space(10);
                GUILayout.Label("Steam is required for multiplayer.");
                GUILayout.Label("Make sure Steam is running!");
                GUILayout.Space(5);
                GUI.contentColor = new UnityEngine.Color(0.5f, 0.5f, 0.5f);
                GUILayout.Label("Press Insert to hide UI");
                GUI.contentColor = UnityEngine.Color.white;
                GUILayout.EndVertical();
                GUI.DragWindow();
                return;
            }
            
            // Show connected players
            if (IsConnected)
            {
                GUILayout.Space(5);
                var playerNames = Steam.GetConnectedPlayerNames();
                if (playerNames.Count > 0)
                {
                    GUI.contentColor = new UnityEngine.Color(0.8f, 1f, 0.8f);
                    GUILayout.Label($"🎮 Playing with: {string.Join(", ", playerNames)}");
                    GUI.contentColor = UnityEngine.Color.white;
                }
                GUILayout.Label($"Total Players: {Steam.ConnectedPeerCount + 1}");
            }
            
            GUILayout.Space(10);
            
            // === NOT CONNECTED STATE ===
            if (!IsRunning && !IsJoining)
            {
                if (isLobbyScene)
                {
                    // In Home/Intro - show host/join options
                    if (GUILayout.Button("🎮 Host Game", GUILayout.Height(35)))
                        StartHosting();
                    
                    GUILayout.Space(10);
                    GUILayout.Label("Join via Lobby ID:");
                    _lobbyIdInput = GUILayout.TextField(_lobbyIdInput);
                    
                    if (GUILayout.Button("🔗 Join Lobby", GUILayout.Height(35)))
                        JoinSteamLobby();
                    
                    GUILayout.Space(5);
                    GUI.contentColor = new UnityEngine.Color(0.6f, 0.6f, 0.6f);
                    GUILayout.Label("Or: Right-click friend → Join Game");
                    GUI.contentColor = UnityEngine.Color.white;
                    
                    // === FRIENDS LIST ===
                    GUILayout.Space(10);
                    GUILayout.BeginHorizontal();
                    string friendsToggleText = _showFriendsList ? "▼ Friends Playing" : "▶ Friends Playing";
                    if (GUILayout.Button(friendsToggleText, GUILayout.Height(25)))
                    {
                        _showFriendsList = !_showFriendsList;
                        if (_showFriendsList)
                        {
                            _cachedFriends = Steam.GetFriendsPlayingGame();
                            _lastFriendsRefresh = Time.realtimeSinceStartup;
                        }
                    }
                    if (_showFriendsList && GUILayout.Button("🔄", GUILayout.Width(30), GUILayout.Height(25)))
                    {
                        _cachedFriends = Steam.GetFriendsPlayingGame();
                        _lastFriendsRefresh = Time.realtimeSinceStartup;
                    }
                    GUILayout.EndHorizontal();
                    
                    if (_showFriendsList)
                    {
                        GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.15f, 0.2f, 0.95f);
                        GUILayout.BeginVertical("box");
                        
                        if (_cachedFriends.Count == 0)
                        {
                            GUI.contentColor = new UnityEngine.Color(0.6f, 0.6f, 0.6f);
                            GUILayout.Label("No friends playing Crawlspace 2");
                            GUI.contentColor = UnityEngine.Color.white;
                        }
                        else
                        {
                            _friendsScrollPos = GUILayout.BeginScrollView(_friendsScrollPos, GUILayout.Height(100));
                            foreach (var friend in _cachedFriends)
                            {
                                GUILayout.BeginHorizontal();
                                
                                // Friend name and status
                                GUI.contentColor = friend.IsJoinable ? new UnityEngine.Color(0.5f, 1f, 0.5f) : UnityEngine.Color.white;
                                GUILayout.Label(friend.Name, GUILayout.Width(120));
                                
                                GUI.contentColor = new UnityEngine.Color(0.7f, 0.7f, 0.7f);
                                GUILayout.Label(friend.Status, GUILayout.Width(80));
                                GUI.contentColor = UnityEngine.Color.white;
                                
                                // Join button if joinable
                                if (friend.IsJoinable)
                                {
                                    GUI.backgroundColor = new UnityEngine.Color(0.2f, 0.5f, 0.2f);
                                    if (GUILayout.Button("Join", GUILayout.Width(45)))
                                    {
                                        Steam.JoinFriendGame(friend.LobbyId);
                                        _statusMessage = $"Joining {friend.Name}...";
                                    }
                                    GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.15f, 0.2f, 0.95f);
                                }
                                
                                GUILayout.EndHorizontal();
                            }
                            GUILayout.EndScrollView();
                        }
                        
                        GUILayout.EndVertical();
                        GUI.backgroundColor = new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.95f);
                    }
                }
                else
                {
                    // Not in Home - tell user to go back
                    GUILayout.Space(5);
                    GUI.backgroundColor = new UnityEngine.Color(0.4f, 0.3f, 0.1f);
                    GUILayout.BeginVertical("box");
                    GUI.contentColor = new UnityEngine.Color(1f, 0.85f, 0.5f);
                    GUILayout.Label("🏠 Go to Home to play multiplayer");
                    GUILayout.Space(3);
                    GUI.contentColor = new UnityEngine.Color(0.8f, 0.8f, 0.8f);
                    GUILayout.Label("Host and join from the house area,");
                    GUILayout.Label("then start the night together.");
                    GUI.contentColor = UnityEngine.Color.white;
                    GUILayout.EndVertical();
                    GUI.backgroundColor = new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.95f);
                }
            }
            
            // === JOINING STATE ===
            if (IsJoining)
            {
                GUILayout.Space(5);
                GUI.contentColor = new UnityEngine.Color(1f, 0.9f, 0.5f);
                GUILayout.Label("⏳ Joining lobby...");
                GUI.contentColor = UnityEngine.Color.white;
                
                if (GUILayout.Button("Cancel", GUILayout.Height(28)))
                {
                    Disconnect();
                    _statusMessage = "Join cancelled";
                }
            }
            
            // === HOSTING/CONNECTED STATE ===
            if (IsHost && Steam.IsInLobby)
            {
                GUILayout.Space(5);
                
                if (isLobbyScene)
                {
                    // In Home/Intro as host - can invite
                    GUI.backgroundColor = new UnityEngine.Color(0.2f, 0.5f, 0.8f);
                    if (GUILayout.Button("📨 Invite Friends", GUILayout.Height(30)))
                    {
                        Steam.InviteFriends();
                    }
                    GUI.backgroundColor = new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.95f);
                    
                    GUILayout.Space(5);
                    
                    // Toggle to show/hide lobby code + copy button
                    GUILayout.BeginHorizontal();
                    string toggleText = _showLobbyCode ? "Hide Code" : "Show Code";
                    if (GUILayout.Button(toggleText, GUILayout.Width(80)))
                    {
                        _showLobbyCode = !_showLobbyCode;
                    }
                    
                    bool recentlyCopied = (Time.realtimeSinceStartup - _copiedTime) < 2f;
                    string copyText = recentlyCopied ? "✓ Copied!" : "📋 Copy";
                    if (GUILayout.Button(copyText, GUILayout.Width(80)))
                    {
                        GUIUtility.systemCopyBuffer = _lobbyIdInput;
                        _copiedTime = Time.realtimeSinceStartup;
                    }
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    
                    if (_showLobbyCode)
                    {
                        GUI.contentColor = new UnityEngine.Color(0.9f, 0.9f, 0.6f);
                        GUILayout.TextField(_lobbyIdInput);
                        GUI.contentColor = UnityEngine.Color.white;
                    }
                }
                else
                {
                    // In game as host - lobby is locked
                    GUI.contentColor = new UnityEngine.Color(0.7f, 0.7f, 0.7f);
                    GUILayout.Label("🔒 Lobby locked during game");
                    GUILayout.Label("New players can join in Home");
                    GUI.contentColor = UnityEngine.Color.white;
                }
            }
            
            // === DISCONNECT & VOICE ===
            if (IsRunning && !IsJoining)
            {
                GUILayout.Space(10);
                
                // Voice chat toggle - use button style for better visibility
                bool voiceEnabled = VoiceChat?.Enabled ?? false;
                string voiceText = voiceEnabled ? "🎤 Voice: ON" : "🎤 Voice: OFF";
                GUI.backgroundColor = voiceEnabled ? new UnityEngine.Color(0.2f, 0.6f, 0.2f) : new UnityEngine.Color(0.4f, 0.4f, 0.4f);
                if (GUILayout.Button(voiceText, GUILayout.Height(28)))
                {
                    if (VoiceChat != null)
                    {
                        VoiceChat.Enabled = !voiceEnabled;
                    }
                }
                GUI.backgroundColor = new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.95f);
                
                GUILayout.Space(5);
                GUI.backgroundColor = new UnityEngine.Color(0.6f, 0.2f, 0.2f);
                if (GUILayout.Button("Disconnect", GUILayout.Height(28)))
                    Disconnect();
                GUI.backgroundColor = new UnityEngine.Color(0.1f, 0.1f, 0.1f, 0.95f);
            }
            
            // Footer hints
            GUILayout.FlexibleSpace();
            GUI.contentColor = new UnityEngine.Color(0.5f, 0.5f, 0.5f);
            GUILayout.Label("Press Insert to hide UI");
            GUI.contentColor = UnityEngine.Color.white;
            
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
        
        private void JoinSteamLobby()
        {
            // Only allow joining in Home/Intro scene
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool isLobbyScene = currentScene.Equals("Home", System.StringComparison.OrdinalIgnoreCase) ||
                                currentScene.IndexOf("Intro", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isLobbyScene)
            {
                _statusMessage = "Return to Home to join!";
                return;
            }
            
            if (string.IsNullOrEmpty(_lobbyIdInput))
            {
                _statusMessage = "Enter a lobby ID first";
                return;
            }
            
            // Don't allow joining if already in any session
            if (Steam.IsRunning || Steam.IsInLobby)
            {
                Plugin.Log.LogInfo($"Already in session - IsRunning={Steam.IsRunning}, IsInLobby={Steam.IsInLobby}");
                _statusMessage = "Already in a session! Disconnect first.";
                return;
            }
            
            if (ulong.TryParse(_lobbyIdInput, out ulong lobbyId))
            {
                Steam.JoinLobby(lobbyId);
                _statusMessage = "Joining lobby...";
            }
            else
            {
                _statusMessage = "Invalid lobby ID";
            }
        }

        private void StartHosting()
        {
            // Only allow hosting in Home/Intro scene
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool isLobbyScene = currentScene.Equals("Home", System.StringComparison.OrdinalIgnoreCase) ||
                                currentScene.IndexOf("Intro", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isLobbyScene)
            {
                _statusMessage = "Return to Home to host!";
                return;
            }
            
            if (!_steamInitialized)
            {
                _statusMessage = "Steam not initialized!";
                return;
            }
            
            // Don't allow hosting if already in any session
            if (Steam.IsRunning || Steam.IsInLobby || Steam.IsJoining)
            {
                Plugin.Log.LogInfo($"Already in session - IsRunning={Steam.IsRunning}, IsInLobby={Steam.IsInLobby}, IsJoining={Steam.IsJoining}");
                if (Steam.IsHost)
                {
                    _statusMessage = "Already hosting!";
                }
                else
                {
                    _statusMessage = "Already in a session! Disconnect first.";
                }
                return;
            }
            
            try
            {
                Steam.StartHost();
                _statusMessage = "Creating lobby...";
                Plugin.Log.LogInfo("Starting Steam host...");
            }
            catch (System.Exception ex)
            {
                _statusMessage = $"Host failed: {ex.Message}";
                Plugin.Log.LogError($"Failed to start host: {ex}");
            }
        }

        private void Disconnect()
        {
            try
            {
                Spectate?.Cleanup();
                VoiceChat?.Cleanup();
                PlayerSync?.Cleanup();
                Steam?.Shutdown();
                _statusMessage = "Disconnected";
                Plugin.Log.LogInfo("Disconnected");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Disconnect error: {ex}");
                _statusMessage = "Disconnected";
            }
        }

        private void OnDestroy()
        {
            Plugin.Log.LogWarning("MPManager OnDestroy called!");
            Spectate?.Cleanup();
            VoiceChat?.Cleanup();
            PlayerSync?.Cleanup();
            Steam?.Shutdown();
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.crawlspace2.multiplayer";
        public const string PLUGIN_NAME = "Crawlspace2MP";
        public const string PLUGIN_VERSION = "1.2.0";
    }
    
    // Harmony patches to block client from controlling game flow
    // A player is a "client" if they're connected AND they're not the lobby owner
    [HarmonyPatch(typeof(calenderControl), "increaseValue")]
    public class CalendarIncreasePatch
    {
        static bool Prefix()
        {
            // Block if we're in a multiplayer session but not the lobby owner
            var steam = MPManager.Instance?.Steam;
            if (steam != null && steam.IsInLobby && steam.IsConnected)
            {
                // Check if we're actually the lobby owner
                bool isLobbyOwner = steam.CurrentLobby.Owner.Id == Steamworks.SteamClient.SteamId;
                if (!isLobbyOwner)
                {
                    Plugin.Log.LogInfo("[Client] Calendar blocked - only host can select night");
                    return false; // Skip original method
                }
            }
            return true; // Allow original method
        }
    }
    
    [HarmonyPatch(typeof(calenderControl), "decreaseValue")]
    public class CalendarDecreasePatch
    {
        static bool Prefix()
        {
            // Block if we're in a multiplayer session but not the lobby owner
            var steam = MPManager.Instance?.Steam;
            if (steam != null && steam.IsInLobby && steam.IsConnected)
            {
                bool isLobbyOwner = steam.CurrentLobby.Owner.Id == Steamworks.SteamClient.SteamId;
                if (!isLobbyOwner)
                {
                    Plugin.Log.LogInfo("[Client] Calendar blocked - only host can select night");
                    return false;
                }
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(doorSceneChange), "OnTriggerEnter")]
    public class DoorPatch
    {
        static bool Prefix(doorSceneChange __instance, Collider other)
        {
            var steam = MPManager.Instance?.Steam;
            if (steam == null || !steam.IsInLobby) return true; // Not in multiplayer
            
            bool isLobbyOwner = steam.CurrentLobby.Owner.Id == Steamworks.SteamClient.SteamId;
            
            // Block if we're a client (not the lobby owner)
            if (steam.IsConnected && !isLobbyOwner)
            {
                Plugin.Log.LogInfo("[Client] Door blocked - only host can start night");
                return false;
            }
            
            // If we're the host, send scene change BEFORE the original method runs
            if (isLobbyOwner && steam.IsRunning)
            {
                // Call loadSelectedNight() first to populate scenename (same as original method does)
                __instance.loadSelectedNight();
                
                // Now read the scenename field directly (it's public)
                string sceneName = __instance.scenename;
                
                if (!string.IsNullOrEmpty(sceneName))
                {
                    Plugin.Log.LogInfo($"[Host] Door entered, sending scene change: {sceneName}");
                    MPManager.Instance.PlayerSync.SendSceneChange(sceneName);
                }
                else
                {
                    Plugin.Log.LogWarning("[Host] doorSceneChange.scenename is empty after loadSelectedNight()");
                }
            }
            
            return true;
        }
    }
    
    [HarmonyPatch(typeof(sceneLeave), "OnTriggerEnter")]
    public class SceneLeavePatch
    {
        static bool Prefix(sceneLeave __instance, Collider other)
        {
            var steam = MPManager.Instance?.Steam;
            if (steam == null || !steam.IsInLobby) return true; // Not in multiplayer
            
            bool isLobbyOwner = steam.CurrentLobby.Owner.Id == Steamworks.SteamClient.SteamId;
            
            // Block if we're a client (not the lobby owner)
            if (steam.IsConnected && !isLobbyOwner)
            {
                Plugin.Log.LogInfo("[Client] Scene exit blocked - only host can complete night");
                return false;
            }
            
            // If we're the host, send scene change BEFORE the original method runs
            if (isLobbyOwner && steam.IsRunning)
            {
                // Call loadSelectedNight() first to populate scenename (same as original method does)
                __instance.loadSelectedNight();
                
                // scenename is private, so we need reflection
                var scenenameField = typeof(sceneLeave).GetField("scenename", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                string sceneName = scenenameField?.GetValue(__instance) as string;
                
                if (!string.IsNullOrEmpty(sceneName))
                {
                    Plugin.Log.LogInfo($"[Host] Scene exit, sending scene change: {sceneName}");
                    MPManager.Instance.PlayerSync.SendSceneChange(sceneName);
                }
                else
                {
                    Plugin.Log.LogWarning("[Host] sceneLeave.scenename is empty after loadSelectedNight()");
                }
            }
            
            return true;
        }
    }
    
    [HarmonyPatch(typeof(StartMenu), "LoadScene")]
    public class StartMenuPatch
    {
        static bool Prefix()
        {
            // Block if connected as client
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsConnected && 
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Debug menu blocked - only host can change scenes");
                return false;
            }
            return true;
        }
    }
    
    // Sync painting flashes between players
    [HarmonyPatch(typeof(paintingControl), "onPaintingFlash")]
    public class PaintingFlashPatch
    {
        static void Postfix(int paintingID)
        {
            // Don't re-send if we're receiving a flash from network
            if (MPManager.Instance?.PlayerSync?.IsReceivingPaintingFlash == true)
                return;
            
            // Send the flash to other players
            MPManager.Instance?.PlayerSync?.SendPaintingFlash(paintingID);
        }
    }
    
    // Disable painting entity spawning on client - host controls when entities appear
    [HarmonyPatch(typeof(paintingControl), "setEntityPainting")]
    public class PaintingEntitySpawnPatch
    {
        static bool Prefix()
        {
            // Only controller should spawn painting entities
            if (MPManager.Instance?.PlayerSync != null && 
                MPManager.Instance.Steam != null &&
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return false; // Skip on client - host will sync entity state
            }
            return true;
        }
        
        static void Postfix(paintingControl __instance)
        {
            // After controller spawns entity, sync the state to client
            if (MPManager.Instance?.PlayerSync != null && 
                MPManager.Instance.Steam != null &&
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                // Send painting entity state
                MPManager.Instance.PlayerSync?.SendPaintingEntityState(__instance);
            }
        }
    }
    
    // Disable painting timer/death logic on client - host controls it
    [HarmonyPatch(typeof(paintingControl), "timerControl")]
    public class PaintingTimerPatch
    {
        static bool Prefix()
        {
            // Only controller should run death timer logic
            if (MPManager.Instance?.PlayerSync != null && 
                MPManager.Instance.Steam != null &&
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return false; // Skip on client when host is controlling
            }
            return true;
        }
    }
    
    // Sync painting death to all players - when paintings kill, everyone in main room dies
    [HarmonyPatch(typeof(paintingControl), "killPlayer")]
    public class PaintingKillSyncPatch
    {
        static void Postfix(paintingControl __instance)
        {
            // When host triggers painting death, sync to client
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                // Send painting death to other players
                MPManager.Instance.PlayerSync.SendPaintingDeath();
            }
        }
    }
    
    // Disable painting trigger timer on client
    [HarmonyPatch(typeof(paintingControl), "paintingTriggerTimer")]
    public class PaintingTriggerPatch
    {
        static bool Prefix()
        {
            // Only controller should trigger painting entities
            if (MPManager.Instance?.PlayerSync != null && 
                MPManager.Instance.Steam != null &&
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return false; // Skip on client when host is controlling
            }
            return true;
        }
    }
    
    // Disable clown randomizer on client - controller (host or takeover) controls which clown is visible
    [HarmonyPatch(typeof(clownRandom), "randomizerV2")]
    public class ClownRandomizerPatch
    {
        static bool Prefix()
        {
            // Only controller should randomize clown position
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return false; // Skip on non-controller - controller will sync
            }
            return true;
        }
        
        static void Postfix(clownRandom __instance)
        {
            // After controller randomizes, sync to other player
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                MPManager.Instance.PlayerSync?.SendClownState(__instance);
            }
        }
    }
    
    // Disable clown visibility check on non-controller - controller handles attack state
    [HarmonyPatch(typeof(clownRandom), "checkVisableClown")]
    public class ClownVisibilityPatch
    {
        static bool Prefix()
        {
            // Only controller should check visibility and trigger attacks
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return false; // Skip on non-controller
            }
            return true;
        }
    }
    
    // Disable clown attack/kill timer on non-controller
    [HarmonyPatch(typeof(clownRandom), "FixedUpdate")]
    public class ClownUpdatePatch
    {
        static bool Prefix()
        {
            // Only controller should run clown logic
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return false; // Skip on non-controller
            }
            return true;
        }
    }
    
    // Sync clown attack start
    [HarmonyPatch(typeof(clownRandom), "clownStartAttack")]
    public class ClownAttackPatch
    {
        static void Postfix(clownRandom __instance)
        {
            // After controller starts attack, sync to other player
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                MPManager.Instance.PlayerSync?.SendClownAttack();
            }
        }
    }
    
    // Sync Jeff flashes between players
    [HarmonyPatch(typeof(jeffBrain), "onFlash")]
    public class JeffFlashPatch
    {
        static void Postfix()
        {
            // Don't re-send if we're receiving a flash from network
            if (MPManager.Instance?.PlayerSync?.IsReceivingJeffFlash == true)
                return;
            
            // Send the flash to other players
            MPManager.Instance?.PlayerSync?.SendJeffFlash();
        }
    }
    
    // Helper to get closest player position (local or remote)
    public static class MultiplayerTargeting
    {
        public static Vector3 GetClosestPlayerPosition(Vector3 monsterPos, GameObject localPlayer)
        {
            Vector3 closestPos = monsterPos; // Default to monster pos if no valid targets
            float closestDist = float.MaxValue;
            
            // Check local player
            if (localPlayer != null)
            {
                Vector3 localPos = localPlayer.transform.position;
                float localDist = Vector3.Distance(monsterPos, localPos);
                if (localDist < closestDist)
                {
                    closestDist = localDist;
                    closestPos = localPos;
                }
            }
            
            // Check remote players
            if (MPManager.Instance?.PlayerSync != null)
            {
                var remotePositions = MPManager.Instance.PlayerSync.GetRemotePlayerPositionsNonGhost();
                foreach (var remotePos in remotePositions)
                {
                    float dist = Vector3.Distance(monsterPos, remotePos);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestPos = remotePos;
                    }
                }
            }
            
            // If no valid targets found, return local player pos anyway (monster needs somewhere to go)
            if (closestDist == float.MaxValue && localPlayer != null)
            {
                return localPlayer.transform.position;
            }
            
            return closestPos;
        }
        
        public static float GetClosestPlayerDistance(Vector3 monsterPos, GameObject localPlayer)
        {
            Vector3 closest = GetClosestPlayerPosition(monsterPos, localPlayer);
            return Vector3.Distance(monsterPos, closest);
        }
    }
    
    // SPARKY: Triggers synced, runs LOCALLY - each player has their own Sparky chasing them
    [HarmonyPatch(typeof(sparkyBrain), "huntMode")]
    public class SparkyHuntPatch
    {
        static bool Prefix(sparkyBrain __instance)
        {
            // Let Sparky run locally - each player's Sparky chases THEM
            // State is synced so they trigger at the same time
            return true;
        }
    }
    
    // SPARKY: Runs locally
    [HarmonyPatch(typeof(sparkyBrain), "wanderMode")]
    public class SparkyWanderPatch
    {
        static bool Prefix()
        {
            // Let Sparky wander locally
            return true;
        }
    }
    
    // JEFF: Triggers synced, runs LOCALLY - each player has their own Jeff near them
    [HarmonyPatch(typeof(jeffBrain), "huntMode")]
    public class JeffHuntPatch
    {
        static bool Prefix(jeffBrain __instance)
        {
            // Let Jeff run locally - each player's Jeff teleports near THEM
            // State is synced so they trigger at the same time
            return true;
        }
    }
    
    // JEFF: Runs locally
    [HarmonyPatch(typeof(jeffBrain), "wanderMode")]
    public class JeffWanderPatch
    {
        static bool Prefix()
        {
            // Let Jeff wander locally
            return true;
        }
    }
    
    // HENRY: SYNCED - Block AI on client, host controls position
    [HarmonyPatch(typeof(henryBrain), "moveToPlayer")]
    public class HenryMovePatch
    {
        static bool Prefix(henryBrain __instance)
        {
            // Only host runs Henry AI - client receives position sync
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.Steam.IsHost)
                    return false; // Block on client
            }
            return true;
        }
    }
    
    // SMILE: Triggers synced, runs LOCALLY - each player has their own Smile chasing them
    [HarmonyPatch(typeof(SmileBrain), "moveToPlayer")]
    public class SmileMovePatch
    {
        static bool Prefix(SmileBrain __instance)
        {
            // Let Smile run locally - each player's Smile chases THEM
            // State (isChasing, chaseTime) is synced so they trigger at the same time
            return true;
        }
    }
    
    // SPARKY: Runs locally - each player has their own Sparky chasing THEM
    // Triggers are synced so both players' Sparkys start at the same time
    // Each player must cover THEIR OWN ears to survive THEIR Sparky
    [HarmonyPatch(typeof(sparkyBrain), "playerDistFuncStateControl")]
    public class SparkyClientPatch
    {
        static bool Prefix(sparkyBrain __instance)
        {
            // Ghosts can't die again
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Let Sparky run completely locally - game handles ear covering for local player
            return true;
        }
    }
    
    // JEFF: Runs locally
    [HarmonyPatch(typeof(jeffBrain), "playerDistFuncStateControl")]
    public class JeffClientPatch
    {
        static bool Prefix(jeffBrain __instance)
        {
            // Ghosts can't die again
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Let Jeff run locally - each player has their own Jeff
            return true;
        }
    }
    
    // HENRY: SYNCED - Block state control on client
    [HarmonyPatch(typeof(henryBrain), "playerDistFuncStateControl")]
    public class HenryClientPatch
    {
        static bool Prefix(henryBrain __instance)
        {
            // Ghosts can't die again
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Only host runs Henry AI - client receives position sync
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.Steam.IsHost)
                    return false; // Block on client
            }
            return true;
        }
    }
    
    // HAROLD: SYNCED - Block AI on client
    [HarmonyPatch(typeof(mapEnBrain), "playerDistFunc")]
    public class HaroldClientPatch
    {
        static bool Prefix(mapEnBrain __instance)
        {
            // Ghosts can't die again
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Only host runs Harold AI - client receives position sync
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.Steam.IsHost)
                    return false; // Block on client
            }
            return true;
        }
    }
    
    // HAROLD: SYNCED - Block wander on client
    [HarmonyPatch(typeof(mapEnBrain), "wanderMode")]
    public class HaroldWanderPatch
    {
        static bool Prefix(mapEnBrain __instance)
        {
            // Only host runs Harold AI - client receives position sync
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.Steam.IsHost)
                    return false; // Block on client
            }
            return true;
        }
    }
    
    // SMILE: Runs locally
    [HarmonyPatch(typeof(SmileBrain), "playerDistFuncStateControl")]
    public class SmileClientPatch
    {
        static bool Prefix(SmileBrain __instance)
        {
            // Ghosts can't die again
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Let Smile run locally - each player has their own Smile
            return true;
        }
    }
    
    // Let EnemyDifMaster run locally - attack triggers happen for each player
    [HarmonyPatch(typeof(EnemyDifMaster), "multiAttackTrigger")]
    public class EnemyDifMasterMultiAttackPatch
    {
        static bool Prefix()
        {
            // Let attack triggers run locally
            return true;
        }
    }
    
    [HarmonyPatch(typeof(EnemyDifMaster), "attackTrigger")]
    public class EnemyDifMasterAttackPatch
    {
        static bool Prefix()
        {
            // Let attack triggers run locally
            return true;
        }
    }
    
    // SMILE: Trigger synced - when host triggers, clients also trigger (but at their own position)
    [HarmonyPatch(typeof(SmileBrain), "onTrigger")]
    public class SmileTriggerPatch
    {
        static void Postfix(SmileBrain __instance)
        {
            // When Smile triggers, sync the trigger event (not position)
            // Each player's Smile will teleport near THEM
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.Steam.IsHost)
            {
                // Send trigger event - clients will run their own onTrigger
                MPManager.Instance.PlayerSync?.SendSmileTrigger(__instance.transform.position);
            }
        }
    }
    
    // Prevent client from running random puzzle initialization - host will sync the puzzle state
    [HarmonyPatch(typeof(PuzzleMaster), "Start")]
    public class PuzzleMasterStartPatch
    {
        static bool Prefix(PuzzleMaster __instance)
        {
            // Only skip on client - host needs to run the random initialization
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Skipping PuzzleMaster random init - waiting for host sync");
                
                // Still need to initialize the static variables
                PuzzleMaster.totalCompletedPuzzles = 0;
                PuzzleMaster.requiredPuzzles = __instance.totalPuzzlesThisNight;
                
                // Don't run the random enableFan() calls - host will tell us which puzzles are active
                return false;
            }
            return true;
        }
    }
    
    // Prevent client from generating random puzzle preset IDs - host will sync them
    [HarmonyPatch(typeof(PuzzleController), "setPuzzlePresetID")]
    public class PuzzlePresetPatch
    {
        static bool Prefix()
        {
            // Only skip on client - host needs to generate the random preset
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Skipping random puzzle preset - waiting for host sync");
                return false;
            }
            return true;
        }
    }
    
    // Make puzzles work when EITHER player has battery in the slot
    // This prevents resetBoard() from being called when only remote player has battery there
    [HarmonyPatch(typeof(PuzzleController), "FixedUpdate")]
    public class PuzzleControllerUpdatePatch
    {
        static bool Prefix(PuzzleController __instance)
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync == null) return true; // Not in multiplayer
            
            // Check if local battery is in this puzzle slot
            bool localBatteryHere = __instance.thisPuzzleID == BackpackControl.batteryLocationID && BackpackControl.batteryCharge > 0f;
            
            // If local battery is here, let original run normally
            if (localBatteryHere) return true;
            
            // Check if remote player has battery in this puzzle's slot
            int remoteBatteryLocation = playerSync.GetRemoteBatteryLocationID();
            float remoteBatteryCharge = playerSync.GetRemoteBatteryCharge();
            bool remoteBatteryHere = __instance.thisPuzzleID == remoteBatteryLocation && remoteBatteryCharge > 0f;
            
            // If remote battery is here, keep puzzle active but skip original (which would reset it)
            if (remoteBatteryHere)
            {
                // Keep the puzzle lit up
                __instance.setMats(1);
                
                // Handle timer2 for loadPreset (only once)
                var timer2Field = typeof(PuzzleController).GetField("timer2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var puzzleCompletedField = typeof(PuzzleController).GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var presetIDField = typeof(PuzzleController).GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (timer2Field != null && puzzleCompletedField != null && presetIDField != null)
                {
                    int timer2 = (int)timer2Field.GetValue(__instance);
                    bool completed = (bool)puzzleCompletedField.GetValue(__instance);
                    int presetID = (int)presetIDField.GetValue(__instance);
                    
                    if (timer2 < 10) // Keep incrementing until stable
                    {
                        timer2++;
                        timer2Field.SetValue(__instance, timer2);
                        
                        if (timer2 == 5 && !completed && presetID > 0)
                        {
                            __instance.loadPreset(presetID);
                        }
                    }
                }
                
                return false; // Skip original - don't let it call resetBoard()
            }
            
            // Neither battery here - let original run (it will reset/turn off)
            return true;
        }
    }
    
    // Sync puzzle completion
    [HarmonyPatch(typeof(PuzzleController), "onWin")]
    public class PuzzleCompletePatch
    {
        static void Postfix(PuzzleController __instance)
        {
            // Send puzzle completion to other players
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning)
            {
                MPManager.Instance.PlayerSync.SendPuzzleComplete(__instance.thisPuzzleID);
            }
        }
    }
    
    // Sync puzzle block changes in real-time (throttled)
    [HarmonyPatch(typeof(PuzzleBlock), "setThisID")]
    public class PuzzleBlockPatch
    {
        // Throttle: track last send time per puzzle
        private static Dictionary<int, float> _lastBlockSendTime = new Dictionary<int, float>();
        private static float _throttleInterval = 0.05f; // 50ms between sends per puzzle
        
        static void Postfix(PuzzleBlock __instance, int input)
        {
            // Don't re-send if we're receiving from network
            if (MPManager.Instance?.PlayerSync?.IsReceivingPuzzleBlock == true)
                return;
            
            // Send block change to other players
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning)
            {
                int puzzleID = __instance.pcontrol.thisPuzzleID;
                
                // Throttle sends per puzzle
                float now = Time.time;
                if (_lastBlockSendTime.TryGetValue(puzzleID, out float lastTime))
                {
                    if (now - lastTime < _throttleInterval)
                        return; // Too soon, skip
                }
                _lastBlockSendTime[puzzleID] = now;
                
                MPManager.Instance.PlayerSync.SendPuzzleBlock(puzzleID, __instance.blockNumber, input);
            }
        }
    }
    
    // Sync clown nose honk
    [HarmonyPatch(typeof(clownNose), "checkHonk")]
    public class ClownHonkPatch
    {
        static void Prefix(clownNose __instance, out bool __state)
        {
            // Check if honk sound is playing before
            __state = __instance.honkSound != null && __instance.honkSound.isPlaying;
        }
        
        static void Postfix(clownNose __instance, bool __state)
        {
            // Don't re-send if we're receiving from network
            if (MPManager.Instance?.PlayerSync?.IsReceivingHonk == true)
                return;
            
            // Check if honk just started playing
            if (__instance.honkSound != null && __instance.honkSound.isPlaying && !__state)
            {
                if (MPManager.Instance?.Steam != null && 
                    MPManager.Instance.Steam.IsRunning)
                {
                    MPManager.Instance.PlayerSync.SendClownHonk();
                }
            }
        }
    }
    
    // Sync vent/crawl sounds so players can hear each other crawling
    [HarmonyPatch(typeof(ventSoundPlayer), "playRandomAudio")]
    public class VentSoundPatch
    {
        static void Postfix(ventSoundPlayer __instance)
        {
            // Don't re-send if we're receiving from network
            if (MPManager.Instance?.PlayerSync?.IsReceivingVentSound == true)
                return;
            
            // Send the sound to other players
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning)
            {
                // Get the spawn position that was just set
                Vector3 soundPos = __instance.spawnpos.position;
                MPManager.Instance.PlayerSync.SendVentSound(soundPos, 0);
                Plugin.Log.LogInfo($"[VentSound] Sent vent sound at {soundPos}");
            }
        }
    }
    
    // Also patch crawlSoundContrl for crawling sounds (haptic feedback triggers this)
    [HarmonyPatch(typeof(crawlSoundContrl), "playHaptic")]
    public class CrawlSoundPatch
    {
        static void Postfix(crawlSoundContrl __instance)
        {
            // Don't re-send if we're receiving from network
            if (MPManager.Instance?.PlayerSync?.IsReceivingVentSound == true)
                return;
            
            // Send the sound to other players
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning)
            {
                // Use the crawlSoundContrl's position
                Vector3 soundPos = __instance.transform.position;
                MPManager.Instance.PlayerSync.SendVentSound(soundPos, 1); // 1 = crawl sound
                Plugin.Log.LogInfo($"[CrawlSound] Sent crawl sound at {soundPos}");
            }
        }
    }
    
    // Lock puzzle interaction - one player at a time per puzzle
    [HarmonyPatch(typeof(PuzzleBlock), "OnTriggerStay")]
    public class PuzzleBlockLockPatch
    {
        static bool Prefix(PuzzleBlock __instance)
        {
            // Only apply lock in multiplayer
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            // Get puzzle ID for this block
            int puzzleId = __instance.pcontrol.thisPuzzleID;
            string lockId = $"puzzle_{puzzleId}";
            
            // Check if another player has the lock
            if (PlayerSync.IsLockedByOther(lockId))
            {
                // Another player is using this puzzle - block our interaction
                return false;
            }
            
            // Try to acquire/refresh lock (will auto-release after timeout)
            MPManager.Instance.PlayerSync.RefreshLock(lockId);
            return true;
        }
    }
    
    // NOTE: Crank lock removed - not needed since each player can only charge their own battery
    // The crank checks BackpackControl.batteryLocationID == 1 which is per-player
    
    // Override crank battery visual when remote player has battery in crank
    // The game's batteryScreenVisual() checks LOCAL battery location, but we need to show
    // the remote player's battery when THEY have it in the crank
    [HarmonyPatch(typeof(crankControl), "batteryScreenVisual")]
    public class CrankVisualSyncPatch
    {
        static void Postfix(crankControl __instance)
        {
            // Only apply in multiplayer
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return;
            
            // If LOCAL player has battery in crank (location 1), the game already handles it
            if (BackpackControl.batteryLocationID == 1)
                return;
            
            // Check if remote player has battery in crank
            var remoteState = MPManager.Instance.PlayerSync.GetFirstRemoteBatteryState();
            if (remoteState != null && remoteState.LocationID == 1)
            {
                // Remote player has battery in crank - show their battery visual
                if (__instance.batteryFill != null)
                {
                    __instance.batteryFill.fillAmount = remoteState.Charge / 55f;
                }
                if (__instance.batteryIMG != null)
                {
                    __instance.batteryIMG.SetActive(true);
                }
            }
        }
    }
    
    // Override battery zone visual when remote player has battery in that station
    // The game's visualSet() checks LOCAL battery location, but we need to show
    // the remote player's battery when THEY have it in that station
    [HarmonyPatch(typeof(batteryZoneCheck), "visualSet")]
    public class BatteryZoneVisualPatch
    {
        static void Postfix(batteryZoneCheck __instance)
        {
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return;
            
            if (BackpackControl.batteryLocationID == __instance.thisStationID)
                return;
            
            var remoteState = MPManager.Instance.PlayerSync?.GetFirstRemoteBatteryState();
            if (remoteState != null && remoteState.LocationID == __instance.thisStationID)
            {
                if (__instance.thisBatteryVisual != null)
                    __instance.thisBatteryVisual.SetActive(true);
            }
            else
            {
                // Remote player doesn't have battery here either - make sure it's hidden
                // (unless local player has it, which is handled above)
                if (__instance.thisBatteryVisual != null && BackpackControl.batteryLocationID != __instance.thisStationID)
                    __instance.thisBatteryVisual.SetActive(false);
            }
        }
    }
    
    // Sync vent door animations when a player triggers them
    [HarmonyPatch(typeof(VentAnimControl), "OnTriggerStay")]
    public class VentDoorSyncPatch
    {
        static void Postfix(VentAnimControl __instance, Collider other)
        {
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return;
            
            // Send the vent door trigger to other players
            int ventId = __instance.GetInstanceID();
            MPManager.Instance.PlayerSync.SendVentDoorTrigger(ventId, __instance.transform.position);
        }
    }
    
    // Block client from randomizing paintings - host controls which paintings are shown
    [HarmonyPatch(typeof(paintingControl), "setAllPaintings")]
    public class PaintingRandomBlockPatch
    {
        static bool Prefix()
        {
            var steam = MPManager.Instance?.Steam;
            if (steam != null && steam.IsInLobby && steam.IsConnected && !steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Blocked painting randomization - waiting for host sync");
                return false; // Block client from randomizing
            }
            return true;
        }
    }
    
    // Sync entity paintings (the scary versions) when host spawns them
    [HarmonyPatch(typeof(paintingControl), "setEntityPainting")]
    public class PaintingEntitySyncPatch
    {
        static void Postfix(paintingControl __instance)
        {
            var steam = MPManager.Instance?.Steam;
            if (steam == null || !steam.IsRunning || !steam.IsHost)
                return;
            
            // Send entity painting state to client
            MPManager.Instance.PlayerSync.SendPaintingEntityState(__instance);
        }
    }
    
    // Block placing battery in ANY slot if remote player already has their battery there
    [HarmonyPatch(typeof(BackpackControl), "placeBatteryCheck")]
    public class BlockBatteryDoublePlacePatch
    {
        static bool Prefix()
        {
            // Only apply in multiplayer
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            // Get the current zone we're trying to place in
            int targetZone = batteryZoneCheck.batteryZoneIDGlobal;
            if (targetZone <= 0) return true; // Not in a valid zone
            
            // Check if remote player has battery in this zone
            var remoteState = MPManager.Instance.PlayerSync.GetFirstRemoteBatteryState();
            if (remoteState != null && remoteState.LocationID == targetZone)
            {
                Plugin.Log.LogInfo($"[Battery] Blocked placing battery in zone {targetZone} - remote player already has battery there");
                return false; // Block the placement
            }
            
            return true;
        }
    }
    
    // Sync end scene progress - client doesn't progress independently
    [HarmonyPatch(typeof(EndControl), "isLooking")]
    public class EndControlSyncPatch
    {
        static bool Prefix()
        {
            // Only host progresses the end scene
            var steam = MPManager.Instance?.Steam;
            if (steam != null && steam.IsRunning && !steam.IsHost)
            {
                return false; // Client doesn't progress - gets synced from host
            }
            return true;
        }
    }
    
    // Detect player death and notify partner
    [HarmonyPatch(typeof(jumpscareController), "onDeathClown")]
    public class DeathClownPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            if (__instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance) is int deathID && deathID > 0)
                MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 1);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHarold")]
    public class DeathHaroldPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            if (__instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance) is int deathID && deathID > 0)
                MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 2);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSparky")]
    public class DeathSparkyPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            if (__instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance) is int deathID && deathID > 0)
                MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 3);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHenry")]
    public class DeathHenryPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            if (__instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance) is int deathID && deathID > 0)
                MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 4);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSmiley")]
    public class DeathSmileyPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            if (__instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance) is int deathID && deathID > 0)
                MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 5);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathJeff")]
    public class DeathJeffPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            if (__instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(__instance) is int deathID && deathID > 0)
                MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 6);
        }
    }
    
    // Add friend indicator to minimap
    [HarmonyPatch(typeof(MinimapControl), "setMapIconPos")]
    public class MinimapFriendPatch
    {
        private static GameObject _friendIndicator;
        private static MinimapControl _lastMinimapControl;
        
        // Call this to clean up on scene change
        public static void Cleanup()
        {
            if (_friendIndicator != null)
            {
                Object.Destroy(_friendIndicator);
                _friendIndicator = null;
            }
            _lastMinimapControl = null;
        }
        
        static void Postfix(MinimapControl __instance)
        {
            // Only if we're in multiplayer
            if (MPManager.Instance?.PlayerSync == null) return;
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning) return;
            
            // Only show friend indicator when minimap is actually visible
            // The minimap is visible when timer > 0 and minimap GameObject is active
            if (__instance.minimap == null || !__instance.minimap.activeSelf)
            {
                if (_friendIndicator != null)
                    _friendIndicator.SetActive(false);
                return;
            }
            
            // If minimap control changed (new scene), recreate indicator
            if (_lastMinimapControl != __instance)
            {
                Cleanup();
                _lastMinimapControl = __instance;
            }
            
            // Get remote player positions
            var remotePositions = MPManager.Instance.PlayerSync.GetRemotePlayerPositions();
            if (remotePositions.Count == 0)
            {
                if (_friendIndicator != null)
                    _friendIndicator.SetActive(false);
                return;
            }
            
            // Create friend indicator if it doesn't exist
            if (_friendIndicator == null)
            {
                // Clone the player indicator and make it a different color
                _friendIndicator = Object.Instantiate(__instance.playerIndicator, __instance.playerIndicator.transform.parent);
                _friendIndicator.name = "FriendIndicator";
                
                // Try to change color to cyan/blue to distinguish from player (green) and enemy (red)
                // Try SpriteRenderer first
                var spriteRenderer = _friendIndicator.GetComponent<SpriteRenderer>();
                if (spriteRenderer != null)
                {
                    spriteRenderer.color = UnityEngine.Color.cyan;
                }
                
                // Try regular Renderer
                var renderer = _friendIndicator.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                {
                    renderer.material.color = UnityEngine.Color.cyan;
                }
                
                // Try to find any Image component via reflection (to avoid needing UI assembly)
                foreach (var comp in _friendIndicator.GetComponents<Component>())
                {
                    var colorProp = comp.GetType().GetProperty("color");
                    if (colorProp != null && colorProp.PropertyType == typeof(UnityEngine.Color))
                    {
                        colorProp.SetValue(comp, UnityEngine.Color.cyan);
                        break;
                    }
                }
                
                Plugin.Log.LogInfo("Created friend indicator on minimap");
            }
            
            // Position the friend indicator using first remote player
            Vector3 friendWorldPos = remotePositions[0];
            Vector3 localPosition = new Vector3(
                friendWorldPos.x * 15.4f + __instance.xOffest, 
                friendWorldPos.z * 15.52f + __instance.yOffset, 
                0f
            );
            _friendIndicator.transform.localPosition = localPosition;
            _friendIndicator.SetActive(true);
        }
    }
    
    // ==================== GHOST DEATH HANDLING ====================
    // In multiplayer, when a player dies:
    // 1. The scene reloads (same Night level, not Home)
    // 2. Dead player teleports to alive player's position
    // 3. Dead player appears transparent to others (ghost)
    // 4. Ghost can move around but can't interact with puzzles/enemies
    
    // ==================== EXIT DOOR SYNC ====================
    // Patch LeaveDoorControl to work in multiplayer
    // The door should charge if EITHER player has battery at location 100
    // Progress is synced so both players see the same percentage
    [HarmonyPatch(typeof(LeaveDoorControl), "leaveCheck")]
    public class LeaveDoorCheckPatch
    {
        static bool Prefix(LeaveDoorControl __instance)
        {
            var steam = MPManager.Instance?.Steam;
            if (steam == null || !steam.IsRunning) return true;
            
            // Check if local player has battery at exit
            bool localAtExit = BackpackControl.batteryLocationID == 100 && BackpackControl.batteryCharge >= 0.1f;
            
            // Check if remote player has battery at exit
            bool remoteAtExit = MPManager.Instance.PlayerSync.IsRemoteBatteryAtExit();
            
            // If local player is at exit, run normal logic (it will charge and sync)
            if (localAtExit) return true;
            
            // If remote player is at exit but we're not, show the door as ready but don't charge locally
            // The remote player's charging progress will be synced to us
            if (remoteAtExit)
            {
                // Check puzzles completed
                if (PuzzleMaster.totalCompletedPuzzles != PuzzleMaster.requiredPuzzles)
                {
                    __instance.setNo();
                    return false;
                }
                
                // Show green (ready) state - the fill progress comes from sync
                __instance.setYes();
                return false; // Don't run local charging logic
            }
            
            // Neither player at exit - run default behavior
            return true;
        }
    }
    
    // ==================== GHOST SPECTATE SYSTEM ====================
    // When a player dies in multiplayer, instead of going to Home:
    // 1. Reload the current Night scene
    // 2. Teleport to partner's position
    // 3. Become a transparent ghost that can move around but not interact
    
    /// <summary>
    /// Intercept scene loading after death to reload Night scene instead of Home
    /// The jumpscareController coroutines call SceneManager.LoadScene("Home") after 0.8s
    /// We intercept this and reload the current Night scene instead
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), "LoadScene", new System.Type[] { typeof(string) })]
    public class GhostSceneInterceptPatch
    {
        static bool Prefix(ref string sceneName)
        {
            Plugin.Log.LogInfo($"[Ghost] SceneManager.LoadScene intercepted: sceneName={sceneName}, IsGhostSceneReload={PlayerSync.IsGhostSceneReload}");
            
            // Only intercept if we're in multiplayer and ghost reload is pending
            if (!PlayerSync.IsGhostSceneReload) return true;
            
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync == null)
            {
                Plugin.Log.LogWarning("[Ghost] PlayerSync is null, allowing normal scene load");
                return true;
            }
            
            // Check if this is trying to load Home after death
            if (sceneName.Equals("Home", System.StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogInfo($"[Ghost] INTERCEPTING Home load! Reloading Night scene instead");
                
                // Clear the flag and let PlayerSync handle the reload
                PlayerSync.IsGhostSceneReload = false;
                playerSync.ReloadSceneAsGhost();
                
                return false; // Don't load Home
            }
            
            Plugin.Log.LogInfo($"[Ghost] Not intercepting - sceneName is not Home");
            return true; // Allow other scene loads
        }
    }
    
    /// <summary>
    /// Re-enable world/hands after ghost scene reload
    /// The death coroutines disable worldMaster, leftHand, rightHand before loading Home
    /// We need to re-enable them after ghost reload
    /// </summary>
    [HarmonyPatch(typeof(jumpscareController), "Start")]
    public class GhostJumpscareStartPatch
    {
        static void Postfix(jumpscareController __instance)
        {
            // If we're a ghost reloading into the scene, make sure world is enabled
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] GhostJumpscareStartPatch - Re-enabling world after ghost reload");
                
                // Re-enable world objects that death coroutines disabled
                if (__instance.worldMaster != null)
                {
                    __instance.worldMaster.SetActive(true);
                    Plugin.Log.LogInfo("[Ghost]   worldMaster enabled");
                }
                if (__instance.leftHand != null)
                {
                    __instance.leftHand.SetActive(true);
                    Plugin.Log.LogInfo("[Ghost]   leftHand enabled");
                }
                if (__instance.rightHand != null)
                {
                    __instance.rightHand.SetActive(true);
                    Plugin.Log.LogInfo("[Ghost]   rightHand enabled");
                }
                    
                // Hide jumpscare elements
                if (__instance.bkgGO != null)
                    __instance.bkgGO.SetActive(false);
                if (__instance.staticScreen != null)
                    __instance.staticScreen.SetActive(false);
                if (__instance.sparkyGO != null)
                    __instance.sparkyGO.SetActive(false);
                if (__instance.haroldGO != null)
                    __instance.haroldGO.SetActive(false);
                if (__instance.smileyGO != null)
                    __instance.smileyGO.SetActive(false);
                if (__instance.jeffGO != null)
                    __instance.jeffGO.SetActive(false);
                if (__instance.henryGO != null)
                    __instance.henryGO.SetActive(false);
                if (__instance.clownJSDoll != null)
                    __instance.clownJSDoll.SetActive(false);
                    
                // Reset deathID to stop FixedUpdate animations
                var deathIDField = typeof(jumpscareController).GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (deathIDField != null)
                {
                    deathIDField.SetValue(__instance, 0);
                    Plugin.Log.LogInfo("[Ghost]   deathID reset to 0");
                }
                
                Plugin.Log.LogInfo("[Ghost] GhostJumpscareStartPatch complete");
            }
        }
    }
    
    /// <summary>
    /// Block ghost players from interacting with puzzles
    /// </summary>
    [HarmonyPatch(typeof(PuzzleBlock), "OnTriggerStay")]
    public class GhostPuzzleBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                return false; // Block puzzle interaction for ghosts
            }
            return true;
        }
    }
    
    /// <summary>
    /// Block ghost players from picking up batteries
    /// </summary>
    [HarmonyPatch(typeof(BackpackControl), "grabBattery")]
    public class GhostBatteryBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                return false; // Block battery pickup for ghosts
            }
            return true;
        }
    }
    
    /// <summary>
    /// Block ghost players from using the crank (charging battery)
    /// </summary>
    [HarmonyPatch(typeof(crankControl), "getGeneratedPower")]
    public class GhostCrankBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                return false; // Block crank interaction for ghosts
            }
            return true;
        }
    }
    
    // ==================== GHOST MONSTER IMMUNITY ====================
    // Ghosts can't be killed by monsters - they're already dead!
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathClown")]
    public class GhostDeathClownBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked death from Clown - ghosts are immune");
                return false;
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHarold")]
    public class GhostDeathHaroldBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked death from Harold - ghosts are immune");
                return false;
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSparky")]
    public class GhostDeathSparkyBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked death from Sparky - ghosts are immune");
                return false;
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHenry")]
    public class GhostDeathHenryBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked death from Henry - ghosts are immune");
                return false;
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSmiley")]
    public class GhostDeathSmileyBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked death from Smiley - ghosts are immune");
                return false;
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathJeff")]
    public class GhostDeathJeffBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked death from Jeff - ghosts are immune");
                return false;
            }
            return true;
        }
    }
    
    /// <summary>
    /// Block painting death for ghosts
    /// </summary>
    [HarmonyPatch(typeof(paintingControl), "killPlayer")]
    public class GhostPaintingDeathBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost)
            {
                Plugin.Log.LogInfo("[Ghost] Blocked painting death - ghosts are immune");
                return false;
            }
            return true;
        }
    }
}
