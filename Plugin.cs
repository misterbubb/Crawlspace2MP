using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Crawlspace2MP
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            
            Log.LogInfo($"Crawlspace 2 Multiplayer v{PluginInfo.PLUGIN_VERSION} loading...");
            
            // Apply Harmony patches
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            
            // Create persistent manager with all the networking
            var manager = new GameObject("Crawlspace2MP_Manager");
            var mpManager = manager.AddComponent<MPManager>();
            DontDestroyOnLoad(manager);
            manager.hideFlags = HideFlags.HideAndDontSave;
            
            Log.LogInfo("Multiplayer mod loaded!");
        }
    }

    // Persistent manager that holds everything
    public class MPManager : MonoBehaviour
    {
        public static MPManager Instance { get; private set; }
        
        public PlayerSync PlayerSync { get; private set; }
        public SteamTransport Steam { get; private set; }
        public VoiceChat VoiceChat { get; private set; }
        
        // State shortcuts
        public bool IsHost => Steam?.IsHost ?? false;
        public bool IsConnected => Steam?.IsConnected ?? false;
        public bool IsRunning => Steam?.IsRunning ?? false;
        public bool IsJoining => Steam?.IsJoining ?? false;
        
        private string _lobbyIdInput = "";
        private string _statusMessage = "Initializing Steam...";
        private string _connectedPlayerName = "";
        private Rect _windowRect = new Rect(10, 10, 320, 380);
        private float _lastLog = 0;
        private bool _steamInitialized = false;
        private float _copiedTime = -10f;  // Time when copy was clicked (-10 so it starts as "Copy")
        private bool _uiHidden = false;  // Toggle with Insert key for streamers
        private bool _showLobbyCode = false;  // Hidden by default for streamers

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
                
                // Color ping based on quality
                string pingColor = ping < 50 ? "green" : (ping < 100 ? "yellow" : "red");
                
                GUI.backgroundColor = new UnityEngine.Color(0f, 0.5f, 0f, 0.8f);
                GUI.contentColor = UnityEngine.Color.white;
                GUI.Box(new Rect(Screen.width - 160, 10, 150, 65), "");
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
            
            if (Steam.IsHost && Steam.IsInLobby)
            {
                Plugin.Log.LogInfo("Already hosting");
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
            VoiceChat?.Cleanup();
            PlayerSync?.Cleanup();
            Steam?.Shutdown();
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.crawlspace2.multiplayer";
        public const string PLUGIN_NAME = "Crawlspace2MP";
        public const string PLUGIN_VERSION = "0.1.0";
    }
    
    // Harmony patches to block client from controlling game flow
    [HarmonyPatch(typeof(calenderControl), "increaseValue")]
    public class CalendarIncreasePatch
    {
        static bool Prefix()
        {
            // Block if connected as client
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsConnected && 
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Calendar blocked - only host can select night");
                return false; // Skip original method
            }
            return true; // Allow original method
        }
    }
    
    [HarmonyPatch(typeof(calenderControl), "decreaseValue")]
    public class CalendarDecreasePatch
    {
        static bool Prefix()
        {
            // Block if connected as client
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsConnected && 
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Calendar blocked - only host can select night");
                return false;
            }
            return true;
        }
    }
    
    [HarmonyPatch(typeof(doorSceneChange), "OnTriggerEnter")]
    public class DoorPatch
    {
        static bool Prefix(doorSceneChange __instance, Collider other)
        {
            // Block if connected as client
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsConnected && 
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Door blocked - only host can start night");
                return false;
            }
            
            // If host, send scene change BEFORE the original method runs
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsHost &&
                MPManager.Instance.Steam.IsRunning)
            {
                // Try multiple possible field names for the scene
                string sceneName = null;
                var doorType = typeof(doorSceneChange);
                
                // Try common field names
                string[] possibleFields = { "sceneToLoad", "sceneName", "targetScene", "nextScene", "scene", "loadScene" };
                foreach (var fieldName in possibleFields)
                {
                    var field = doorType.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        sceneName = field.GetValue(__instance) as string;
                        if (!string.IsNullOrEmpty(sceneName))
                        {
                            Plugin.Log.LogInfo($"[Host] Found scene field '{fieldName}' = {sceneName}");
                            break;
                        }
                    }
                }
                
                // If still not found, try to find any string field
                if (string.IsNullOrEmpty(sceneName))
                {
                    foreach (var field in doorType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (field.FieldType == typeof(string))
                        {
                            string val = field.GetValue(__instance) as string;
                            Plugin.Log.LogInfo($"[Host] doorSceneChange string field: {field.Name} = '{val}'");
                            // Check if it looks like a scene name (contains "Night" or "Home" etc)
                            if (!string.IsNullOrEmpty(val) && (val.Contains("Night") || val.Contains("Home") || val.Contains("Intro")))
                            {
                                sceneName = val;
                                Plugin.Log.LogInfo($"[Host] Using field '{field.Name}' as scene name: {sceneName}");
                                break;
                            }
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(sceneName))
                {
                    Plugin.Log.LogInfo($"[Host] Door entered, sending scene change BEFORE load: {sceneName}");
                    MPManager.Instance.PlayerSync.SendSceneChange(sceneName);
                }
                else
                {
                    Plugin.Log.LogWarning("[Host] Could not find scene name field on doorSceneChange - listing all fields:");
                    foreach (var field in doorType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        Plugin.Log.LogWarning($"  Field: {field.Name} ({field.FieldType.Name})");
                    }
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
            // Block if connected as client
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsConnected && 
                !MPManager.Instance.Steam.IsHost)
            {
                Plugin.Log.LogInfo("[Client] Scene exit blocked - only host can complete night");
                return false;
            }
            
            // If host, send scene change BEFORE the original method runs
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsHost &&
                MPManager.Instance.Steam.IsRunning)
            {
                // Try multiple possible field names for the scene
                string sceneName = null;
                var leaveType = typeof(sceneLeave);
                
                string[] possibleFields = { "sceneToLoad", "sceneName", "targetScene", "nextScene", "scene", "loadScene" };
                foreach (var fieldName in possibleFields)
                {
                    var field = leaveType.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        sceneName = field.GetValue(__instance) as string;
                        if (!string.IsNullOrEmpty(sceneName))
                        {
                            Plugin.Log.LogInfo($"[Host] Found scene field '{fieldName}' = {sceneName}");
                            break;
                        }
                    }
                }
                
                // If still not found, try to find any string field that looks like a scene
                if (string.IsNullOrEmpty(sceneName))
                {
                    foreach (var field in leaveType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (field.FieldType == typeof(string))
                        {
                            string val = field.GetValue(__instance) as string;
                            if (!string.IsNullOrEmpty(val) && (val.Contains("Night") || val.Contains("Home") || val.Contains("Intro")))
                            {
                                sceneName = val;
                                Plugin.Log.LogInfo($"[Host] Using field '{field.Name}' as scene name: {sceneName}");
                                break;
                            }
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(sceneName))
                {
                    Plugin.Log.LogInfo($"[Host] Scene exit, sending scene change BEFORE load: {sceneName}");
                    MPManager.Instance.PlayerSync.SendSceneChange(sceneName);
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
    // Ignores ghost players - monsters don't target ghosts
    public static class MultiplayerTargeting
    {
        public static Vector3 GetClosestPlayerPosition(Vector3 monsterPos, GameObject localPlayer)
        {
            Vector3 closestPos = monsterPos; // Default to monster pos if no valid targets
            float closestDist = float.MaxValue;
            
            // Check local player (only if not a ghost)
            bool localIsGhost = MPManager.Instance?.PlayerSync?.IsLocalPlayerGhost ?? false;
            if (!localIsGhost && localPlayer != null)
            {
                Vector3 localPos = localPlayer.transform.position;
                float localDist = Vector3.Distance(monsterPos, localPos);
                if (localDist < closestDist)
                {
                    closestDist = localDist;
                    closestPos = localPos;
                }
            }
            
            // Check remote players (only non-ghosts)
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
    
    // Make Sparky target closest player (host only)
    [HarmonyPatch(typeof(sparkyBrain), "huntMode")]
    public class SparkyHuntPatch
    {
        static bool Prefix(sparkyBrain __instance)
        {
            // Only modify if we're in multiplayer as host
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            // Client: skip, positions synced
            if (!MPManager.Instance.Steam.IsHost)
                return false;
            
            __instance.sparkyscream.volume = 0.4f;
            Vector3 targetPos = MultiplayerTargeting.GetClosestPlayerPosition(__instance.transform.position, __instance.player);
            __instance.agent.SetDestination(targetPos);
            return false; // Skip original
        }
    }
    
    // Make Jeff target closest player (host only)
    [HarmonyPatch(typeof(jeffBrain), "huntMode")]
    public class JeffHuntPatch
    {
        static bool Prefix(jeffBrain __instance)
        {
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            if (!MPManager.Instance.Steam.IsHost)
                return false;
            
            Vector3 targetPos = MultiplayerTargeting.GetClosestPlayerPosition(__instance.transform.position, __instance.player);
            __instance.agent.SetDestination(targetPos);
            return false;
        }
    }
    
    // Make Henry target closest player (host only)
    [HarmonyPatch(typeof(henryBrain), "moveToPlayer")]
    public class HenryMovePatch
    {
        static bool Prefix(henryBrain __instance)
        {
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            if (!MPManager.Instance.Steam.IsHost)
                return false;
            
            // Access private field via reflection
            var resetSwitchField = typeof(henryBrain).GetField("resetSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool resetSwitch = (bool)resetSwitchField.GetValue(__instance);
            
            if (resetSwitch)
            {
                // Call moverandomposV2 via reflection
                var method = typeof(henryBrain).GetMethod("moverandomposV2", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                method?.Invoke(__instance, null);
                return false;
            }
            
            __instance.outOfBoundCheck();
            Vector3 targetPos = MultiplayerTargeting.GetClosestPlayerPosition(__instance.transform.position, __instance.player);
            __instance.agent.SetDestination(targetPos);
            return false;
        }
    }
    
    // Make Smile target closest player
    [HarmonyPatch(typeof(SmileBrain), "moveToPlayer")]
    public class SmileMovePatch
    {
        static bool Prefix(SmileBrain __instance)
        {
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            // Client: skip movement, positions are synced
            if (!MPManager.Instance.Steam.IsHost)
                return false;
            
            Vector3 targetPos = MultiplayerTargeting.GetClosestPlayerPosition(__instance.transform.position, __instance.player);
            Vector3 noYVector = new Vector3(targetPos.x, __instance.transform.position.y, targetPos.z);
            
            // Access private chaseTime field
            var chaseTimeField = typeof(SmileBrain).GetField("chaseTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            int chaseTime = (int)chaseTimeField.GetValue(__instance);
            
            if (chaseTime < 60)
            {
                __instance.transform.position = Vector3.MoveTowards(__instance.transform.position, noYVector, __instance.smileSpeed * 2f);
                
                // Access and set audioSwitch
                var audioSwitchField = typeof(SmileBrain).GetField("audioSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool audioSwitch = (bool)audioSwitchField.GetValue(__instance);
                if (!audioSwitch)
                {
                    __instance.playAudio2();
                    audioSwitchField.SetValue(__instance, true);
                }
            }
            else
            {
                __instance.transform.position = Vector3.MoveTowards(__instance.transform.position, noYVector, __instance.smileSpeed);
            }
            
            return false;
        }
    }
    
    // Disable Sparky AI on client (positions synced from host)
    [HarmonyPatch(typeof(sparkyBrain), "playerDistFuncStateControl")]
    public class SparkyClientPatch
    {
        static bool Prefix(sparkyBrain __instance)
        {
            // Skip AI on client - positions are synced
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                // Ghost players are immune to death
                if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost)
                    return false;
                
                // Still check for kill on client
                float dist = Vector3.Distance(__instance.player.transform.position, __instance.transform.position);
                if (dist < 0.5f && !earMaster.isCoveringEars)
                {
                    __instance.jsc.onDeathSparky();
                }
                return false;
            }
            return true;
        }
    }
    
    // Disable Jeff AI on client
    [HarmonyPatch(typeof(jeffBrain), "playerDistFuncStateControl")]
    public class JeffClientPatch
    {
        static bool Prefix(jeffBrain __instance)
        {
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                // Ghost players are immune to death
                if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost)
                    return false;
                
                // Still check for kill on client when Jeff is staring (state 4)
                // Access jeffState via reflection
                var stateField = typeof(jeffBrain).GetField("jeffState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (stateField != null)
                {
                    int state = (int)stateField.GetValue(__instance);
                    if (state == 4) // Staring state
                    {
                        float dist = Vector3.Distance(__instance.player.transform.position, __instance.transform.position);
                        if (dist < 0.6f)
                        {
                            __instance.jsc.onDeathJeff();
                        }
                    }
                }
                return false;
            }
            return true;
        }
    }
    
    // Disable Henry AI on client
    [HarmonyPatch(typeof(henryBrain), "playerDistFuncStateControl")]
    public class HenryClientPatch
    {
        static bool Prefix(henryBrain __instance)
        {
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                // Ghost players are immune to death
                if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost)
                    return false;
                
                // Still check for kill on client
                float dist = Vector3.Distance(__instance.player.transform.position, __instance.transform.position);
                if (dist < 0.5f)
                {
                    __instance.jsc.onDeathHenry();
                }
                return false;
            }
            return true;
        }
    }
    
    // Disable Harold AI on client
    [HarmonyPatch(typeof(mapEnBrain), "playerDistFunc")]
    public class HaroldClientPatch
    {
        static bool Prefix(mapEnBrain __instance)
        {
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                // Ghost players are immune to death
                if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost)
                    return false;
                
                // Still check for kill on client
                float dist = Vector3.Distance(__instance.player.transform.position, __instance.transform.position);
                if (dist < 0.6f)
                {
                    __instance.jsc.onDeathHarold();
                }
                return false;
            }
            return true;
        }
    }
    
    // Disable Smile AI on client
    [HarmonyPatch(typeof(SmileBrain), "playerDistFuncStateControl")]
    public class SmileClientPatch
    {
        static bool Prefix(SmileBrain __instance)
        {
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                !MPManager.Instance.Steam.IsHost)
            {
                // Ghost players are immune to death
                if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost)
                    return false;
                
                // Still check for kill on client
                float dist = Vector3.Distance(__instance.player.transform.position, __instance.transform.position);
                if (dist < 0.5f)
                {
                    __instance.jsc.onDeathSmiley();
                }
                return false;
            }
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
    
    // Lock crank interaction - one player at a time
    [HarmonyPatch(typeof(CrankHandle), "OnTriggerStay")]
    public class CrankLockPatch
    {
        static bool Prefix()
        {
            // Only apply lock in multiplayer
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            string lockId = "crank";
            
            // Check if another player has the lock
            if (PlayerSync.IsLockedByOther(lockId))
            {
                // Another player is using the crank - block our interaction
                return false;
            }
            
            // Try to acquire/refresh lock
            MPManager.Instance.PlayerSync.RefreshLock(lockId);
            return true;
        }
    }
    
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
            // Only apply in multiplayer
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return;
            
            // If LOCAL player has battery in this station, the game already handles it
            if (BackpackControl.batteryLocationID == __instance.thisStationID)
                return;
            
            // Check if remote player has battery in this station
            var remoteState = MPManager.Instance.PlayerSync.GetFirstRemoteBatteryState();
            if (remoteState != null && remoteState.LocationID == __instance.thisStationID)
            {
                // Remote player has battery in this station - show the battery visual
                if (__instance.thisBatteryVisual != null)
                {
                    __instance.thisBatteryVisual.SetActive(true);
                }
            }
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
    
    // ==================== DEATH / GHOST SYSTEM PATCHES ====================
    
    // Helper class to manage ghost death interception
    public static class GhostDeathHelper
    {
        // Intercept death and become ghost instead of going to Home
        public static bool InterceptDeath(jumpscareController jsc, int deathType)
        {
            // Only intercept in multiplayer
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return false; // Let normal death happen
            
            // Already a ghost? Completely block - ghosts can't die
            if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost)
            {
                Plugin.Log.LogInfo($"[Ghost] Blocked death - already a ghost");
                return true; // Block the death completely
            }
            
            Plugin.Log.LogInfo($"[Ghost] Intercepting death type {deathType}, will become ghost after jumpscare");
            
            // Mark as ghost BEFORE the jumpscare plays (so we don't die again during it)
            MPManager.Instance.PlayerSync.OnLocalPlayerDeath(deathType);
            
            // Let the jumpscare play! Start a coroutine to respawn after it finishes
            MPManager.Instance.StartCoroutine(RespawnAfterJumpscare(jsc, deathType));
            
            // Return FALSE to let the original death method run (plays jumpscare)
            // But we've already marked ourselves as ghost, so the scene load at the end will be blocked
            return false;
        }
        
        private static System.Collections.IEnumerator RespawnAfterJumpscare(jumpscareController jsc, int deathType)
        {
            // Wait for jumpscare to play (they're typically 2-4 seconds)
            yield return new WaitForSeconds(3.5f);
            
            Plugin.Log.LogInfo($"[Ghost] Jumpscare finished, becoming ghost (no teleport)");
            
            // Re-enable world so there's a floor to stand on
            if (jsc.worldMaster != null) 
            {
                jsc.worldMaster.SetActive(true);
                Plugin.Log.LogInfo($"[Ghost] Re-enabled worldMaster");
            }
            
            // Re-enable hands
            if (jsc.leftHand != null) jsc.leftHand.SetActive(true);
            if (jsc.rightHand != null) jsc.rightHand.SetActive(true);
            
            // Hide jumpscare elements
            if (jsc.bkgGO != null) jsc.bkgGO.SetActive(false);
            if (jsc.sparkyGO != null) jsc.sparkyGO.SetActive(false);
            if (jsc.haroldGO != null) jsc.haroldGO.SetActive(false);
            if (jsc.smileyGO != null) jsc.smileyGO.SetActive(false);
            if (jsc.jeffGO != null) jsc.jeffGO.SetActive(false);
            if (jsc.henryGO != null) jsc.henryGO.SetActive(false);
            if (jsc.clownJSDoll != null) jsc.clownJSDoll.SetActive(false);
            if (jsc.staticScreen != null) jsc.staticScreen.SetActive(false);
            
            // Reset deathID so the game doesn't get stuck
            var deathIDField = typeof(jumpscareController).GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                deathIDField.SetValue(jsc, 0);
            }
            
            // Reset monsters so they go back to hunting the other player
            ResetMonsters();
            
            // DON'T teleport - just let the player stay where they are
            // The scene load to Home is already blocked by BlockGhostSceneLoadPatch
            Plugin.Log.LogInfo($"[Ghost] Player is now a ghost, staying in place");
            
            // Wait a moment then check if we need to end the game
            yield return new WaitForSeconds(0.5f);
            
            // Check if ALL players are now ghosts - if so, end the game
            if (MPManager.Instance.PlayerSync.AreAllPlayersGhosts())
            {
                Plugin.Log.LogInfo($"[Ghost] ALL players are ghosts - ending level!");
                yield return new WaitForSeconds(2f);
                
                // Host sends scene change, then loads
                if (MPManager.Instance.Steam.IsHost)
                {
                    MPManager.Instance.PlayerSync.SendSceneChange("Home");
                }
                
                // Force load Home (bypass our block since all are ghosts)
                MPManager.Instance.PlayerSync.ForceLoadScene("Home");
            }
        }
        
        // Reset all monsters to their normal hunting state after a ghost death
        private static void ResetMonsters()
        {
            Plugin.Log.LogInfo("[Ghost] Resetting monsters after death");
            
            // Reset Henry
            var henry = Object.FindObjectOfType<henryBrain>();
            if (henry != null)
            {
                // Re-enable NavMeshAgent if disabled
                if (henry.agent != null && !henry.agent.enabled)
                {
                    henry.agent.enabled = true;
                }
                // Reset state via reflection
                var stateField = typeof(henryBrain).GetField("henryState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (stateField != null)
                {
                    stateField.SetValue(henry, 0); // Reset to initial state
                }
                Plugin.Log.LogInfo("[Ghost] Reset Henry");
            }
            
            // Reset Sparky
            var sparky = Object.FindObjectOfType<sparkyBrain>();
            if (sparky != null)
            {
                if (sparky.agent != null && !sparky.agent.enabled)
                {
                    sparky.agent.enabled = true;
                }
                var stateField = typeof(sparkyBrain).GetField("sparkyState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (stateField != null)
                {
                    stateField.SetValue(sparky, 0);
                }
                Plugin.Log.LogInfo("[Ghost] Reset Sparky");
            }
            
            // Reset Harold
            var harold = Object.FindObjectOfType<mapEnBrain>();
            if (harold != null)
            {
                if (harold.agent != null && !harold.agent.enabled)
                {
                    harold.agent.enabled = true;
                }
                var stateField = typeof(mapEnBrain).GetField("haroldState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (stateField != null)
                {
                    stateField.SetValue(harold, 0);
                }
                Plugin.Log.LogInfo("[Ghost] Reset Harold");
            }
            
            // Reset Jeff
            var jeff = Object.FindObjectOfType<jeffBrain>();
            if (jeff != null)
            {
                if (jeff.agent != null && !jeff.agent.enabled)
                {
                    jeff.agent.enabled = true;
                }
                var stateField = typeof(jeffBrain).GetField("jeffState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (stateField != null)
                {
                    stateField.SetValue(jeff, 0);
                }
                // Make sure Jeff's body is visible again
                if (jeff.jeffBody != null)
                {
                    jeff.jeffBody.SetActive(true);
                }
                Plugin.Log.LogInfo("[Ghost] Reset Jeff");
            }
            
            // Reset Smiley
            var smile = Object.FindObjectOfType<SmileBrain>();
            if (smile != null)
            {
                var stateField = typeof(SmileBrain).GetField("smileState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (stateField != null)
                {
                    stateField.SetValue(smile, 0);
                }
                Plugin.Log.LogInfo("[Ghost] Reset Smiley");
            }
            
            // Reset Clown
            var clown = Object.FindObjectOfType<clownRandom>();
            if (clown != null)
            {
                // Clown uses a different system - just make sure it's not in attack mode
                var attackField = typeof(clownRandom).GetField("isAttacking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (attackField != null)
                {
                    attackField.SetValue(clown, false);
                }
                Plugin.Log.LogInfo("[Ghost] Reset Clown");
            }
        }
    }
    
    // Patch all death methods to intercept and become ghost
    [HarmonyPatch(typeof(jumpscareController), "onDeathSparky")]
    public class DeathSparkyPatch
    {
        static bool Prefix(jumpscareController __instance)
        {
            return !GhostDeathHelper.InterceptDeath(__instance, 3);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHarold")]
    public class DeathHaroldPatch
    {
        static bool Prefix(jumpscareController __instance)
        {
            return !GhostDeathHelper.InterceptDeath(__instance, 2);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHenry")]
    public class DeathHenryPatch
    {
        static bool Prefix(jumpscareController __instance)
        {
            return !GhostDeathHelper.InterceptDeath(__instance, 4);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSmiley")]
    public class DeathSmileyPatch
    {
        static bool Prefix(jumpscareController __instance)
        {
            return !GhostDeathHelper.InterceptDeath(__instance, 5);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathJeff")]
    public class DeathJeffPatch
    {
        static bool Prefix(jumpscareController __instance)
        {
            return !GhostDeathHelper.InterceptDeath(__instance, 6);
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathClown")]
    public class DeathClownPatch
    {
        static bool Prefix(jumpscareController __instance)
        {
            return !GhostDeathHelper.InterceptDeath(__instance, 10);
        }
    }
    
    // Block scene loads to Home when we're a ghost (but not all players are ghosts)
    // This prevents the jumpscare from sending us back to Home
    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), "LoadScene", new System.Type[] { typeof(string) })]
    public class BlockGhostSceneLoadPatch
    {
        static bool Prefix(string sceneName)
        {
            // Allow if force flag is set (used when all players are ghosts)
            if (PlayerSync.ForceSceneLoadAllowed)
            {
                Plugin.Log.LogInfo($"[Ghost] Force scene load allowed: {sceneName}");
                return true;
            }
            
            // Only block in multiplayer when we're a ghost
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true; // Not in multiplayer, allow
            
            if (MPManager.Instance?.PlayerSync == null)
                return true;
            
            // If we're a ghost and trying to load Home, block it (unless ALL players are ghosts)
            if (MPManager.Instance.PlayerSync.IsLocalPlayerGhost && 
                sceneName.Equals("Home", System.StringComparison.OrdinalIgnoreCase))
            {
                // Check if ALL players are ghosts - if so, allow the load
                if (MPManager.Instance.PlayerSync.AreAllPlayersGhosts())
                {
                    Plugin.Log.LogInfo($"[Ghost] All players are ghosts, allowing scene load to Home");
                    return true;
                }
                
                // Block the load - we're a ghost but partner is still alive
                Plugin.Log.LogInfo($"[Ghost] Blocked scene load to Home - still a ghost, partner alive");
                return false;
            }
            
            return true; // Allow other scene loads
        }
    }
}
