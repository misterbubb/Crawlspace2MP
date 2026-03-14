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
            
            // CRITICAL: Validate reflection before doing anything else
            ValidateReflection();
            
            LoadCustomAssets();
            
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            
            var manager = new GameObject("Crawlspace2MP_Manager");
            manager.AddComponent<MPManager>();
            DontDestroyOnLoad(manager);
            manager.hideFlags = HideFlags.HideAndDontSave;
            
            Log.LogInfo("Multiplayer mod loaded!");
        }
        
        private void ValidateReflection()
        {
            var failures = new List<string>();
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            
            // Enemy AI fields
            if (typeof(henryBrain).GetField("resetSwitch", flags) == null)
                failures.Add("henryBrain.resetSwitch");
            if (typeof(henryBrain).GetField("chaseSwitch", flags) == null)
                failures.Add("henryBrain.chaseSwitch");
            if (typeof(sparkyBrain).GetField("currentState", flags) == null)
                failures.Add("sparkyBrain.currentState");
            if (typeof(jeffBrain).GetField("currentState", flags) == null)
                failures.Add("jeffBrain.currentState");
            if (typeof(jeffBrain).GetField("totalFlashes", flags) == null)
                failures.Add("jeffBrain.totalFlashes");
            if (typeof(SmileBrain).GetField("isChasing", flags) == null)
                failures.Add("SmileBrain.isChasing");
            if (typeof(SmileBrain).GetField("chaseTime", flags) == null)
                failures.Add("SmileBrain.chaseTime");
            if (typeof(clownRandom).GetField("clownAttackingSwitch", flags) == null)
                failures.Add("clownRandom.clownAttackingSwitch");
            if (typeof(clownRandom).GetField("clownKillTimer", flags) == null)
                failures.Add("clownRandom.clownKillTimer");
            
            // Puzzle fields
            if (typeof(PuzzleController).GetField("puzzleHasCompleted", flags) == null)
                failures.Add("PuzzleController.puzzleHasCompleted");
            if (typeof(PuzzleController).GetField("puzzlePresetID", flags) == null)
                failures.Add("PuzzleController.puzzlePresetID");
            if (typeof(PuzzleController).GetField("timer2", flags) == null)
                failures.Add("PuzzleController.timer2");
            if (typeof(PuzzleController).GetField("timer", flags) == null)
                failures.Add("PuzzleController.timer");
            if (typeof(PuzzleController).GetField("handColorID", flags) == null)
                failures.Add("PuzzleController.handColorID");
            if (typeof(PuzzleController).GetField("originCubeID", flags) == null)
                failures.Add("PuzzleController.originCubeID");
            
            // Painting fields
            if (typeof(paintingControl).GetField("boolpaintingTall1", flags) == null)
                failures.Add("paintingControl.boolpaintingTall1");
            if (typeof(paintingControl).GetField("boolpaintingTall2", flags) == null)
                failures.Add("paintingControl.boolpaintingTall2");
            if (typeof(paintingControl).GetField("boolpaintingTall3", flags) == null)
                failures.Add("paintingControl.boolpaintingTall3");
            if (typeof(paintingControl).GetField("boolpaintingSquare1", flags) == null)
                failures.Add("paintingControl.boolpaintingSquare1");
            if (typeof(paintingControl).GetField("boolpaintingSquare2", flags) == null)
                failures.Add("paintingControl.boolpaintingSquare2");
            if (typeof(paintingControl).GetField("boolpaintingSquare3", flags) == null)
                failures.Add("paintingControl.boolpaintingSquare3");
            
            // Public fields that should exist
            if (typeof(PuzzleBlock).GetField("blockIDValue") == null)
                failures.Add("PuzzleBlock.blockIDValue");
            if (typeof(BackpackControl).GetField("batteryLocationID") == null)
                failures.Add("BackpackControl.batteryLocationID (static)");
            if (typeof(BackpackControl).GetField("batteryCharge") == null)
                failures.Add("BackpackControl.batteryCharge (static)");
            if (typeof(PuzzleMaster).GetField("totalCompletedPuzzles") == null)
                failures.Add("PuzzleMaster.totalCompletedPuzzles (static)");
            if (typeof(earMaster).GetField("isCoveringEars") == null)
                failures.Add("earMaster.isCoveringEars (static)");
            
            // Minimap fields (ghost minimap)
            if (typeof(MinimapControl).GetField("timer", flags) == null)
                failures.Add("MinimapControl.timer");
            if (typeof(MinimapControl).GetField("haroldTimer", flags) == null)
                failures.Add("MinimapControl.haroldTimer");
            
            if (failures.Count > 0)
            {
                Log.LogError($"╔═══════════════════════════════════════════════════════════════╗");
                Log.LogError($"║ CRITICAL: Reflection validation failed for {failures.Count,2} field(s)          ║");
                Log.LogError($"╠═══════════════════════════════════════════════════════════════╣");
                foreach (var f in failures)
                    Log.LogError($"║   ✗ {f,-57} ║");
                Log.LogError($"╠═══════════════════════════════════════════════════════════════╣");
                Log.LogError($"║ The game may have updated. Multiplayer features WILL break.  ║");
                Log.LogError($"║ Please report this to the mod developer.                      ║");
                Log.LogError($"╚═══════════════════════════════════════════════════════════════╝");
            }
            else
            {
                Log.LogInfo("✓ Reflection validation passed - all fields found");
            }
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
                
                if (helmet != null && visor != null)
                {
                    // Both assets loaded successfully - create combined prefab
                    HelmetPrefab = helmet;
                    var visorInstance = Instantiate(visor);
                    visorInstance.transform.SetParent(helmet.transform, false);
                    visorInstance.transform.localPosition = new Vector3(0f, -0.11f, 0.211f);
                    visorInstance.transform.localRotation = Quaternion.identity;
                    visorInstance.name = "VR_Visor";
                    Log.LogInfo("✓ Loaded helmet with visor (SM_Kaska_LP + VR)");
                }
                else if (helmet != null)
                {
                    // Only helmet loaded
                    HelmetPrefab = helmet;
                    Log.LogWarning("Loaded helmet only (visor missing from asset bundle)");
                }
                else if (visor != null)
                {
                    // Only visor loaded
                    HelmetPrefab = visor;
                    Log.LogWarning("Loaded visor only (helmet missing from asset bundle)");
                }
                else
                {
                    // Neither loaded
                    Log.LogError("Failed to load helmet or visor from asset bundle - remote players will use fallback visuals");
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
        public VoiceChat VoiceChat { get; private set; } // Disabled - experimental, will be replaced
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
        private bool _steamInitialized = false;
        private float _copiedTime = -10f;  // Time when copy was clicked (-10 so it starts as "Copy")
        private bool _uiHidden = false;  // Toggle with Insert key for streamers
        private bool _showLobbyCode = false;  // Hidden by default for streamers
        private bool _showFriendsList = false;  // Toggle friends list
        private Vector2 _friendsScrollPos = Vector2.zero;  // Scroll position for friends list
        private float _lastFriendsRefresh = 0f;  // Last time friends list was refreshed
        private List<SteamTransport.FriendGameInfo> _cachedFriends = new List<SteamTransport.FriendGameInfo>();
        
        // ===== Trailer staging tools (F1/F2/F3) =====
#if DEBUG
        private GameObject _stagedHenry = null;
        private GameObject _stagedSparky = null;
        private Vector3 _sparkySpawnPos;
        private Vector3 _sparkyTargetPos;
        private bool _sparkyLunging = false;
        private float _sparkyLungeT = 0f;
        private const float SPARKY_LUNGE_DURATION = 0.4f;
        private const float SPARKY_SPAWN_DISTANCE = 0.9144f; // 3 feet in meters
        
        // ===== Debug: F4 disables all entities, all vents need repair =====
        public static bool DebugEntitiesDisabled { get; private set; } = false;
        
        // ===== Actor recording system for trailer creation =====
        public ActorRecorder ActorRecorder { get; private set; } = new ActorRecorder();
        private Vector2 _actorScrollPos = Vector2.zero;
#endif

        private void Awake()
        {
            Instance = this;
            
            // Create Steam networking
            Steam = new SteamTransport();
            
            PlayerSync = new PlayerSync();
            PlayerSync.Initialize(Steam);
            
            // Voice chat disabled for now - experimental and buggy
            // VoiceChat = new VoiceChat();
            // VoiceChat.Initialize(Steam);
            
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
            
            // Clean up actors on scene change (their GameObjects get destroyed anyway)
#if DEBUG
            ActorRecorder?.RemoveAllActors();
            if (ActorRecorder != null && ActorRecorder.IsRecording)
                ActorRecorder.StopRecording();
            
            // Reset debug mode on scene change
            DebugEntitiesDisabled = false;
            _stagedHenry = null;
            _stagedSparky = null;
            _sparkyLunging = false;
#endif
            
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
            // Insert key toggles UI visibility (for streamers/recording)
            // Check both new and old Input System for desktop compatibility
            var keyboard = Keyboard.current;
            bool insertPressed = keyboard != null && keyboard.insertKey.wasPressedThisFrame;
            if (!insertPressed)
            {
                try { insertPressed = Input.GetKeyDown(KeyCode.Insert); } catch { }
            }
            if (insertPressed)
            {
                _uiHidden = !_uiHidden;
            }
            
            // ===== Trailer staging hotkeys =====
#if DEBUG
            if (keyboard != null)
            {
                // F1 — Spawn/move Henry at player head position (frozen, harmless)
                if (keyboard.f1Key.wasPressedThisFrame)
                {
                    SpawnStagedHenry();
                }
                
                // F2 — Spawn/move Sparky 3ft in front of player (frozen, harmless)
                if (keyboard.f2Key.wasPressedThisFrame)
                {
                    SpawnStagedSparky();
                }
                
                // F3 — Sparky lunges forward 3ft toward player, then resets
                if (keyboard.f3Key.wasPressedThisFrame && _stagedSparky != null && !_sparkyLunging)
                {
                    _sparkyLunging = true;
                    _sparkyLungeT = 0f;
                }
                
                // F4 — Toggle debug mode: disable all entities + all 9 vents need repair
                if (keyboard.f4Key.wasPressedThisFrame)
                {
                    DebugEntitiesDisabled = !DebugEntitiesDisabled;
                    Plugin.Log.LogInfo($"[Debug] Entities disabled: {DebugEntitiesDisabled}");
                    
                    if (DebugEntitiesDisabled)
                    {
                        DisableAllEntities();
                        ForceAllPuzzlesActive();
                    }
                    else
                    {
                        EnableAllEntities();
                        // Stop recording if active
                        if (ActorRecorder.IsRecording)
                            ActorRecorder.StopRecording();
                    }
                }
                
                // F5 — Play all actors
                if (keyboard.f5Key.wasPressedThisFrame && ActorRecorder.Actors.Count > 0)
                {
                    foreach (var actor in ActorRecorder.Actors)
                    {
                        actor.IsPlaying = true;
                        actor.CurrentFrame = 0;
                        actor.FrameTimer = 0f;
                    }
                    Plugin.Log.LogInfo($"[Actor] Playing all {ActorRecorder.Actors.Count} actors");
                }
                
                // Animate Sparky lunge
                if (_sparkyLunging && _stagedSparky != null)
                {
                    _sparkyLungeT += Time.deltaTime / SPARKY_LUNGE_DURATION;
                    if (_sparkyLungeT >= 1f)
                    {
                        // Lunge complete — reset to spawn position
                        _stagedSparky.transform.position = _sparkySpawnPos;
                        _sparkyLunging = false;
                    }
                    else
                    {
                        // Lerp from spawn to target
                        _stagedSparky.transform.position = Vector3.Lerp(_sparkySpawnPos, _sparkyTargetPos, _sparkyLungeT);
                    }
                }
            }
#endif
            
            // Poll Steam events
            Steam?.Update();
            PlayerSync?.Update();
            // VoiceChat?.Update(); // Disabled
            Spectate?.Update();
            
            // Actor recorder — capture frames and update playback
#if DEBUG
            if (ActorRecorder.IsRecording)
            {
                ActorRecorder.CaptureFrame();
            }
            ActorRecorder.Update();
#endif
        }
        
#if DEBUG
        private void SpawnStagedHenry()
        {
            var henry = Object.FindObjectOfType<henryBrain>(true);
            if (henry == null)
            {
                Plugin.Log.LogWarning("[Trailer] No Henry found in scene");
                return;
            }
            
            // Make sure the GameObject is active (debug mode may have hidden it)
            henry.gameObject.SetActive(true);
            
            // Disable AI completely
            henry.enabled = false;
            var agent = henry.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null) agent.enabled = false;
            
            // Move to player head position
            Camera cam = Camera.main;
            if (cam != null)
            {
                henry.transform.position = cam.transform.position;
            }
            
            // Force Henry visible — clear resetSwitch so setimage() won't hide him,
            // and directly set the material
            var resetField = typeof(henryBrain).GetField("resetSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (resetField != null) resetField.SetValue(henry, false);
            if (henry.sparkyJS != null && henry.sp1 != null)
            {
                henry.sparkyJS.material = henry.sp1;
            }
            
            _stagedHenry = henry.gameObject;
            Plugin.Log.LogInfo("[Trailer] Henry staged at player position");
        }
        
        private void SpawnStagedSparky()
        {
            var sparky = Object.FindObjectOfType<sparkyBrain>(true);
            if (sparky == null)
            {
                Plugin.Log.LogWarning("[Trailer] No Sparky found in scene");
                return;
            }
            
            // Make sure the GameObject is active (debug mode may have hidden it)
            sparky.gameObject.SetActive(true);
            
            // Disable AI completely
            sparky.enabled = false;
            var agent = sparky.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null) agent.enabled = false;
            
            // Get player head position and forward direction
            Camera cam = Camera.main;
            if (cam == null) return;
            
            Vector3 playerPos = cam.transform.position;
            Vector3 forward = cam.transform.forward;
            forward.y = 0; // Keep on same height plane
            forward.Normalize();
            
            // Spawn 3ft in front of player
            _sparkySpawnPos = playerPos + forward * SPARKY_SPAWN_DISTANCE;
            _sparkySpawnPos.y = sparky.transform.position.y; // Keep original Y (floor level)
            _sparkyTargetPos = playerPos;
            _sparkyTargetPos.y = sparky.transform.position.y;
            
            sparky.transform.position = _sparkySpawnPos;
            
            // Face toward the player
            Vector3 lookDir = playerPos - _sparkySpawnPos;
            lookDir.y = 0;
            if (lookDir.sqrMagnitude > 0.001f)
            {
                sparky.transform.rotation = Quaternion.LookRotation(lookDir);
            }
            
            // Force Sparky visible — set currentState=2 so setimage() shows him,
            // and directly set the material
            var stateField = typeof(sparkyBrain).GetField("currentState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (stateField != null) stateField.SetValue(sparky, 2);
            if (sparky.sparkyJS != null && sparky.sp1 != null)
            {
                sparky.sparkyJS.material = sparky.sp1;
            }
            
            _stagedSparky = sparky.gameObject;
            _sparkyLunging = false;
            Plugin.Log.LogInfo("[Trailer] Sparky staged 3ft in front of player");
        }
        
        private void DisableAllEntities()
        {
            // Disable monster AI and movement
            // Use includeInactive=true so we can find entities that were already hidden
            var sparky = Object.FindObjectOfType<sparkyBrain>(true);
            if (sparky != null)
            {
                sparky.enabled = false;
                var agent = sparky.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = false;
                // Hide unless staged
                if (_stagedSparky == null) sparky.gameObject.SetActive(false);
            }
            
            var jeff = Object.FindObjectOfType<jeffBrain>(true);
            if (jeff != null)
            {
                jeff.enabled = false;
                var agent = jeff.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = false;
                jeff.gameObject.SetActive(false);
            }
            
            var henry = Object.FindObjectOfType<henryBrain>(true);
            if (henry != null)
            {
                henry.enabled = false;
                var agent = henry.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = false;
                // Hide unless staged
                if (_stagedHenry == null) henry.gameObject.SetActive(false);
            }
            
            var harold = Object.FindObjectOfType<mapEnBrain>(true);
            if (harold != null)
            {
                harold.enabled = false;
                var agent = harold.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = false;
                harold.gameObject.SetActive(false);
            }
            
            var smile = Object.FindObjectOfType<SmileBrain>(true);
            if (smile != null)
            {
                smile.enabled = false;
                var agent = smile.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = false;
                smile.gameObject.SetActive(false);
            }
            
            // Disable all clown instances
            foreach (var clown in Object.FindObjectsOfType<clownRandom>(true))
                clown.gameObject.SetActive(false);
            
            // Disable painting entity spawns (prevent painting kills)
            var painting = Object.FindObjectOfType<paintingControl>(true);
            if (painting != null)
            {
                var timerField = typeof(paintingControl).GetField("deathTimerMax", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (timerField != null) timerField.SetValue(painting, 999999);
            }
            
            Plugin.Log.LogInfo("[Debug] All entities disabled");
        }
        
        private void EnableAllEntities()
        {
            // Re-enable all monster GameObjects and AI
            var sparky = Object.FindObjectOfType<sparkyBrain>(true);
            if (sparky != null)
            {
                sparky.gameObject.SetActive(true);
                sparky.enabled = true;
                var agent = sparky.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = true;
            }
            
            var jeff = Object.FindObjectOfType<jeffBrain>(true);
            if (jeff != null)
            {
                jeff.gameObject.SetActive(true);
                jeff.enabled = true;
                var agent = jeff.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = true;
            }
            
            var henry = Object.FindObjectOfType<henryBrain>(true);
            if (henry != null)
            {
                henry.gameObject.SetActive(true);
                henry.enabled = true;
                var agent = henry.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = true;
            }
            
            var harold = Object.FindObjectOfType<mapEnBrain>(true);
            if (harold != null)
            {
                harold.gameObject.SetActive(true);
                harold.enabled = true;
                var agent = harold.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = true;
            }
            
            var smile = Object.FindObjectOfType<SmileBrain>(true);
            if (smile != null)
            {
                smile.gameObject.SetActive(true);
                smile.enabled = true;
                var agent = smile.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = true;
            }
            
            foreach (var clown in Object.FindObjectsOfType<clownRandom>(true))
                clown.gameObject.SetActive(true);
            
            // Clear staged references since we're re-enabling AI
            _stagedHenry = null;
            _stagedSparky = null;
            
            Plugin.Log.LogInfo("[Debug] All entities re-enabled");
        }
        
        private void ForceAllPuzzlesActive()
        {
            var pm = Object.FindObjectOfType<PuzzleMaster>();
            if (pm == null)
            {
                Plugin.Log.LogWarning("[Debug] PuzzleMaster not found — not in a Night level?");
                return;
            }
            
            var pmType = typeof(PuzzleMaster);
            var pcType = typeof(PuzzleController);
            string[] psFields = { "ps1", "ps2", "ps3", "ps4", "ps5", "ps6", "ps7", "ps8", "ps9" };
            PuzzleController[] controllers = {
                pm.pCon1, pm.pCon2, pm.pCon3,
                pm.pCon4, pm.pCon5, pm.pCon6,
                pm.pCon7, pm.pCon8, pm.pCon9
            };
            
            int alreadyActive = 0;
            for (int i = 0; i < 9; i++)
            {
                var psField = pmType.GetField(psFields[i], System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool isActive = (bool)psField.GetValue(pm);
                
                if (!isActive && controllers[i] != null)
                {
                    // Activate this puzzle — call thisFan(1) which sets a random preset and hides the map indicator
                    psField.SetValue(pm, true);
                    
                    // Check if it was marked as completed by enableRest (thisFan(2))
                    var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    bool wasCompleted = (bool)completedField.GetValue(controllers[i]);
                    
                    if (wasCompleted)
                    {
                        // Undo the enableRest completion — make it a real puzzle
                        completedField.SetValue(controllers[i], false);
                        controllers[i].fanspin.isOn = false;
                        controllers[i].thisMapIndicator?.SetActive(false);
                        controllers[i].setPuzzlePresetID(); // Give it a random preset
                    }
                }
                else if (isActive)
                {
                    alreadyActive++;
                }
            }
            
            // Set required puzzles to 9
            PuzzleMaster.requiredPuzzles = 9;
            PuzzleMaster.totalCompletedPuzzles = 0;
            
            Plugin.Log.LogInfo($"[Debug] Forced all 9 puzzles active (were {alreadyActive}), required=9");
            
            // If we're the host in multiplayer, re-send puzzle init so the client gets the update
            if (Steam != null && Steam.IsRunning && Steam.IsHost && PlayerSync != null)
            {
                // Reset the sent flag so CheckPuzzleInitSync re-sends
                var syncType = typeof(PlayerSync);
                var sentField = syncType.GetField("_puzzleInitSent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sentField?.SetValue(PlayerSync, false);
            }
        }

        private void DrawDebugActorUI()
        {
            float panelWidth = 260f;
            float panelX = 10f;
            float panelY = 10f;
            
            // Calculate panel height based on content
            int recordingCount = ActorRecorder.Recordings.Count;
            int actorCount = ActorRecorder.Actors.Count;
            float baseHeight = 110f; // Header + record button + labels
            float recordingsHeight = recordingCount * 25f;
            float actorsHeight = actorCount * 25f;
            if (recordingCount > 0) baseHeight += 25f; // "Recordings:" label
            if (actorCount > 0) baseHeight += 25f; // "Actors:" label
            float panelHeight = Mathf.Min(baseHeight + recordingsHeight + actorsHeight, 500f);
            
            GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.1f, 0.2f, 0.95f);
            GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "");
            
            GUI.contentColor = new UnityEngine.Color(1f, 0.8f, 0.4f);
            GUI.Label(new Rect(panelX + 10, panelY + 5, panelWidth - 20, 20), "🎬 Debug Mode (F4)");
            GUI.contentColor = UnityEngine.Color.white;
            
            float y = panelY + 28f;
            
            // Entities status
            GUI.Label(new Rect(panelX + 10, y, panelWidth - 20, 20), "Entities: OFF | All 9 vents active");
            y += 22f;
            
            // Record button
            if (ActorRecorder.IsRecording)
            {
                GUI.backgroundColor = new UnityEngine.Color(0.8f, 0.2f, 0.2f, 1f);
                if (GUI.Button(new Rect(panelX + 10, y, panelWidth - 20, 25), "⏹ Stop Recording"))
                {
                    ActorRecorder.StopRecording();
                }
            }
            else
            {
                GUI.backgroundColor = new UnityEngine.Color(0.8f, 0.3f, 0.3f, 1f);
                if (GUI.Button(new Rect(panelX + 10, y, panelWidth - 20, 25), "⏺ Record"))
                {
                    ActorRecorder.StartRecording();
                }
            }
            y += 30f;
            
            GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.1f, 0.2f, 0.95f);
            
            // Recordings list
            if (recordingCount > 0)
            {
                GUI.contentColor = new UnityEngine.Color(0.7f, 0.9f, 1f);
                GUI.Label(new Rect(panelX + 10, y, panelWidth - 20, 20), $"Recordings ({recordingCount}):");
                GUI.contentColor = UnityEngine.Color.white;
                y += 22f;
                
                for (int i = 0; i < recordingCount; i++)
                {
                    var rec = ActorRecorder.Recordings[i];
                    float duration = rec.Frames.Count * rec.FrameInterval;
                    
                    GUI.Label(new Rect(panelX + 10, y, 100, 20), $"{rec.Name} ({duration:F1}s)");
                    
                    // Play button — spawns an actor
                    GUI.backgroundColor = new UnityEngine.Color(0.2f, 0.6f, 0.2f, 1f);
                    if (GUI.Button(new Rect(panelX + panelWidth - 110, y, 40, 20), "▶"))
                    {
                        ActorRecorder.SpawnActor(rec, true);
                    }
                    
                    // Delete recording button
                    GUI.backgroundColor = new UnityEngine.Color(0.6f, 0.2f, 0.2f, 1f);
                    if (GUI.Button(new Rect(panelX + panelWidth - 60, y, 40, 20), "✕"))
                    {
                        ActorRecorder.DeleteRecording(rec);
                        break; // List modified, break out
                    }
                    
                    GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.1f, 0.2f, 0.95f);
                    y += 25f;
                }
            }
            
            // Actors list
            if (actorCount > 0)
            {
                GUI.contentColor = new UnityEngine.Color(0.9f, 0.9f, 0.5f);
                GUI.Label(new Rect(panelX + 10, y, panelWidth - 20, 20), $"Actors ({actorCount}):");
                GUI.contentColor = UnityEngine.Color.white;
                y += 22f;
                
                for (int i = 0; i < ActorRecorder.Actors.Count; i++)
                {
                    var actor = ActorRecorder.Actors[i];
                    string status = actor.IsPlaying ? "▶" : "⏸";
                    
                    GUI.Label(new Rect(panelX + 10, y, 120, 20), $"{status} {actor.Name}");
                    
                    // Pause/Resume
                    GUI.backgroundColor = new UnityEngine.Color(0.3f, 0.3f, 0.6f, 1f);
                    if (GUI.Button(new Rect(panelX + panelWidth - 110, y, 40, 20), actor.IsPlaying ? "⏸" : "▶"))
                    {
                        ActorRecorder.ToggleActor(actor);
                    }
                    
                    // Remove actor
                    GUI.backgroundColor = new UnityEngine.Color(0.6f, 0.2f, 0.2f, 1f);
                    if (GUI.Button(new Rect(panelX + panelWidth - 60, y, 40, 20), "✕"))
                    {
                        ActorRecorder.RemoveActor(actor);
                        break; // List modified
                    }
                    
                    GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.1f, 0.2f, 0.95f);
                    y += 25f;
                }
                
                // Remove all actors button
                if (actorCount > 1)
                {
                    GUI.backgroundColor = new UnityEngine.Color(0.5f, 0.15f, 0.15f, 1f);
                    if (GUI.Button(new Rect(panelX + 10, y, panelWidth - 20, 22), "Remove All Actors"))
                    {
                        ActorRecorder.RemoveAllActors();
                    }
                    GUI.backgroundColor = new UnityEngine.Color(0.15f, 0.1f, 0.2f, 0.95f);
                }
            }
            
            GUI.contentColor = UnityEngine.Color.white;
            GUI.backgroundColor = UnityEngine.Color.white;
        }
#endif
        
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
            
            // ===== Debug mode UI (F4) =====
#if DEBUG
            if (DebugEntitiesDisabled)
            {
                DrawDebugActorUI();
            }
#endif
            
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

        public void HostGame()
        {
            StartHosting();
        }
        
        public void JoinLobby(ulong lobbyId)
        {
            if (!_steamInitialized)
            {
                _statusMessage = "Steam not initialized!";
                return;
            }
            
            // Don't allow joining if already in any session
            if (Steam.IsRunning || Steam.IsInLobby)
            {
                _statusMessage = "Already in a session! Disconnect first.";
                return;
            }
            
            Steam.JoinLobby(lobbyId);
            _statusMessage = "Joining lobby...";
        }
        
        public void DisconnectFromLobby()
        {
            Disconnect();
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
                // VoiceChat?.Cleanup(); // Disabled
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
            Spectate?.Cleanup();
            // VoiceChat?.Cleanup(); // Disabled
            PlayerSync?.Cleanup();
            Steam?.Shutdown();
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.crawlspace2.multiplayer";
        public const string PLUGIN_NAME = "Crawlspace2MP";
        public const string PLUGIN_VERSION = "1.1.1";
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
                    Plugin.LogDebug("[Client] Calendar blocked - only host can select night");
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
            
            // Ghosts can't trigger scene exits
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // All alive remote players must be in the main room to leave
            if (MPManager.Instance?.PlayerSync != null)
            {
                var alivePositions = MPManager.Instance.PlayerSync.GetRemotePlayerPositionsNonGhost();
                foreach (var pos in alivePositions)
                {
                    // Main room bounds (same as clown room check)
                    bool inMainRoom = pos.x > -6.8f && pos.x < 0.5f && pos.z > -4.3f && pos.z < 3.1f;
                    if (!inMainRoom)
                    {
                        Plugin.Log.LogInfo("[Exit] Blocked - alive remote player not in main room yet");
                        return false;
                    }
                }
            }
            
            bool isLobbyOwner = steam.CurrentLobby.Owner.Id == Steamworks.SteamClient.SteamId;
            
            // Any player (host or client) can trigger exit — the "all alive players in room"
            // check above already gates it. Send scene change so the other player loads too.
            if (steam.IsRunning)
            {
                __instance.loadSelectedNight();
                
                var scenenameField = typeof(sceneLeave).GetField("scenename", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                string sceneName = scenenameField?.GetValue(__instance) as string;
                
                if (!string.IsNullOrEmpty(sceneName))
                {
                    Plugin.Log.LogInfo($"[{(isLobbyOwner ? "Host" : "Client")}] Scene exit, sending scene change: {sceneName}");
                    MPManager.Instance.PlayerSync.SendSceneChange(sceneName);
                }
                else
                {
                    Plugin.Log.LogWarning("[Exit] sceneLeave.scenename is empty after loadSelectedNight()");
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
            if (MPManager.Instance?.PlayerSync?.IsReceivingPaintingFlash == true)
                return;
            
            // Ghosts can't interact with paintings
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return;
            
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
    // Painting death timer - runs on BOTH players
    // Entity state is synced from host, but each player checks their own room position
    // This way paintings can kill the client even if the host isn't in the main room
    [HarmonyPatch(typeof(paintingControl), "timerControl")]
    public class PaintingTimerPatch
    {
        static bool Prefix(paintingControl __instance)
        {
            // Debug mode — no painting kills
#if DEBUG
            if (MPManager.DebugEntitiesDisabled) return false;
#endif
            
            // Ghosts don't have painting death timers
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Let it run for everyone alive - each player has their own death timer
            // based on their own isInMainRoom state
            return true;
        }
    }
    
    // Painting kills are LOCAL per player - each player's own timerControl() handles
    // their death based on whether THEY are in the main room. No need to sync kills.
    // Previously this sent painting death to the other player, causing double-kills.
    [HarmonyPatch(typeof(paintingControl), "killPlayer")]
    public class PaintingKillSyncPatch
    {
        static void Postfix(paintingControl __instance)
        {
            // No-op: painting deaths are independent per player.
            // Each player's timerControl() counts their own death timer
            // based on their own isInMainRoom state.
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
    
    // Clown FixedUpdate: controller handles AI logic, but client still needs kill timer
    [HarmonyPatch(typeof(clownRandom), "FixedUpdate")]
    public class ClownUpdatePatch
    {
        static bool Prefix(clownRandom __instance)
        {
            // Ghosts can't die
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
            {
                return false;
            }
            
            // Controller runs everything normally
            if (MPManager.Instance?.Steam == null || 
                !MPManager.Instance.Steam.IsRunning ||
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                return true;
            }
            
            // Non-controller: block AI logic but run kill timer
            var attackField = typeof(clownRandom).GetField("clownAttackingSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            bool isAttacking = attackField != null ? (bool)attackField.GetValue(__instance) : false;
            
            if (isAttacking)
            {
                var killTimerField = typeof(clownRandom).GetField("clownKillTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int killTimer = killTimerField != null ? (int)killTimerField.GetValue(__instance) : 0;
                killTimer++;
                killTimerField?.SetValue(__instance, killTimer);
                
                if (killTimer >= __instance.clownTimeToKill)
                {
                    __instance.JSC.onDeathClown();
                    killTimerField?.SetValue(__instance, 0);
                }
                
                // Room bounds check - if player leaves room, reset attack
                if (!(__instance.player.transform.position.x > -6.4f) || 
                    !(__instance.player.transform.position.x < 0.1f) || 
                    !(__instance.player.transform.position.z > -3.9f) || 
                    !(__instance.player.transform.position.z < 2.7f))
                {
                    killTimerField?.SetValue(__instance, 0);
                    attackField?.SetValue(__instance, false);
                }
            }
            
            return false; // Block the rest (changeOnComplete, etc.)
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
            
            // Ghosts can't help defeat Jeff
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return;
            
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
            // Only controller runs Henry AI - client receives position sync
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.PlayerSync.ShouldControlMonsters)
                    return false; // Block on non-controller
            }
            return true;
        }
    }
    
    // HENRY: Fix camping behavior - use closest player position for bounds check
    [HarmonyPatch(typeof(henryBrain), "outOfBoundCheck")]
    public class HenryBoundsCheckPatch
    {
        static bool Prefix(henryBrain __instance)
        {
            // Only controller runs this
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.PlayerSync.ShouldControlMonsters)
                    return false;
            }
            
            // In multiplayer, Henry should only reset if ALL players are in the main room
            // Original game resets when the single player enters main room
            // With multiplayer, if one player is still in the vents, Henry should keep chasing
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && MPManager.Instance.Steam.IsRunning)
            {
                // Check local player position
                Vector3 localPos = __instance.player.transform.position;
                bool localInMainRoom = localPos.x > -6.8f && localPos.x < 0.5f && 
                                       localPos.z > -4.3f && localPos.z < 3.1f;
                
                // Check if any remote player is NOT in the main room (and alive)
                var remotePositions = playerSync.GetRemotePlayerPositionsNonGhost();
                bool anyRemoteOutsideMainRoom = false;
                foreach (var pos in remotePositions)
                {
                    bool inMainRoom = pos.x > -6.8f && pos.x < 0.5f && 
                                      pos.z > -4.3f && pos.z < 3.1f;
                    if (!inMainRoom)
                    {
                        anyRemoteOutsideMainRoom = true;
                        break;
                    }
                }
                
                // Only reset Henry if ALL alive players are in the main room
                // If local player is in main room but a remote player is in the vents, don't reset
                if (localInMainRoom && !anyRemoteOutsideMainRoom)
                {
                    var cooldownField = typeof(henryBrain).GetField("cooldownTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    int cooldownTimer = cooldownField != null ? (int)cooldownField.GetValue(__instance) : 0;
                    
                    if (cooldownTimer > 50)
                    {
                        cooldownField?.SetValue(__instance, 0);
                        __instance.setPosRandom();
                        return false;
                    }
                    cooldownField?.SetValue(__instance, cooldownTimer + 1);
                }
                
                return false; // We handled it
            }
            
            return true; // Not in multiplayer, run original
        }
    }
    
    // SMILE: Triggers synced, runs LOCALLY - each player has their own Smile chasing them
    [HarmonyPatch(typeof(SmileBrain), "moveToPlayer")]
    public class SmileMovePatch
    {
        static bool Prefix(SmileBrain __instance)
        {
            // Ghosts don't get chased
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return false;
            
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
    
    // HENRY: SYNCED - Block AI logic on client but keep death check
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
            
            // Client: block ALL AI logic for Henry (position is synced from host)
            // Kill checks are handled by the HOST via targeted kill packets
            // This prevents double-kills when both players are near Henry
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync != null)
            {
                if (!MPManager.Instance.PlayerSync.ShouldControlMonsters)
                {
                    return false; // Block everything - host handles kills via targeted packet
                }
            }
            return true;
        }
    }
    
    // HAROLD: SYNCED - Block AI on client, host handles kills via targeted packet
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
            
            // Client: block ALL AI for Harold (position is synced from host)
            // Kill checks are handled by the HOST via targeted kill packets
            // Keep minimap/haptics since those are local visual feedback
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync != null)
            {
                if (!MPManager.Instance.PlayerSync.ShouldControlMonsters)
                {
                    // Still do minimap + haptics (local visual feedback only)
                    if (__instance.player != null)
                    {
                        float dist = Vector3.Distance(__instance.player.transform.position, __instance.transform.position);
                        if (dist < __instance.minimapViewDist)
                        {
                            __instance.mmc.setMapIconPosEnemy();
                            if (dist < __instance.minimapViewDist / 2f)
                            {
                                __instance.hapticTriggerFunc();
                            }
                        }
                    }
                    return false; // Block AI + kill check - host handles kills
                }
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
            // Only controller runs Harold AI - client receives position sync
            if (MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning)
            {
                if (!MPManager.Instance.PlayerSync.ShouldControlMonsters)
                    return false; // Block on non-controller
                
                // Custom wander logic that accounts for multiplayer
                __instance.agent.speed = __instance.baseSpeed;
                
                // Get battery charge from closest ALIVE player
                float batteryCharge = BackpackControl.batteryCharge; // Default to local
                bool hasAlivePlayer = !MPManager.Instance.PlayerSync.IsLocalGhost;
                
                // If local player is ghost, check remote players
                if (MPManager.Instance.PlayerSync.IsLocalGhost)
                {
                    batteryCharge = MPManager.Instance.PlayerSync.GetRemoteBatteryCharge();
                    // Check if any remote player is alive (non-ghost)
                    var remotePositions = MPManager.Instance.PlayerSync.GetRemotePlayerPositionsNonGhost();
                    hasAlivePlayer = remotePositions.Count > 0;
                }
                
                // If no alive players, keep wandering (don't lock)
                if (!hasAlivePlayer)
                {
                    var playerLockField = typeof(mapEnBrain).GetField("playerLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    playerLockField?.SetValue(__instance, false);
                    
                    // Keep moving to random positions
                    if (Vector3.Distance(__instance.transform.position, __instance.agent.destination) < 0.1f)
                    {
                        __instance.setRandomPos();
                    }
                    
                    return false; // Skip original method
                }
                
                // Access playerLock field via reflection
                var playerLockFieldRef = typeof(mapEnBrain).GetField("playerLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool playerLock = playerLockFieldRef != null ? (bool)playerLockFieldRef.GetValue(__instance) : false;
                
                if (batteryCharge <= 0.2f)
                {
                    playerLockFieldRef?.SetValue(__instance, true);
                }
                else if (playerLock)
                {
                    playerLockFieldRef?.SetValue(__instance, false);
                    __instance.setRandomPos();
                }
                
                if (Vector3.Distance(__instance.transform.position, __instance.agent.destination) < 0.1f)
                {
                    __instance.setRandomPos();
                }
                
                return false; // Skip original method
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
    
    // Only controller runs EnemyDifMaster attack timer for SYNCED monsters (Henry/Harold)
    // Sparky, Jeff, and Smile are independent - they trigger locally on each player
    // But multiAttackTrigger controls Sparky/Jeff/Smile timing, so let it run on both
    [HarmonyPatch(typeof(EnemyDifMaster), "multiAttackTrigger")]
    public class EnemyDifMasterMultiAttackPatch
    {
        static bool Prefix()
        {
#if DEBUG
            if (MPManager.DebugEntitiesDisabled) return false;
#endif
            
            // Ghosts don't trigger monster attacks
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return false;
            
            return true;
        }
    }
    
    [HarmonyPatch(typeof(EnemyDifMaster), "attackTrigger")]
    public class EnemyDifMasterAttackPatch
    {
        static bool Prefix()
        {
#if DEBUG
            if (MPManager.DebugEntitiesDisabled) return false;
#endif
            
            // Ghosts don't trigger monster attacks
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return false;
            
            return true;
        }
    }
    
    // Sync Sparky trigger from controller to non-controller
    [HarmonyPatch(typeof(sparkyBrain), "triggerAttack")]
    public class SparkyTriggerSyncPatch
    {
        static void Postfix(sparkyBrain __instance)
        {
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync != null &&
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                // Controller triggered Sparky - sync to other players
                // The state sync will pick up currentState == 2 and apply it
                // No extra packet needed - the 10Hz monster sync handles it
            }
        }
    }
    
    // Sync Jeff trigger from controller to non-controller
    [HarmonyPatch(typeof(jeffBrain), "triggerAttack")]
    public class JeffTriggerSyncPatch
    {
        static void Postfix(jeffBrain __instance)
        {
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning &&
                MPManager.Instance.PlayerSync != null &&
                MPManager.Instance.PlayerSync.ShouldControlMonsters)
            {
                // Controller triggered Jeff - sync to other players
                // The state sync will pick up currentState == 2 and apply it
                // No extra packet needed - the 10Hz monster sync handles it
            }
        }
    }
    
    // SMILE: Fully independent per player (like Sparky) - NO sync needed
    // Each player's EnemyDifMaster triggers Smile locally via attackTrigger()
    // Removed SmileTriggerPatch - was causing infinite trigger spam
    
    // Prevent client from running random puzzle initialization - host will sync the puzzle state
    [HarmonyPatch(typeof(PuzzleMaster), "Start")]
    public class PuzzleMasterStartPatch
    {
        static bool Prefix(PuzzleMaster __instance)
        {
            bool isInMultiplayer = MPManager.Instance?.Steam != null && MPManager.Instance.Steam.IsRunning;
            if (!isInMultiplayer) return true;
            
            bool isHost = MPManager.Instance.Steam.IsHost;
            bool isGhostHost = isHost && MPManager.Instance.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost;
            
            // Skip random initialization on client (host will sync the real state)
            // Also skip on ghost host (we'll restore saved state after Start runs)
            if (!isHost || isGhostHost)
            {
                // Still need to initialize the static variables
                PuzzleMaster.totalCompletedPuzzles = 0;
                PuzzleMaster.requiredPuzzles = __instance.totalPuzzlesThisNight;
                
                // Hide all map indicators until state is synced/restored
                PuzzleController[] controllers = {
                    __instance.pCon1, __instance.pCon2, __instance.pCon3,
                    __instance.pCon4, __instance.pCon5, __instance.pCon6,
                    __instance.pCon7, __instance.pCon8, __instance.pCon9
                };
                foreach (var pc in controllers)
                {
                    if (pc != null && pc.thisMapIndicator != null)
                        pc.thisMapIndicator.SetActive(false);
                }
                
                if (isGhostHost)
                    Plugin.Log.LogInfo("[Ghost Host] Skipping PuzzleMaster.Start() random init - will restore saved state");
                
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
            
            var pcType = typeof(PuzzleController);
            
            // Check if local battery is in this puzzle slot
            bool localBatteryHere = __instance.thisPuzzleID == BackpackControl.batteryLocationID && BackpackControl.batteryCharge > 0f;
            
            // If local battery is here, mostly let original run — but protect against presetID == 0
            if (localBatteryHere)
            {
                var presetIDField = pcType.GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int presetID = presetIDField != null ? (int)presetIDField.GetValue(__instance) : 0;
                
                // If preset hasn't been synced yet (client waiting for host init), hold timer2 below 5
                // so loadPreset(0) never gets called (which would make the puzzle unsolvable).
                // We set to 3 because the original FixedUpdate will increment it to 4 (not 5).
                if (presetID == 0)
                {
                    var timer2Field = pcType.GetField("timer2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (timer2Field != null)
                    {
                        int timer2 = (int)timer2Field.GetValue(__instance);
                        if (timer2 >= 3)
                        {
                            timer2Field.SetValue(__instance, 3);
                        }
                    }
                }
                
                return true;
            }
            
            // Check if remote player has battery in this puzzle's slot
            int remoteBatteryLocation = playerSync.GetRemoteBatteryLocationID();
            float remoteBatteryCharge = playerSync.GetRemoteBatteryCharge();
            bool remoteBatteryHere = __instance.thisPuzzleID == remoteBatteryLocation && remoteBatteryCharge > 0f;
            
            // If remote battery is here, keep puzzle active but skip original (which would reset it)
            if (remoteBatteryHere)
            {
                // DON'T run the timer/handColorID/clearTempTiles logic here.
                // The remote player is controlling this puzzle — block state comes via
                // PACKET_PUZZLE_BLOCK visual sync. Running the timer would erase their progress.
                
                // Keep the puzzle lit up
                __instance.setMats(1);
                
                // Handle timer2 for loadPreset (mirrors original exactly)
                var timer2Field2 = pcType.GetField("timer2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var puzzleCompletedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var presetIDField2 = pcType.GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (timer2Field2 != null && puzzleCompletedField != null && presetIDField2 != null)
                {
                    int timer2 = (int)timer2Field2.GetValue(__instance);
                    bool completed = (bool)puzzleCompletedField.GetValue(__instance);
                    int presetID = (int)presetIDField2.GetValue(__instance);
                    
                    timer2++;
                    
                    if (timer2 == 5 && !completed)
                    {
                        if (presetID > 0)
                        {
                            // Preset is ready, load it
                            __instance.loadPreset(presetID);
                        }
                        else
                        {
                            // Preset not synced yet - hold timer2 at 4 so we retry next frame
                            timer2 = 4;
                        }
                    }
                    
                    timer2Field2.SetValue(__instance, timer2);
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
                // Track that we completed this puzzle locally
                MPManager.Instance.PlayerSync.MarkPuzzleCompleted(__instance.thisPuzzleID);
                MPManager.Instance.PlayerSync.SendPuzzleComplete(__instance.thisPuzzleID);
            }
        }
    }
    
    // NOTE: Puzzle block visual sync has been REMOVED
    // Each player solves puzzles independently - only completion is synced
    // This prevents buggy visual sync where shapes appear in wrong positions
    
    // Sync clown nose honk
    [HarmonyPatch(typeof(clownNose), "checkHonk")]
    public class ClownHonkPatch
    {
        static void Prefix(clownNose __instance, out bool __state)
        {
            __state = __instance.honkSound != null && __instance.honkSound.isPlaying;
        }
        
        static void Postfix(clownNose __instance, bool __state)
        {
            if (MPManager.Instance?.PlayerSync?.IsReceivingHonk == true)
                return;
            
            // Ghosts can't honk
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return;
            
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
            if (MPManager.Instance?.PlayerSync?.IsReceivingVentSound == true)
                return;
            
            // Ghosts don't make vent sounds
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return;
            
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning)
            {
                Vector3 soundPos = __instance.spawnpos.position;
                MPManager.Instance.PlayerSync.SendVentSound(soundPos, 0);
            }
        }
    }
    
    // Sync crawling sounds to remote player
    [HarmonyPatch(typeof(crawlSoundContrl), "playTapSound")]
    public class CrawlSoundPatch
    {
        static void Postfix(crawlSoundContrl __instance)
        {
            if (MPManager.Instance?.PlayerSync?.IsReceivingVentSound == true)
                return;
            
            // Ghosts don't make crawl sounds
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
                return;
            
            if (MPManager.Instance?.Steam != null && 
                MPManager.Instance.Steam.IsRunning)
            {
                // Send the player's position so the remote player hears it in 3D
                Vector3 soundPos = __instance.transform.position;
                
                // Also send whether we're in main room so remote plays correct sound set
                bool inMainRoom = __instance.mtc != null && __instance.mtc.isInMainRoom;
                MPManager.Instance.PlayerSync.SendCrawlSound(soundPos, inMainRoom);
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
    
    // Sync puzzle block visual changes to the other player in real-time
    [HarmonyPatch(typeof(PuzzleBlock), "setThisID")]
    public class PuzzleBlockVisualSyncPatch
    {
        static void Postfix(PuzzleBlock __instance, int input)
        {
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return;
            
            // Don't re-send changes we received from the other player
            if (MPManager.Instance.PlayerSync.IsReceivingPuzzleBlock)
                return;
            
            // Send if EITHER player's battery is powering this puzzle
            if (__instance.pcontrol == null) return;
            int puzzleID = __instance.pcontrol.thisPuzzleID;
            
            bool localBatteryHere = puzzleID == BackpackControl.batteryLocationID && BackpackControl.batteryCharge > 0f;
            int remoteBattery = MPManager.Instance.PlayerSync.GetRemoteBatteryLocationID();
            bool remoteBatteryHere = puzzleID == remoteBattery;
            
            if (!localBatteryHere && !remoteBatteryHere)
                return;
            
            MPManager.Instance.PlayerSync.SendPuzzleBlock(puzzleID, __instance.blockNumber, input);
        }
    }
    
    // NOTE: Crank lock removed - not needed since each player can only charge their own battery
    // The crank checks BackpackControl.batteryLocationID == 1 which is per-player
    
    // Override crank battery visual when remote player has battery in crank
    // Uses Prefix to SKIP the original method (which would zero out the display)
    // and instead show the smoothly interpolated remote charge value
    [HarmonyPatch(typeof(crankControl), "batteryScreenVisual")]
    public class CrankVisualSyncPatch
    {
        static bool Prefix(crankControl __instance)
        {
            // Not in multiplayer - let original run
            if (MPManager.Instance?.Steam == null || !MPManager.Instance.Steam.IsRunning)
                return true;
            
            // If LOCAL player has battery in crank, let the game handle it normally
            if (BackpackControl.batteryLocationID == 1)
                return true;
            
            // Check if a remote player has battery in crank
            if (!MPManager.Instance.PlayerSync.RemoteHasBatteryInCrank)
                return true; // No remote battery in crank, let original run (it will hide the display)
            
            // Remote player has battery in crank - we handle the display ourselves
            float smoothCharge = MPManager.Instance.PlayerSync.RemoteCrankChargeDisplay;
            
            if (__instance.batteryFill != null)
                __instance.batteryFill.fillAmount = smoothCharge / 55f;
            
            if (__instance.batteryIMG != null)
                __instance.batteryIMG.SetActive(true);
            
            // Also apply interpolated rotation to the crank handle
            __instance.transform.rotation = MPManager.Instance.PlayerSync.RemoteCrankRotationDisplay;
            
            return false; // Skip original - prevents it from zeroing out our display
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
            
            // Check if ANY remote player has battery at this station (supports 3+ players)
            var remoteState = MPManager.Instance.PlayerSync?.GetRemoteBatteryAtLocation(__instance.thisStationID);
            if (remoteState != null)
            {
                if (__instance.thisBatteryVisual != null)
                    __instance.thisBatteryVisual.SetActive(true);
            }
            else
            {
                // No remote player has battery here either - make sure it's hidden
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
            
            // Ghosts don't sync vent doors
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost)
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
            
            // Allow crank (1) and exit door (100) - each player has independent batteries
            // and needs to use these stations. Only block puzzle stations (2-99) to prevent
            // two players from powering the same puzzle simultaneously.
            if (targetZone == 1 || targetZone >= 100) return true;
            
            // Check if ANY remote player has battery in this puzzle station (supports 3+ players)
            var remoteState = MPManager.Instance.PlayerSync.GetRemoteBatteryAtLocation(targetZone);
            if (remoteState != null)
            {
                return false; // Block the placement - remote player already powering this puzzle
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
    // CRITICAL: Send death notifications IMMEDIATELY (when deathID == 0)
    // This ensures notifications are sent even if the game is paused (VR headset off)
    // The old code waited for deathID > 0, which happens in the coroutine AFTER WaitForSeconds
    // If the game is paused, the coroutine never completes and the notification never sends
    
    // CRITICAL: All death patches use Prefix (not Postfix) because the death coroutines
    // set deathID synchronously before the first yield. A Postfix would see deathID != 0
    // and never send the death notification. Prefix runs BEFORE the method, when deathID is still 0.
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathClown")]
    public class DeathClownPatch
    {
        static void Prefix(jumpscareController __instance)
        {
            // Don't send death notification if already a ghost
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost) return;
            
            var deathIDField = __instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                int deathID = (int)deathIDField.GetValue(__instance);
                if (deathID == 0)
                {
                    Plugin.Log.LogInfo("[Death] Clown killed player - sending death notification");
                    MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 1);
                }
            }
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHarold")]
    public class DeathHaroldPatch
    {
        static void Prefix(jumpscareController __instance)
        {
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost) return;
            
            var deathIDField = __instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                int deathID = (int)deathIDField.GetValue(__instance);
                if (deathID == 0)
                {
                    Plugin.Log.LogInfo("[Death] Harold killed player - sending death notification");
                    MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 2);
                }
            }
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSparky")]
    public class DeathSparkyPatch
    {
        static void Prefix(jumpscareController __instance)
        {
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost) return;
            
            var deathIDField = __instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                int deathID = (int)deathIDField.GetValue(__instance);
                if (deathID == 0)
                {
                    Plugin.Log.LogInfo("[Death] Sparky killed player - sending death notification");
                    MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 3);
                }
            }
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHenry")]
    public class DeathHenryPatch
    {
        static void Prefix(jumpscareController __instance)
        {
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost) return;
            
            var deathIDField = __instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                int deathID = (int)deathIDField.GetValue(__instance);
                if (deathID == 0)
                {
                    Plugin.Log.LogInfo("[Death] Henry killed player - sending death notification");
                    MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 4);
                }
            }
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSmiley")]
    public class DeathSmileyPatch
    {
        static void Prefix(jumpscareController __instance)
        {
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost) return;
            
            var deathIDField = __instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                int deathID = (int)deathIDField.GetValue(__instance);
                if (deathID == 0)
                {
                    Plugin.Log.LogInfo("[Death] Smiley killed player - sending death notification");
                    MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 5);
                }
            }
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathJeff")]
    public class DeathJeffPatch
    {
        static void Prefix(jumpscareController __instance)
        {
            if (MPManager.Instance?.PlayerSync != null && MPManager.Instance.PlayerSync.IsLocalGhost) return;
            
            var deathIDField = __instance.GetType().GetField("deathID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (deathIDField != null)
            {
                int deathID = (int)deathIDField.GetValue(__instance);
                if (deathID == 0)
                {
                    Plugin.Log.LogInfo("[Death] Jeff killed player - sending death notification");
                    MPManager.Instance?.PlayerSync?.SendDeathGhost(true, 6);
                }
            }
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
            
            // Check if door is already fully charged (either player charged it)
            bool doorFullyCharged = false;
            var timerField = typeof(LeaveDoorControl).GetField("doorLeaveTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (timerField != null)
            {
                int timer = (int)timerField.GetValue(__instance);
                doorFullyCharged = timer >= __instance.doorLeaveRequiredTime && __instance.doorLeaveRequiredTime > 0;
            }
            
            // If door is fully charged AND puzzles are done, keep it in the "ready" state
            // regardless of whose battery is currently in the slot
            if (doorFullyCharged && PuzzleMaster.totalCompletedPuzzles == PuzzleMaster.requiredPuzzles)
            {
                __instance.setYes();
                if (__instance.doorLeaveHitbox != null)
                    __instance.doorLeaveHitbox.SetActive(true);
                return false;
            }
            
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
            // Only intercept if we're in multiplayer and ghost reload is pending
            if (!PlayerSync.IsGhostSceneReload) return true;
            
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync == null) return true;
            
            // Check if this is trying to load Home after death
            if (sceneName.Equals("Home", System.StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogInfo("[Ghost] Intercepting Home load - reloading Night scene as ghost");
                
                // Clear the flag and let PlayerSync handle the reload
                PlayerSync.IsGhostSceneReload = false;
                playerSync.ReloadSceneAsGhost();
                
                return false; // Don't load Home
            }
            
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
                Plugin.Log.LogInfo("[Ghost] Re-enabling world after ghost reload");
                
                // Re-enable world objects that death coroutines disabled
                if (__instance.worldMaster != null)
                    __instance.worldMaster.SetActive(true);
                if (__instance.leftHand != null)
                    __instance.leftHand.SetActive(true);
                if (__instance.rightHand != null)
                    __instance.rightHand.SetActive(true);
                    
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
                }
                
                Plugin.Log.LogInfo("[Ghost] World re-enabled after ghost reload");
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
    // CRITICAL: Check IsDyingThisFrame to avoid blocking the jumpscare on the INITIAL death.
    // Without this, the death notification Prefix sets _localIsGhost=true, then the ghost block
    // Prefix sees IsLocalGhost=true and cancels the original method - no jumpscare plays.
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathClown")]
    public class GhostDeathClownBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost && !playerSync.IsDyingThisFrame)
                return false;
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHarold")]
    public class GhostDeathHaroldBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost && !playerSync.IsDyingThisFrame)
                return false;
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSparky")]
    public class GhostDeathSparkyBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost && !playerSync.IsDyingThisFrame)
                return false;
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathHenry")]
    public class GhostDeathHenryBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost && !playerSync.IsDyingThisFrame)
                return false;
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathSmiley")]
    public class GhostDeathSmileyBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost && !playerSync.IsDyingThisFrame)
                return false;
            return true;
        }
    }
    
    [HarmonyPatch(typeof(jumpscareController), "onDeathJeff")]
    public class GhostDeathJeffBlockPatch
    {
        static bool Prefix()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync != null && playerSync.IsLocalGhost && !playerSync.IsDyingThisFrame)
                return false;
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
    
    /// <summary>
    /// Ghost minimap - always visible without battery when in ghost mode
    /// </summary>
    [HarmonyPatch(typeof(MinimapControl), "FixedUpdate")]
    public class GhostMinimapPatch
    {
        static bool Prefix(MinimapControl __instance)
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync == null || !playerSync.IsLocalGhost)
                return true; // Not a ghost, run original
            
            // Ghost mode: force minimap always open
            // Access private fields via reflection
            var timerField = typeof(MinimapControl).GetField("timer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (timerField == null) return true;
            
            // Force timer to max (7 = fully open)
            timerField.SetValue(__instance, 7);
            
            // Keep minimap active and update position
            if (__instance.minimap != null)
                __instance.minimap.SetActive(true);
            
            // Update player icon position
            __instance.setMapIconPos();
            
            // Update minimap scale to fully open
            if (__instance.minimapRect != null)
                __instance.minimapRect.transform.localScale = new Vector3(1f, 1f, 1f);
            
            // Still update enemy indicator (Harold tracking)
            if (__instance.enemyIndicator != null)
            {
                var haroldTimerField = typeof(MinimapControl).GetField("haroldTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (haroldTimerField != null)
                {
                    int haroldTimer = (int)haroldTimerField.GetValue(__instance);
                    haroldTimer--;
                    haroldTimerField.SetValue(__instance, haroldTimer);
                    __instance.enemyIndicator.SetActive(haroldTimer > 0);
                }
            }
            
            // Set hand to transparent material (minimap is on the hand)
            if (__instance.handmesh != null && __instance.handmatTrans != null)
                __instance.handmesh.material = __instance.handmatTrans;
            
            return false; // Skip original
        }
    }
}
