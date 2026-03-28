using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using GorillaLocomotion;
using Object = UnityEngine.Object;

namespace Crawlspace2MP
{
    public class PlayerSync
    {
        private const byte PACKET_PLAYER_POSITION = 1;
        private const byte PACKET_FLASHLIGHT = 2;
        private const byte PACKET_SCENE_CHANGE = 3;
        private const byte PACKET_NIGHT_SELECTED = 4;
        private const byte PACKET_TV_SYNC = 5;
        private const byte PACKET_PAINTING_SYNC = 6;
        private const byte PACKET_PAINTING_FLASH = 7;
        private const byte PACKET_MONSTER_SYNC = 8;
        private const byte PACKET_JEFF_FLASH = 9;
        private const byte PACKET_BATTERY_SYNC = 10;
        private const byte PACKET_PAINTING_ENTITY = 11;
        private const byte PACKET_PUZZLE_INIT = 12;
        private const byte PACKET_PUZZLE_COMPLETE = 13;
        private const byte PACKET_PUZZLE_BLOCK = 14;
        private const byte PACKET_CLOWN_HONK = 15;
        private const byte PACKET_VENT_SOUND = 16;
        private const byte PACKET_CLOWN_STATE = 25;
        private const byte PACKET_CLOWN_ATTACK = 26;
        private const byte PACKET_SMILE_TRIGGER = 27;
        private const byte PACKET_VENT_DOOR = 28;
        private const byte PACKET_PAINTING_DEATH = 29;
        private const byte PACKET_END_PROGRESS = 40;
        private const byte PACKET_INTERACTION_LOCK = 17;
        private const byte PACKET_EXIT_DOOR_PROGRESS = 18;
        private const byte PACKET_CRANK_SYNC = 19;
        private const byte PACKET_DEATH_GHOST = 20;
        private const byte PACKET_VERSION_CHECK = 21;
        private const byte PACKET_PING = 22;
        private const byte PACKET_PONG = 23;
        private const byte PACKET_HOST_MIGRATION = 24;
        private const byte PACKET_EAR_COVERING = 41;
        private const byte PACKET_TARGETED_KILL = 42;
        
        private Dictionary<int, RemotePlayer> _remotePlayers = new Dictionary<int, RemotePlayer>();
        
        // Ear covering sync - if ANY player covers ears, Sparky shouldn't kill
        private bool _remotePlayerCoveringEars = false;
        public bool IsAnyPlayerCoveringEars => earMaster.isCoveringEars || _remotePlayerCoveringEars;
        
        // Version checking
        private Dictionary<int, string> _peerVersions = new Dictionary<int, string>();
        public event Action<int, string> OnVersionMismatch;  // peerId, theirVersion
        
        // Ping tracking
        private Dictionary<int, float> _peerPings = new Dictionary<int, float>();  // peerId -> ping in ms
        private Dictionary<int, float> _pendingPings = new Dictionary<int, float>();  // peerId -> send time
        private float _lastPingTime = 0f;
        private const float PING_INTERVAL = 2f;  // Ping every 2 seconds
        public float GetPing(int peerId) => _peerPings.TryGetValue(peerId, out float ping) ? ping : -1f;
        public float AveragePing => _peerPings.Count > 0 ? GetAveragePing() : -1f;
        
        private float GetAveragePing()
        {
            if (_peerPings.Count == 0) return -1f;
            float total = 0f;
            foreach (var ping in _peerPings.Values) total += ping;
            return total / _peerPings.Count;
        }
        
        // Spawn point tracking (for potential future use)
        private Vector3 _levelSpawnPoint = Vector3.zero;
        private Quaternion _levelSpawnRotation = Quaternion.identity;
        private bool _spawnPointCaptured = false;
        
        // Track connected peer IDs separately (survives scene changes)
        private HashSet<int> _connectedPeerIds = new HashSet<int>();
        
        // Ghost state tracking
        private bool _localIsGhost = false;
        private bool _pendingGhostTeleport = false;
        private bool _pendingHomeLoad = false; // Set when both players are ghosts, processed in Update
        private float _pendingHomeLoadTimer = 0f; // Delay before loading Home to let jumpscare finish
        private string _ghostSceneToReload = null;
        public bool IsLocalGhost => _localIsGhost;
        public bool IsDyingThisFrame { get; set; } // Set during death Prefix, prevents ghost block from cancelling the jumpscare
        public static bool IsGhostSceneReload = false; // Prevent normal death flow
        
        // Cached references
        private Player _gorillaPlayer;
        private MoveTypeController _moveController;
        private HandControl _handControl;
        private Camera _mainCamera;
        private Transform _playerTransform;
        
        // OVR tracking
        private Transform _ovrLeftHand;
        private Transform _ovrRightHand;
        private Transform _ovrHead;
        
        private float _syncInterval = 0.033f; // ~30 updates per second for smoother movement
        private float _lastSyncTime;
        
        // Flashlight tracking
        private Flashlight _localFlashlight;
        private FlashlightControl _localFlashlightControl;
        private bool _lastFlashlightState = false;
        
        // Scene tracking
        private string _lastScene = "";
        public static bool IsLoadingFromSync = false; // Prevent infinite loop
        
        // Calendar tracking (host only)
        private int _lastNightSelected = -1;
        
        // TV sync tracking
        private UnityEngine.Video.VideoPlayer _tvVideoPlayer;
        private float _lastTvSyncTime = 0f;
        private float _tvSyncInterval = 5f; // Sync TV every 5 seconds
        
        // Painting sync tracking
        private paintingControl _paintingControl;
        private float _lastPaintingSyncTime = 0f;
        private float _paintingSyncInterval = 1f; // Sync paintings every second
        
        // Monster sync tracking
        private sparkyBrain _sparky;
        private jeffBrain _jeff;
        private SmileBrain _smile;
        private henryBrain _henry;
        private mapEnBrain _harold;
        private clownRandom _clown;
        private float _lastMonsterSyncTime = 0f;
        private float _monsterSyncInterval = 0.1f; // Sync monsters 10 times per second
        
        // Track if we should control monsters (client takes over when host dies)
        private bool _hostDiedInLevel = false;
        public bool ShouldControlMonsters => _steam != null && (_steam.IsHost || _hostDiedInLevel);
        
        // Battery sync tracking - SEPARATE batteries per player
        // Each player has their own battery state, but we sync slot placements
        private float _lastBatterySyncTime = 0f;
        private float _batterySyncInterval = 0.1f; // Sync battery 10 times per second
        private int _lastBatteryLocationID = -999;
        private bool _lastBatteryInBackpack = false;
        private float _lastBatteryCharge = -1f;
        private bool _lastLeftHolding = false;
        private bool _lastRightHolding = false;
        
        // Remote player battery states (for display purposes)
        private Dictionary<int, RemoteBatteryState> _remoteBatteryStates = new Dictionary<int, RemoteBatteryState>();
        
        // Crank sync tracking
        private crankControl _crankControl;
        private float _lastCrankSyncTime = 0f;
        private float _crankSyncInterval = 0.2f; // Sync crank 5 times per second
        private float _lastCrankCharge = -1f;
        private bool _lastCrankHasBattery = false;
        
        public class RemoteBatteryState
        {
            public float Charge;
            public int LocationID;
            public bool InBackpack;
            public bool LeftHolding;
            public bool RightHolding;
        }
        
        // Interaction locking - one player at a time
        // Key: interaction type (e.g., "puzzle_1", "crank")
        // Value: peer ID who has the lock (-1 = local player, 0+ = remote peer)
        private static Dictionary<string, int> _interactionLocks = new Dictionary<string, int>();
        private static Dictionary<string, float> _lockTimestamps = new Dictionary<string, float>(); // When lock was last refreshed
        private const float LOCK_TIMEOUT = 5.0f; // Lock expires after 5 seconds without refresh (increased for international play)
        private float _lastLockCleanupTime = 0f;
        private HashSet<string> _localActiveLocks = new HashSet<string>();
        
        // Scene load delay for clients (to match host fade timing)
        private float _sceneLoadDelayTimer = 0f;
        private const float SCENE_LOAD_DELAY = 0.5f; // Half second delay to let fade start
        
        // Check if an interaction is locked by another player
        public static bool IsLockedByOther(string interactionId)
        {
            if (_interactionLocks.TryGetValue(interactionId, out int lockHolder))
            {
                // Check if lock has expired
                if (_lockTimestamps.TryGetValue(interactionId, out float timestamp))
                {
                    if (Time.time - timestamp > LOCK_TIMEOUT)
                    {
                        // Lock expired, remove it
                        _interactionLocks.Remove(interactionId);
                        _lockTimestamps.Remove(interactionId);
                        return false;
                    }
                }
                return lockHolder != -1; // -1 means we hold it
            }
            return false;
        }
        
        // Check if we hold a lock
        public static bool IsLockedByUs(string interactionId)
        {
            if (_interactionLocks.TryGetValue(interactionId, out int lockHolder))
            {
                return lockHolder == -1;
            }
            return false;
        }
        
        // Refresh a lock (acquire if not held, refresh timestamp if held by us)
        public void RefreshLock(string interactionId)
        {
            // Check if someone else has it (and it hasn't expired)
            if (_interactionLocks.TryGetValue(interactionId, out int lockHolder))
            {
                if (lockHolder != -1)
                {
                    // Check if their lock expired
                    if (_lockTimestamps.TryGetValue(interactionId, out float timestamp))
                    {
                        if (Time.time - timestamp > LOCK_TIMEOUT)
                        {
                            // Their lock expired, we can take it
                            _interactionLocks[interactionId] = -1;
                            _lockTimestamps[interactionId] = Time.time;
                            _localActiveLocks.Add(interactionId);
                            SendInteractionLock(interactionId, true);
                            return;
                        }
                    }
                    return; // Someone else has it, can't refresh
                }
            }
            
            // We have it or no one has it - refresh/acquire
            bool isNew = !_interactionLocks.ContainsKey(interactionId) || _interactionLocks[interactionId] != -1;
            _interactionLocks[interactionId] = -1;
            _lockTimestamps[interactionId] = Time.time;
            
            if (isNew)
            {
                _localActiveLocks.Add(interactionId);
                SendInteractionLock(interactionId, true);
            }
        }
        
        // Try to acquire a lock (returns true if successful)
        public bool TryAcquireLock(string interactionId)
        {
            if (IsLockedByOther(interactionId))
                return false;
            
            RefreshLock(interactionId);
            return true;
        }
        
        // Release a lock
        public void ReleaseLock(string interactionId)
        {
            if (_interactionLocks.TryGetValue(interactionId, out int lockHolder))
            {
                if (lockHolder == -1) // Only release if we hold it
                {
                    _interactionLocks.Remove(interactionId);
                    _lockTimestamps.Remove(interactionId);
                    _localActiveLocks.Remove(interactionId);
                    SendInteractionLock(interactionId, false);
                }
            }
        }
        
        // Clean up expired locks periodically
        private void CleanupExpiredLocks()
        {
            if (Time.time - _lastLockCleanupTime < 1f) return;
            _lastLockCleanupTime = Time.time;
            
            var expiredLocks = new List<string>();
            foreach (var kvp in _lockTimestamps)
            {
                if (Time.time - kvp.Value > LOCK_TIMEOUT * 2) // Give extra time before cleanup
                {
                    expiredLocks.Add(kvp.Key);
                }
            }
            
            foreach (var lockId in expiredLocks)
            {
                _interactionLocks.Remove(lockId);
                _lockTimestamps.Remove(lockId);
                _localActiveLocks.Remove(lockId);
            }
        }
        
        // Clear all locks (on disconnect/scene change)
        public void ClearAllLocks()
        {
            _interactionLocks.Clear();
            _lockTimestamps.Clear();
            _localActiveLocks.Clear();
        }
        
        // Puzzle sync tracking
        private PuzzleMaster _puzzleMaster;
        private bool _puzzleInitSent = false;
        
        // Saved puzzle state for ghost reload (host only)
        // When the host dies and reloads the scene as a ghost, PuzzleMaster.Start() resets everything.
        // We save the state before reload and restore it after, so the client's progress isn't wiped.
        private PendingPuzzleInit _savedGhostPuzzleState = null;
        
        // Exit door progress sync
        private LeaveDoorControl _leaveDoor;
        private int _lastDoorLeaveTimer = 0;
        private float _lastDoorSyncTime = 0f;
        private bool _exitDoorFullyCharged = false;
        
        private PacketWriter _writer = new PacketWriter(1024);
        private INetworkTransport _steam;

        public void Initialize(INetworkTransport steam)
        {
            _steam = steam;
            
            // Subscribe to Steam events
            _steam.OnPeerConnected += OnPeerConnected;
            _steam.OnPeerDisconnected += OnPeerDisconnected;
            _steam.OnDataReceived += OnDataReceived;
            
            // Listen for scene loads
            SceneManager.sceneLoaded += OnSceneLoaded;
            
            Plugin.Log.LogInfo("PlayerSync initialized (Steam)");
        }
        
        private void OnPeerConnected(int peerId)
        {
            Plugin.Log.LogInfo($"Peer connected: {peerId}");
            _connectedPeerIds.Add(peerId);
            
            // Create remote player for this peer
            if (!_remotePlayers.ContainsKey(peerId))
            {
                var remote = new RemotePlayer(peerId);
                _remotePlayers[peerId] = remote;
                Plugin.Log.LogInfo($"Created remote player for peer {peerId}");
            }
            
            // Send our version to the new peer
            SendVersionCheck(peerId);
            
            // Send our current battery state to the new peer
            // This ensures they see correct battery visuals immediately
            SendBatterySync();
        }
        
        private void OnPeerDisconnected(int peerId)
        {
            Plugin.Log.LogInfo($"Peer disconnected: {peerId}");
            _connectedPeerIds.Remove(peerId);
            _peerVersions.Remove(peerId);
            _peerPings.Remove(peerId);
            _pendingPings.Remove(peerId);
            
            if (_remotePlayers.TryGetValue(peerId, out var remote))
            {
                remote.Destroy();
                _remotePlayers.Remove(peerId);
            }
            
            // Clean up voice player (disabled)
            // MPManager.Instance?.VoiceChat?.OnPeerDisconnected(peerId);
            
            // Stop spectate if we were receiving from this peer
            MPManager.Instance?.Spectate?.StopReceiving();
            MPManager.Instance?.Spectate?.StopSending();
            
            // If we're a ghost and no one is left, go Home
            if (_localIsGhost && _remotePlayers.Count == 0)
            {
                string scene = SceneManager.GetActiveScene().name;
                if (scene.Contains("Night") && !_pendingHomeLoad)
                {
                    Plugin.Log.LogInfo("[Ghost] All peers disconnected - scheduling Home load");
                    _pendingHomeLoad = true;
                    _pendingHomeLoadTimer = 1.0f;
                }
            }
        }
        
        private void OnDataReceived(int peerId, PacketReader reader)
        {
            if (reader.AvailableBytes < 1) return;
            
            byte packetType = reader.GetByte();
            
            // Route to appropriate handler
            switch (packetType)
            {
                case PACKET_PLAYER_POSITION:
                    HandlePositionPacket(peerId, reader);
                    break;
                case PACKET_FLASHLIGHT:
                    HandleFlashlightPacket(peerId, reader);
                    break;
                case PACKET_SCENE_CHANGE:
                    HandleSceneChangePacket(reader);
                    break;
                case PACKET_NIGHT_SELECTED:
                    HandleNightSelectedPacket(reader);
                    break;
                case PACKET_TV_SYNC:
                    HandleTvSyncPacket(reader);
                    break;
                case PACKET_PAINTING_SYNC:
                    HandlePaintingSyncPacket(reader);
                    break;
                case PACKET_PAINTING_FLASH:
                    HandlePaintingFlashPacket(reader);
                    break;
                case PACKET_MONSTER_SYNC:
                    HandleMonsterSyncPacket(reader);
                    break;
                case PACKET_JEFF_FLASH:
                    HandleJeffFlashPacket();
                    break;
                case PACKET_BATTERY_SYNC:
                    HandleBatterySync(peerId, reader);
                    break;
                case PACKET_PAINTING_ENTITY:
                    HandlePaintingEntity(reader);
                    break;
                case PACKET_PUZZLE_INIT:
                    HandlePuzzleInit(reader);
                    break;
                case PACKET_PUZZLE_COMPLETE:
                    HandlePuzzleComplete(reader);
                    break;
                case PACKET_PUZZLE_BLOCK:
                    HandlePuzzleBlock(reader);
                    break;
                case PACKET_CLOWN_HONK:
                    HandleClownHonkPacket();
                    break;
                case PACKET_VENT_SOUND:
                    HandleVentSoundPacket(reader);
                    break;
                case PACKET_INTERACTION_LOCK:
                    HandleInteractionLockPacket(peerId, reader);
                    break;
                case PACKET_EXIT_DOOR_PROGRESS:
                    HandleExitDoorProgressPacket(reader);
                    break;
                case PACKET_CRANK_SYNC:
                    HandleCrankSyncPacket(reader);
                    break;
                case PACKET_DEATH_GHOST:
                    HandleDeathGhostPacket(peerId, reader);
                    break;
                case PACKET_VERSION_CHECK:
                    HandleVersionCheck(peerId, reader);
                    break;
                case PACKET_PING:
                    HandlePing(peerId, reader);
                    break;
                case PACKET_PONG:
                    HandlePong(peerId, reader);
                    break;
                case PACKET_HOST_MIGRATION:
                    HandleHostMigration(peerId, reader);
                    break;
                case PACKET_CLOWN_STATE:
                    HandleClownState(reader);
                    break;
                case PACKET_CLOWN_ATTACK:
                    HandleClownAttack(reader);
                    break;
                case PACKET_SMILE_TRIGGER:
                    HandleSmileTrigger(reader);
                    break;
                case PACKET_VENT_DOOR:
                    HandleVentDoor(reader);
                    break;
                case PACKET_PAINTING_DEATH:
                    HandlePaintingDeath();
                    break;
                case PACKET_END_PROGRESS:
                    HandleEndProgress(reader);
                    break;
                case PACKET_EAR_COVERING:
                    HandleEarCovering(reader);
                    break;
                case PACKET_TARGETED_KILL:
                    HandleTargetedKill(reader);
                    break;
                case VoiceChat.PACKET_VOICE:
                    // Voice chat disabled
                    break;
                case SpectateSystem.PACKET_SPECTATE_FRAME:
                case SpectateSystem.PACKET_SPECTATE_START:
                case SpectateSystem.PACKET_SPECTATE_STOP:
                    MPManager.Instance?.Spectate?.OnPacketReceived(packetType, reader);
                    break;
                default:
                    Plugin.Log.LogWarning($"Unknown packet type: {packetType}");
                    break;
            }
        }
        
        // Packet handlers using PacketReader
        private void HandlePositionPacket(int peerId, PacketReader reader)
        {
            if (!_remotePlayers.TryGetValue(peerId, out var remote))
            {
                return;
            }
            
            bool isStanding = reader.GetBool();
            var bodyPos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var bodyRot = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var headPos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var headRot = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var leftHandPos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var leftHandRot = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var rightHandPos = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            var rightHandRot = new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            
            // Read hand poses
            float leftGrip = reader.GetFloat();
            float leftTrigger = reader.GetFloat();
            float rightGrip = reader.GetFloat();
            float rightTrigger = reader.GetFloat();
            
            remote.SetTargets(isStanding, bodyPos, bodyRot, headPos, headRot, 
                              leftHandPos, leftHandRot, rightHandPos, rightHandRot,
                              leftGrip, leftTrigger, rightGrip, rightTrigger);
        }
        
        private void HandleFlashlightPacket(int peerId, PacketReader reader)
        {
            bool isOn = reader.GetBool();
            if (_remotePlayers.TryGetValue(peerId, out var remote))
            {
                remote.SetFlashlightState(isOn);
            }
        }
        
        private void HandleSceneChangePacket(PacketReader reader)
        {
            string sceneName = reader.GetString();
            int nightSelected = reader.GetInt();
            
            Plugin.Log.LogInfo($"Received scene change: {sceneName}, night={nightSelected}");
            
            // If transitioning to Home from a Night, save night completion for ALL players
            string currentScene = SceneManager.GetActiveScene().name;
            if (sceneName.Equals("Home", System.StringComparison.OrdinalIgnoreCase) && currentScene.Contains("Night"))
            {
                var sceneLeaveObj = Object.FindObjectOfType<sceneLeave>();
                if (sceneLeaveObj != null)
                {
                    sceneLeaveObj.loadSelectedNight();
                    Plugin.Log.LogInfo($"[Sync] Saved night completion via loadSelectedNight()");
                }
            }
            
            calenderControl.nightSelected = nightSelected;
            
            if (_steam.IsHost && _localIsGhost)
            {
                // Ghost host: client triggered exit, follow them
                Plugin.Log.LogInfo($"[Ghost Host] Client triggered scene exit, loading: {sceneName}");
                IsLoadingFromSync = true;
                ResetGhostState();
                SceneManager.LoadScene(sceneName);
            }
            else if (_steam.IsHost)
            {
                // Alive host: client triggered exit
                Plugin.Log.LogInfo($"[Host] Client triggered scene exit, loading: {sceneName}");
                IsLoadingFromSync = true;
                TriggerClientFade();
                SceneManager.LoadScene(sceneName);
            }
            else
            {
                // Client: defer the scene load to Update so OnSceneLoaded fires properly
                // Loading directly from a network callback can skip Unity's sceneLoaded event
                Plugin.Log.LogInfo($"[Client] Deferring scene load: {sceneName}");
                IsLoadingFromSync = true;
                _lastScene = sceneName;
                _pendingSceneLoad = sceneName;
                _sceneLoadDelayTimer = 0.1f; // Minimal delay, just enough to defer to Update
                TriggerClientFade();
            }
        }
        
        private void HandleNightSelectedPacket(PacketReader reader)
        {
            int night = reader.GetInt();
            Plugin.Log.LogInfo($"Received night selection: {night}");
            calenderControl.nightSelected = night;
            
            // Update the calendar visual on client side
            var calendar = UnityEngine.Object.FindObjectOfType<calenderControl>();
            if (calendar != null)
            {
                calendar.setNightText();
            }
        }
        
        private void HandleTvSyncPacket(PacketReader reader)
        {
            long frame = reader.GetLong();
            
            // Find TV if not cached
            if (_tvVideoPlayer == null)
            {
                var tvControl = Object.FindObjectOfType<tvControl>();
                if (tvControl != null && tvControl.TVVP != null)
                {
                    _tvVideoPlayer = tvControl.TVVP;
                }
            }
            
            if (_tvVideoPlayer != null && _tvVideoPlayer.isPrepared)
            {
                // Only sync if we're more than 30 frames off (about 1 second at 30fps)
                long diff = System.Math.Abs(_tvVideoPlayer.frame - frame);
                if (diff > 30)
                {
                    _tvVideoPlayer.frame = frame;
                }
            }
        }
        
        private void HandlePaintingSyncPacket(PacketReader reader)
        {
            if (_paintingControl == null)
                _paintingControl = Object.FindObjectOfType<paintingControl>();
            if (_paintingControl == null) return;
            
            // Read all 6 painting IDs (3 tall + 3 square)
            int tall1 = reader.GetInt();
            int tall2 = reader.GetInt();
            int tall3 = reader.GetInt();
            int square1 = reader.GetInt();
            int square2 = reader.GetInt();
            int square3 = reader.GetInt();
            
            // Apply the painting IDs
            _paintingControl.intpaintingTall1 = tall1;
            _paintingControl.intpaintingTall2 = tall2;
            _paintingControl.intpaintingTall3 = tall3;
            _paintingControl.intpaintingSquare1 = square1;
            _paintingControl.intpaintingSquare2 = square2;
            _paintingControl.intpaintingSquare3 = square3;
            
            // Apply materials - preserve entity state if an entity is currently active
            // Without this, the periodic sync overwrites entity paintings back to normal
            var pcType = typeof(paintingControl);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            bool eTall1 = (bool)pcType.GetField("boolpaintingTall1", flags).GetValue(_paintingControl);
            bool eTall2 = (bool)pcType.GetField("boolpaintingTall2", flags).GetValue(_paintingControl);
            bool eTall3 = (bool)pcType.GetField("boolpaintingTall3", flags).GetValue(_paintingControl);
            bool eSquare1 = (bool)pcType.GetField("boolpaintingSquare1", flags).GetValue(_paintingControl);
            bool eSquare2 = (bool)pcType.GetField("boolpaintingSquare2", flags).GetValue(_paintingControl);
            bool eSquare3 = (bool)pcType.GetField("boolpaintingSquare3", flags).GetValue(_paintingControl);
            
            _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall1, tall1, eTall1, true);
            _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall2, tall2, eTall2, true);
            _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall3, tall3, eTall3, true);
            _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare1, square1, eSquare1, false);
            _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare2, square2, eSquare2, false);
            _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare3, square3, eSquare3, false);
        }
        
        private void HandlePaintingFlashPacket(PacketReader reader)
        {
            int paintingId = reader.GetInt();
            _isReceivingPaintingFlash = true;
            _paintingControl?.onPaintingFlash(paintingId);
            _isReceivingPaintingFlash = false;
        }
        
        private void HandleMonsterSyncPacket(PacketReader reader)
        {
            // Only non-controllers receive monster sync
            if (_steam.IsHost || _hostDiedInLevel) return;
            
            // Find monsters if not cached
            FindMonsters();
            
            // Sparky: STATE only (triggers at same time), position runs locally
            bool hasSparky = reader.GetBool();
            if (hasSparky)
            {
                int sparkyState = reader.GetInt();
                
                if (_sparky != null)
                {
                    // Only sync state if it would START an attack (state 2 = hunt)
                    // Never overwrite local state with a "lesser" state - local AI handles retreat/wander
                    var stateField = typeof(sparkyBrain).GetField("currentState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    int localState = stateField != null ? (int)stateField.GetValue(_sparky) : 1;
                    
                    // Only apply if host is triggering hunt and we're not already hunting/retreating
                    if (sparkyState == 2 && localState == 1)
                    {
                        stateField?.SetValue(_sparky, sparkyState);
                    }
                    
                    // NavMeshAgent stays ENABLED - Sparky runs locally, chasing THIS player
                }
            }
            
            // Jeff: STATE only (triggers at same time), position runs locally
            bool hasJeff = reader.GetBool();
            if (hasJeff)
            {
                bool bodyVisible = reader.GetBool();
                int jeffState = reader.GetInt();
                int totalFlashes = reader.GetInt();
                
                if (_jeff != null)
                {
                    var stateField = typeof(jeffBrain).GetField("currentState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    int localState = stateField != null ? (int)stateField.GetValue(_jeff) : 1;
                    
                    // Only apply trigger transitions - don't overwrite active states
                    // State 2 = hunting (triggered by attackTrigger), let local AI handle the rest
                    if (jeffState == 2 && localState == 1)
                    {
                        stateField?.SetValue(_jeff, 2);
                    }
                    
                    // NavMeshAgent stays ENABLED - Jeff runs locally, teleporting near THIS player
                }
            }
            
            // Smile: FULLY INDEPENDENT - skip sync data (just read to advance reader)
            bool hasSmile = reader.GetBool();
            if (hasSmile)
            {
                reader.GetBool(); // isChasing - discard
                reader.GetInt();  // chaseTime - discard
            }
            
            // Henry: FULLY synced position + rotation + state
            bool hasHenry = reader.GetBool();
            if (hasHenry)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                bool resetSwitch = reader.GetBool();
                bool chaseSwitch = reader.GetBool();
                
                if (_henry != null)
                {
                    // Disable NavMeshAgent on client - position is synced from host
                    if (_henry.agent != null && _henry.agent.enabled)
                    {
                        _henry.agent.enabled = false;
                    }
                    
                    _henry.transform.position = pos;
                    _henry.transform.rotation = rot;
                    
                    var resetField = typeof(henryBrain).GetField("resetSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    resetField?.SetValue(_henry, resetSwitch);
                    
                    var chaseField = typeof(henryBrain).GetField("chaseSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    chaseField?.SetValue(_henry, chaseSwitch);
                }
            }
            
            // Harold: FULLY synced position + rotation
            bool hasHarold = reader.GetBool();
            if (hasHarold)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                
                if (_harold != null)
                {
                    // Disable NavMeshAgent on client - position is synced from host
                    if (_harold.agent != null && _harold.agent.enabled)
                    {
                        _harold.agent.enabled = false;
                    }
                    
                    _harold.transform.position = pos;
                    _harold.transform.rotation = rot;
                }
            }
            
            // Clown: FULLY synced which clown is active
            bool hasClown = reader.GetBool();
            if (hasClown)
            {
                int activeClown = reader.GetInt();
                if (_clown != null)
                {
                    if (_clown.clown1 != null) _clown.clown1.SetActive(activeClown == 0);
                    if (_clown.clown2 != null) _clown.clown2.SetActive(activeClown == 1);
                    if (_clown.clown3 != null) _clown.clown3.SetActive(activeClown == 2);
                    if (_clown.clown4 != null) _clown.clown4.SetActive(activeClown == 3);
                    if (_clown.clown5 != null) _clown.clown5.SetActive(activeClown == 4);
                    if (_clown.clown6 != null) _clown.clown6.SetActive(activeClown == 5);
                    if (_clown.clown7 != null) _clown.clown7.SetActive(activeClown == 6);
                }
            }
        }
        
        private void HandleJeffFlashPacket()
        {
            _isReceivingJeffFlash = true;
            _jeff?.onFlash();
            _isReceivingJeffFlash = false;
        }
        
        private void HandleBatterySyncPacket(int peerId, PacketReader reader)
        {
            int locationId = reader.GetInt();
            bool inBackpack = reader.GetBool();
            float charge = reader.GetFloat();
            
            if (!_remoteBatteryStates.ContainsKey(peerId))
            {
                _remoteBatteryStates[peerId] = new RemoteBatteryState();
            }
            _remoteBatteryStates[peerId].LocationID = locationId;
            _remoteBatteryStates[peerId].InBackpack = inBackpack;
            _remoteBatteryStates[peerId].Charge = charge;
        }
        
        // (Old handler removed — HandlePuzzleComplete is the active handler)
        
        private void HandlePuzzleBlockPacket(PacketReader reader)
        {
            int blockNumber = reader.GetInt();
            int blockIdValue = reader.GetInt();
            // Apply puzzle block state
        }
        
        private void HandleClownHonkPacket()
        {
            // Find clown nose and play honk sound
            var clownNoseObj = Object.FindObjectOfType<clownNose>();
            if (clownNoseObj != null && clownNoseObj.honkSound != null)
            {
                _isReceivingHonk = true;
                clownNoseObj.honkSound.Play();
                _isReceivingHonk = false;
            }
        }
        
        private void HandleVentSoundPacket(PacketReader reader)
        {
            HandleVentSound(reader);
        }
        
        private void HandleInteractionLockPacket(int peerId, PacketReader reader)
        {
            string interactionId = reader.GetString();
            bool locked = reader.GetBool();
            
            if (locked)
            {
                _interactionLocks[interactionId] = peerId; // Track which peer holds it
                _lockTimestamps[interactionId] = Time.time;
            }
            else
            {
                _interactionLocks.Remove(interactionId);
                _lockTimestamps.Remove(interactionId);
            }
        }
        
        private void HandleExitDoorProgressPacket(PacketReader reader)
        {
            int timer = reader.GetInt();
            int requiredTime = reader.GetInt();
            
            // Don't apply if WE are the one charging (our local timer is authoritative)
            if (BackpackControl.batteryLocationID == 100 && BackpackControl.batteryCharge >= 0.1f)
                return;
            
            if (_leaveDoor == null)
                _leaveDoor = Object.FindObjectOfType<LeaveDoorControl>();
            
            if (_leaveDoor != null)
            {
                var timerField = typeof(LeaveDoorControl).GetField("doorLeaveTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                timerField?.SetValue(_leaveDoor, timer);
                
                var lockField = typeof(LeaveDoorControl).GetField("loadingBarLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                lockField?.SetValue(_leaveDoor, timer > 0);
                
                // Update the fill visual
                if (_leaveDoor.fillImg != null && requiredTime > 0)
                {
                    _leaveDoor.fillImg.fillAmount = (float)timer / (float)requiredTime;
                }
                
                // Show the fill icon if charging
                if (_leaveDoor.fillIcon != null)
                {
                    _leaveDoor.fillIcon.SetActive(timer > 0);
                }
                
                // Update the percentage text via reflection (puzzleCount is TMP_Text)
                var puzzleCountField = typeof(LeaveDoorControl).GetField("puzzleCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (puzzleCountField != null && timer > 0 && requiredTime > 0)
                {
                    var puzzleCount = puzzleCountField.GetValue(_leaveDoor);
                    if (puzzleCount != null)
                    {
                        var textProp = puzzleCount.GetType().GetProperty("text");
                        if (textProp != null)
                        {
                            int percent = (int)System.Math.Round((float)timer / (float)requiredTime * 100f);
                            if (timer >= requiredTime)
                            {
                                textProp.SetValue(puzzleCount, "100%");
                                // Enable the leave hitbox when fully charged
                                if (_leaveDoor.doorLeaveHitbox != null)
                                    _leaveDoor.doorLeaveHitbox.SetActive(true);
                                _exitDoorFullyCharged = true;
                            }
                            else
                            {
                                textProp.SetValue(puzzleCount, percent.ToString() + "%");
                            }
                        }
                    }
                }
                
                // Hide the puzzle count icons when showing percentage
                if (timer > 0)
                {
                    if (_leaveDoor.greenIcon != null) _leaveDoor.greenIcon.SetActive(false);
                    if (_leaveDoor.redIcon != null) _leaveDoor.redIcon.SetActive(false);
                }
            }
        }
        
        private void HandleCrankSyncPacket(PacketReader reader)
        {
            bool hasBattery = reader.GetBool();
            float charge = reader.GetFloat();
            Quaternion rotation = ReadQuaternion(reader);
            
            _remoteCrankHasBattery = hasBattery;
            _remoteCrankChargeTarget = charge;
            _remoteCrankRotTarget = rotation;
            
            // If battery just appeared in crank, snap display to current value (no lerp from 0)
            if (hasBattery && _remoteCrankChargeDisplay < 0.01f)
                _remoteCrankChargeDisplay = charge;
            
            // If battery removed, snap to 0
            if (!hasBattery)
            {
                _remoteCrankChargeTarget = 0f;
                _remoteCrankChargeDisplay = 0f;
            }
        }
        
        private void HandleDeathGhostPacket(int peerId, PacketReader reader)
        {
            bool isGhost = reader.GetBool();
            int deathType = reader.GetInt();
            
            Plugin.Log.LogInfo($"[Death] Received death packet from peer {peerId}: isGhost={isGhost}, deathType={deathType}");
            
            if (_remotePlayers.TryGetValue(peerId, out var remote))
            {
                remote.SetGhostState(isGhost);
                Plugin.Log.LogInfo($"[Death] Set peer {peerId} ghost state to {isGhost}");
            }
            
            // Clear remote player's battery state when they die
            // This prevents their battery from blocking slots for the surviving player
            if (isGhost && _remoteBatteryStates.ContainsKey(peerId))
            {
                _remoteBatteryStates[peerId].LocationID = 0;
                _remoteBatteryStates[peerId].Charge = 0f;
                _remoteBatteryStates[peerId].LeftHolding = false;
                _remoteBatteryStates[peerId].RightHolding = false;
                _remoteBatteryStates[peerId].InBackpack = false;
                Plugin.LogDebug($"[Death] Cleared battery state for dead peer {peerId}");
            }
            
            // Clear interaction locks held by the dead player specifically
            if (isGhost)
            {
                var peerLocks = new List<string>();
                foreach (var kvp in _interactionLocks)
                {
                    if (kvp.Value == peerId)
                        peerLocks.Add(kvp.Key);
                }
                foreach (var lockId in peerLocks)
                {
                    _interactionLocks.Remove(lockId);
                    _lockTimestamps.Remove(lockId);
                }
                if (peerLocks.Count > 0)
                    Plugin.LogDebug($"[Death] Cleared {peerLocks.Count} interaction locks from dead peer {peerId}");
            }
            
            // Also clear crank state if they had battery there
            _remoteCrankHasBattery = false;
            
            // Check if ALL players are now dead (including us)
            // Only go Home if we're a ghost AND every remote player is also a ghost
            if (_localIsGhost && isGhost)
            {
                bool anyoneAlive = false;
                foreach (var kvp in _remotePlayers)
                {
                    if (!kvp.Value.IsGhost) { anyoneAlive = true; break; }
                }
                
                if (!anyoneAlive)
                {
                    string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    if (currentScene.Contains("Night"))
                    {
                        Plugin.Log.LogInfo("[Ghost] All players dead - scheduling Home load");
                        _pendingHomeLoad = true;
                        _pendingHomeLoadTimer = 3.0f;
                        return;
                    }
                }
            }
            
            Plugin.LogDebug($"[Death] Partner death handled - LocalGhost={_localIsGhost}, RemoteGhost={isGhost}");
            
            // Partner died - if we're still alive in a Night level, start sending spectate frames
            string currentScene2 = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (isGhost && !_localIsGhost && currentScene2.Contains("Night"))
            {
                MPManager.Instance?.Spectate?.StartSending();
                
                // If we're the client and the host died, we need to take over monster control
                // This prevents monsters from despawning/freezing when host leaves
                if (!_steam.IsHost)
                {
                    _hostDiedInLevel = true;
                    Plugin.Log.LogInfo("[Client] Host died - taking over monster control!");
                    
                    // Re-enable monster AI by finding and re-initializing them
                    FindMonsters();
                    
                    // Re-enable NavMeshAgents that might have been disabled
                    if (_sparky != null)
                    {
                        var agent = _sparky.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null) agent.enabled = true;
                    }
                    if (_jeff != null)
                    {
                        var agent = _jeff.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null) agent.enabled = true;
                    }
                    if (_smile != null)
                    {
                        var agent = _smile.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null) agent.enabled = true;
                    }
                    if (_henry != null)
                    {
                        var agent = _henry.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null) agent.enabled = true;
                    }
                    if (_harold != null)
                    {
                        var agent = _harold.GetComponent<UnityEngine.AI.NavMeshAgent>();
                        if (agent != null) agent.enabled = true;
                    }
                }
            }
        }
        
        /// <summary>
        /// Send data to all peers via Steam
        /// </summary>
        private void SendToAllPeers(bool reliable = true)
        {
            if (_steam == null || !_steam.IsRunning) return;
            byte[] data = _writer.GetBytes();
            _steam.SendToAll(data, reliable);
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"OnSceneLoaded: {scene.name}, IsGhost={_localIsGhost}");
            
            // Clean up minimap friend indicator
            MinimapFriendPatch.Cleanup();
            
            // Clear all interaction locks on scene change
            ClearAllLocks();
            
            // Reset spawn point tracking on scene change
            _spawnPointCaptured = false;
            
            // Reset host died flag on scene change (new level = fresh start)
            _hostDiedInLevel = false;
            
            // Handle ghost scene reload
            if (_localIsGhost && _pendingGhostTeleport && scene.name.Contains("Night"))
            {
                Plugin.Log.LogInfo("[Ghost] Scene loaded as ghost, preparing teleport");
                HandleGhostSceneLoaded();
            }
            
            // Reset ghost state when entering Home (normal exit or disconnect)
            if (scene.name.Equals("Home", System.StringComparison.OrdinalIgnoreCase))
            {
                if (_localIsGhost)
                {
                    Plugin.Log.LogInfo("[Ghost] Entered Home, resetting ghost state");
                    ResetGhostState();
                }
                _pendingPuzzleInit = null; // Clear stale puzzle data when leaving a Night level
            }
            
            // Stop spectate when leaving a Night level or entering Home
            if (scene.name.Equals("Home", System.StringComparison.OrdinalIgnoreCase) || 
                !scene.name.Contains("Night"))
            {
                MPManager.Instance?.Spectate?.StopSending();
                MPManager.Instance?.Spectate?.StopReceiving();
            }
            
            // Only process if this is actually a new scene
            if (scene.name == _lastScene && !IsLoadingFromSync)
            {
                // Still need to recreate remote players — Unity destroys all GameObjects on scene load
                if (_steam != null && _steam.IsRunning && _connectedPeerIds.Count > 0)
                {
                    foreach (var remote in _remotePlayers.Values)
                        remote.Destroy();
                    _remotePlayers.Clear();
                    _recreateRemotePlayersNextFrame = true;
                }
                return;
            }
            
            Plugin.Log.LogInfo($"Scene loaded: {scene.name}");
            IsLoadingFromSync = false;
            _lastScene = scene.name;
            
            // Destroy old remote player visuals (they're invalid in new scene)
            foreach (var remote in _remotePlayers.Values)
            {
                remote.Destroy();
            }
            _remotePlayers.Clear();
            
            // Clear remote battery states
            _remoteBatteryStates.Clear();
            
            // Reset puzzle sync state for new scene
            _puzzleMaster = null;
            _puzzleInitSent = false;
            _puzzleInitStartWaited = false;
            _puzzleInitApplied = false;
            _puzzleInitReapplyTimer = 0f;
            _puzzleInitCompletedStates = null;
            _puzzleInitActiveStates = null;
            _completedPuzzleIDs.Clear();
            
            // Clear interaction locks
            ClearAllLocks();
            
            // NOTE: Do NOT clear _pendingPuzzleInit here - it may have arrived before the scene loaded
            // and we need it to be applied once PuzzleMaster is available in the new scene
            
            // Recreate remote players for any connected peers after a short delay
            // (need to wait for scene to fully load so we can find controller models)
            if (_steam != null && _steam != null && _steam.IsRunning)
            {
                // Use a coroutine-like approach with Update counter
                _recreateRemotePlayersNextFrame = true;
            }
        }
        
        private bool _recreateRemotePlayersNextFrame = false;
        
        public void Cleanup()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearRemotePlayers();
            ClearAllLocks();
            ResetGhostState();
            _connectedPeerIds.Clear();
            _remoteBatteryStates.Clear();
            _pendingPuzzleInit = null;
            Plugin.Log.LogInfo("PlayerSync cleaned up");
        }
        private int _updateCount = 0;
        
        public void Update()
        {
            _updateCount++;
            
            if (_steam == null) return;
            
            // Smooth crank charge interpolation every frame
            UpdateCrankInterpolation();
            
            // Clear the dying flag from previous frame
            IsDyingThisFrame = false;
            
            // Send pings periodically to measure latency
            if (_steam.IsRunning && _steam.IsConnected && Time.realtimeSinceStartup - _lastPingTime > PING_INTERVAL)
            {
                _lastPingTime = Time.realtimeSinceStartup;
                SendPingToAll();
            }
            
            // Process pending Home load (when both players are ghosts)
            // Delayed to let the jumpscare animation finish before cutting to Home
            if (_pendingHomeLoad)
            {
                _pendingHomeLoadTimer -= Time.deltaTime;
                if (_pendingHomeLoadTimer <= 0f)
                {
                    _pendingHomeLoad = false;
                    
                    // Re-verify all partners are still dead before going Home
                    // The situation may have changed since we scheduled this
                    bool stillAllDead = true;
                    foreach (var kvp in _remotePlayers)
                    {
                        if (!kvp.Value.IsGhost) { stillAllDead = false; break; }
                    }
                    
                    if (stillAllDead && _localIsGhost)
                    {
                        string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                        if (currentScene.Contains("Night"))
                        {
                            Plugin.Log.LogInfo("[Ghost] EXECUTING DEFERRED HOME LOAD - all players dead");
                            ResetGhostState();
                            IsLoadingFromSync = true;
                            UnityEngine.SceneManagement.SceneManager.LoadScene("Home");
                            return;
                        }
                    }
                }
            }
            
            // Process pending scene load with fade delay (deferred from network callback)
            if (_pendingSceneLoad != null)
            {
                // Add a small delay before loading to let any fade effects start
                if (_sceneLoadDelayTimer > 0)
                {
                    _sceneLoadDelayTimer -= Time.deltaTime;
                    return; // Wait for delay
                }
                
                string sceneToLoad = _pendingSceneLoad;
                _pendingSceneLoad = null;
                _sceneLoadDelayTimer = 0f;
                
                Plugin.Log.LogInfo($"[Client] EXECUTING DEFERRED SCENE LOAD: {sceneToLoad}");
                try
                {
                    // Try to trigger fade effect before loading
                    TriggerClientFade();
                    
                    SceneManager.LoadScene(sceneToLoad);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"[Client] SceneManager.LoadScene FAILED: {ex}");
                }
            }
            
            // Recreate remote players after scene load (delayed by one frame)
            if (_recreateRemotePlayersNextFrame)
            {
                _recreateRemotePlayersNextFrame = false;
                RecreateRemotePlayers();
            }
            
            // Update ghost teleport if pending
            UpdateGhostTeleport();
            
            // Ghost fall-through-map recovery (only after the delayed teleport has fired)
            if (_localIsGhost && _spawnPointCaptured && _mainCamera != null && _ghostTeleportDelay <= 0f)
            {
                if (_mainCamera.transform.position.y < -5f)
                {
                    Plugin.Log.LogInfo($"[Ghost] Fell through map (Y={_mainCamera.transform.position.y:F1}), re-teleporting to spawn");
                    TeleportLocalPlayer(_levelSpawnPoint);
                }
            }
            
            // Process pending ghost puzzle state restore (after PuzzleMaster.Start() has run)
            if (_pendingGhostPuzzleRestore)
            {
                _ghostPuzzleRestoreFrames--;
                if (_ghostPuzzleRestoreFrames <= 0)
                {
                    _pendingGhostPuzzleRestore = false;
                    RestoreGhostPuzzleState();
                }
            }
            
            // ALWAYS update remote player visuals (interpolation + hand upgrade retries)
            // This must happen even if networking is temporarily disconnected
            foreach (var remote in _remotePlayers.Values)
            {
                remote.UpdateInterpolation();
            }
            
            // Skip if not in a valid networking state
            // We need to be running AND either be the host OR be connected to peers
            if (!_steam.IsRunning) return;
            if (!_steam.IsHost && !_steam.IsConnected && !_steam.IsInLobby) return;
            
            // Capture spawn point early in the level (before player moves much)
            if (!_spawnPointCaptured && _updateCount > 10)
            {
                CaptureSpawnPoint();
            }
            
            // Log every 5 seconds (debug only)
            if (_updateCount % 300 == 0)
            {
                Plugin.LogDebug($"PlayerSync.Update: remotePlayers={_remotePlayers.Count}, ping={AveragePing:F0}ms");
            }
            
            // Cache references if needed
            if (_gorillaPlayer == null && _handControl == null && _mainCamera == null && _ovrHead == null)
            {
                _gorillaPlayer = Player.Instance;
                _handControl = Object.FindObjectOfType<HandControl>();
                _moveController = Object.FindObjectOfType<MoveTypeController>();
                _mainCamera = Camera.main;
                
                // Try to find OVRCameraRig for hand tracking
                foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                {
                    var type = mb.GetType();
                    if (type.Name == "OVRCameraRig")
                    {
                        var centerEye = type.GetProperty("centerEyeAnchor")?.GetValue(mb) as Transform;
                        var leftHand = type.GetProperty("leftHandAnchor")?.GetValue(mb) as Transform;
                        var rightHand = type.GetProperty("rightHandAnchor")?.GetValue(mb) as Transform;
                        
                        if (centerEye != null && leftHand != null && rightHand != null)
                        {
                            _ovrHead = centerEye;
                            _ovrLeftHand = leftHand;
                            _ovrRightHand = rightHand;
                        }
                        break;
                    }
                }
                
                // Try BackpackControl for hand references
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    var backpack = Object.FindObjectOfType<BackpackControl>();
                    if (backpack != null)
                    {
                        if (backpack.leftHand != null)
                            _ovrLeftHand = backpack.leftHand.transform;
                        if (backpack.rightHand != null)
                            _ovrRightHand = backpack.rightHand.transform;
                        if (backpack.cam != null && _ovrHead == null)
                            _ovrHead = backpack.cam.transform;
                    }
                }
                
                // Try XR controllers directly
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    var controllers = Object.FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.ActionBasedController>();
                    foreach (var controller in controllers)
                    {
                        string nameLower = controller.gameObject.name.ToLower();
                        if (nameLower.Contains("left") && _ovrLeftHand == null)
                            _ovrLeftHand = controller.transform;
                        else if (nameLower.Contains("right") && _ovrRightHand == null)
                            _ovrRightHand = controller.transform;
                    }
                }
                
                // Try crankControl for hand references
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    var crank = Object.FindObjectOfType<crankControl>();
                    if (crank != null)
                    {
                        if (crank.handLeft != null && _ovrLeftHand == null)
                            _ovrLeftHand = crank.handLeft.transform;
                        if (crank.handRight != null && _ovrRightHand == null)
                            _ovrRightHand = crank.handRight.transform;
                    }
                }
                
                // Log final tracking results (one line)
                Plugin.Log.LogInfo($"[Tracking] Head={(_ovrHead != null ? _ovrHead.name : "NONE")}, LHand={(_ovrLeftHand != null ? _ovrLeftHand.name : "NONE")}, RHand={(_ovrRightHand != null ? _ovrRightHand.name : "NONE")}");
                
                if (_gorillaPlayer != null)
                    _playerTransform = _gorillaPlayer.transform;
            }
            
            // Need at least one reference to sync
            if (_gorillaPlayer == null && _handControl == null && _mainCamera == null)
                return;
            
            // Send position updates
            if (Time.time - _lastSyncTime >= _syncInterval)
            {
                SendPositionUpdate();
                _lastSyncTime = Time.time;
            }
            
            // Check flashlight state changes
            CheckFlashlightState();
            
            // Check scene changes (host only sends)
            CheckSceneChange();
            
            // Check calendar changes (host only)
            CheckCalendarChange();
            
            // Check TV sync (host only)
            CheckTvSync();
            
            // Check painting sync (host only)
            CheckPaintingSync();
            
            // Monster sync - only for SYNCED monsters (Henry, Harold, Smile, Clown)
            // Jeff and Sparky run locally per player
            CheckMonsterSync();
            
            // Check battery sync (shared battery - host authoritative)
            CheckBatterySync();
            
            // Check crank sync (so partner can see battery in crank and charge display)
            CheckCrankSync();
            
            // Check puzzle init sync (host only, once per scene)
            CheckPuzzleInitSync();
            
            // Try to apply pending puzzle init (client only)
            if (_pendingPuzzleInit != null && !_steam.IsHost)
            {
                TryApplyPuzzleInit();
            }
            
            // Re-apply puzzle indicators for a few seconds after init to catch late resets
            ReapplyPuzzleIndicators();
            
            // Check exit door progress sync (host only)
            CheckExitDoorSync();
            
            // Check end scene progress sync (host only)
            CheckEndSceneSync();
            
            // Check ear covering sync (both players)
            CheckEarCoveringSync();
            
            // Clean up expired interaction locks
            CleanupExpiredLocks();
        }
        
        private void CheckCalendarChange()
        {
            // Only host syncs calendar selection
            if (!_steam.IsHost) return;
            
            int currentNight = calenderControl.nightSelected;
            if (currentNight != _lastNightSelected)
            {
                _lastNightSelected = currentNight;
                SendNightSelected(currentNight);
            }
        }
        
        private void CheckTvSync()
        {
            // Only host syncs TV
            if (!_steam.IsHost) return;
            
            // Ghost host shouldn't sync TV — fresh scene state would desync client
            if (_localIsGhost) return;
            
            // Only sync periodically
            if (Time.time - _lastTvSyncTime < _tvSyncInterval) return;
            _lastTvSyncTime = Time.time;
            
            // Find TV if not cached
            if (_tvVideoPlayer == null)
            {
                var tvControl = Object.FindObjectOfType<tvControl>();
                if (tvControl != null && tvControl.TVVP != null)
                {
                    _tvVideoPlayer = tvControl.TVVP;
                }
                else
                {
                    // Also try MoviePlayerSample
                    var moviePlayer = Object.FindObjectOfType<MoviePlayerSample>();
                    if (moviePlayer != null)
                    {
                        _tvVideoPlayer = moviePlayer.GetComponent<UnityEngine.Video.VideoPlayer>();
                        if (_tvVideoPlayer != null)
                        {
                            Plugin.LogDebug($"Found TV VideoPlayer");
                        }
                    }
                }
            }
            
            if (_tvVideoPlayer != null && _tvVideoPlayer.isPlaying)
            {
                SendTvSync(_tvVideoPlayer.frame);
            }
        }
        
        private void SendTvSync(long frame)
        {
            _writer.Reset();
            _writer.Put(PACKET_TV_SYNC);
            _writer.Put(frame);
            SendToAllPeers(true);
        }
        
        private void CheckPaintingSync()
        {
            // Only host syncs paintings
            if (!_steam.IsHost) return;
            
            // Ghost host shouldn't sync paintings — client handles their own after host dies
            if (_localIsGhost) return;
            
            // Only sync periodically
            if (Time.time - _lastPaintingSyncTime < _paintingSyncInterval) return;
            _lastPaintingSyncTime = Time.time;
            
            // Find painting controller if not cached
            if (_paintingControl == null)
            {
                _paintingControl = Object.FindObjectOfType<paintingControl>();
            }
            
            if (_paintingControl != null)
            {
                SendPaintingSync();
            }
        }
        
        private void SendPaintingSync()
        {
            _writer.Reset();
            _writer.Put(PACKET_PAINTING_SYNC);
            
            // Send painting IDs (which image is shown)
            _writer.Put(_paintingControl.intpaintingTall1);
            _writer.Put(_paintingControl.intpaintingTall2);
            _writer.Put(_paintingControl.intpaintingTall3);
            _writer.Put(_paintingControl.intpaintingSquare1);
            _writer.Put(_paintingControl.intpaintingSquare2);
            _writer.Put(_paintingControl.intpaintingSquare3);
            
            SendToAllPeers(true);
        }
        
        private void SendNightSelected(int night)
        {
            _writer.Reset();
            _writer.Put(PACKET_NIGHT_SELECTED);
            _writer.Put(night);
            SendToAllPeers(true);
            Plugin.Log.LogInfo($"[Host] Sent night selection: {night}");
        }
        
        private void CheckSceneChange()
        {
            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene != _lastScene)
            {
                Plugin.Log.LogInfo($"Scene changed: {_lastScene} -> {currentScene}");
                string previousScene = _lastScene;
                _lastScene = currentScene;
                
                // Only host broadcasts scene changes, and only if not loading from sync
                if (_steam.IsHost && !IsLoadingFromSync)
                {
                    SendSceneChange(currentScene);
                }
                
                // Reset cached references since they're invalid in new scene
                _gorillaPlayer = null;
                _handControl = null;
                _mainCamera = null;
                _moveController = null;
                _ovrHead = null;
                _ovrLeftHand = null;
                _ovrRightHand = null;
                _localFlashlight = null;
                _localFlashlightControl = null;
                _tvVideoPlayer = null;
                _paintingControl = null;
                _sparky = null;
                _jeff = null;
                _smile = null;
                _henry = null;
                _harold = null;
                _clown = null;
                _puzzleMaster = null;
                _puzzleInitSent = false;
                _puzzleInitStartWaited = false;
                _puzzleInitApplied = false;
                _puzzleInitReapplyTimer = 0f;
                _puzzleInitCompletedStates = null;
                _puzzleInitActiveStates = null;
                _completedPuzzleIDs.Clear(); // Clear puzzle completion tracking on scene change
                _leaveDoor = null;
                _lastDoorLeaveTimer = 0;
                _exitDoorFullyCharged = false;
                _crankControl = null;
                _lastCrankCharge = -1f;
                _lastCrankHasBattery = false;
                
                // FALLBACK: If OnSceneLoaded didn't fire (which can happen when loading from
                // network callbacks), recreate remote players and clear stale state
                if (_steam != null && _steam.IsRunning && _connectedPeerIds.Count > 0)
                {
                    // Always clear and recreate — Unity destroys GameObjects on scene load
                    foreach (var remote in _remotePlayers.Values)
                        remote.Destroy();
                    _remotePlayers.Clear();
                    _remoteBatteryStates.Clear();
                    ClearAllLocks();
                    
                    // Schedule recreation for next frame
                    _recreateRemotePlayersNextFrame = true;
                }
            }
        }
        
        private bool AnyRemotePlayerValid()
        {
            foreach (var remote in _remotePlayers.Values)
            {
                if (remote.Head != null) return true;
            }
            return false;
        }
        
        private void ClearRemotePlayers()
        {
            foreach (var remote in _remotePlayers.Values)
            {
                remote.Destroy();
            }
            _remotePlayers.Clear();
        }
        
        private void RecreateRemotePlayers()
        {
            if (_steam == null || _steam == null) return;
            
            // Use our tracked peer IDs (more reliable than ConnectedPeerList during scene transitions)
            foreach (var peerId in _connectedPeerIds)
            {
                if (!_remotePlayers.ContainsKey(peerId))
                {
                    var remotePlayer = new RemotePlayer(peerId);
                    _remotePlayers[peerId] = remotePlayer;
                }
                else
                {
                    // Try to upgrade visuals if we now have access to real models
                    _remotePlayers[peerId].TryUpgradeVisuals();
                }
            }
            
            Plugin.Log.LogInfo($"Remote players recreated: {_remotePlayers.Count}");
        }
        
        public void SendSceneChange(string sceneName)
        {
            _writer.Reset();
            _writer.Put(PACKET_SCENE_CHANGE);
            _writer.Put(sceneName);
            // Include night selection so client has it BEFORE scene loads
            _writer.Put(calenderControl.nightSelected);
            SendToAllPeers(true);
            Plugin.Log.LogInfo($"Sent scene change: {sceneName}, night={calenderControl.nightSelected}");
        }
        
        private void CheckFlashlightState()
        {
            bool currentState = false;
            bool foundFlashlight = false;
            
            // Try FlashlightControl first (the main one in Crawlspace 2)
            if (_localFlashlightControl == null)
            {
                _localFlashlightControl = Object.FindObjectOfType<FlashlightControl>();
            }
            
            if (_localFlashlightControl != null && _localFlashlightControl.flashlight != null)
            {
                currentState = _localFlashlightControl.flashlight.activeSelf;
                foundFlashlight = true;
            }
            
            // Fallback: Try Flashlight class (used by FlashlightController for hand tracking)
            if (!foundFlashlight)
            {
                if (_localFlashlight == null)
                {
                    _localFlashlight = Object.FindObjectOfType<Flashlight>();
                }
                
                if (_localFlashlight != null && _localFlashlight.spotlight != null)
                {
                    currentState = _localFlashlight.spotlight.enabled;
                    foundFlashlight = true;
                }
            }
            
            if (!foundFlashlight)
            {
                return; // No flashlight found
            }
            
            // Check if state changed
            if (currentState != _lastFlashlightState)
            {
                _lastFlashlightState = currentState;
                SendFlashlightState(currentState);
            }
        }
        
        private void SendFlashlightState(bool isOn)
        {
            _writer.Reset();
            _writer.Put(PACKET_FLASHLIGHT);
            _writer.Put(isOn);
            SendToAllPeers(true);
        }

        private int _sendCount = 0;
        
        private void CaptureHandPoses(ref float leftGrip, ref float leftTrigger, ref float rightGrip, ref float rightTrigger)
        {
            // Try to find HandAnim components on the local player's hands
            var backpack = Object.FindObjectOfType<BackpackControl>();
            if (backpack != null)
            {
                // Get grip and trigger values directly from ActionBasedController
                if (backpack.controllerLeft != null)
                {
                    try
                    {
                        leftGrip = backpack.controllerLeft.selectActionValue.action.ReadValue<float>();
                        leftTrigger = backpack.controllerLeft.uiPressActionValue.action.ReadValue<float>();
                    }
                    catch (Exception)
                    {
                        // Silently ignore - hand pose read can fail during scene transitions
                    }
                }
                
                if (backpack.controllerRight != null)
                {
                    try
                    {
                        rightGrip = backpack.controllerRight.selectActionValue.action.ReadValue<float>();
                        rightTrigger = backpack.controllerRight.uiPressActionValue.action.ReadValue<float>();
                    }
                    catch (Exception)
                    {
                        // Silently ignore
                    }
                }
            }
        }
        
        private void SendPositionUpdate()
        {
            _writer.Reset();
            _writer.Put(PACKET_PLAYER_POSITION);
            
            Vector3 headPos, leftHandPos, rightHandPos;
            Quaternion headRot, leftHandRot, rightHandRot;
            bool isStanding = _moveController != null && _moveController.debugSwitch;
            
            _sendCount++;
            
            // Track which source we're using
            string source = "none";
            
            // Get positions from available source (priority order)
            // Priority: OVR hands (from BackpackControl or OVRCameraRig) > GorillaPlayer > HandControl > Camera fallback
            if (_ovrHead != null && _ovrLeftHand != null && _ovrRightHand != null)
            {
                // Use OVR/BackpackControl hands for proper VR tracking
                source = "OVR/BackpackControl";
                headPos = _ovrHead.position;
                headRot = _ovrHead.rotation;
                leftHandPos = _ovrLeftHand.position;
                leftHandRot = _ovrLeftHand.rotation;
                rightHandPos = _ovrRightHand.position;
                rightHandRot = _ovrRightHand.rotation;
            }
            else if (_gorillaPlayer != null && _gorillaPlayer.leftHandTransform != null && _gorillaPlayer.rightHandTransform != null)
            {
                source = "GorillaPlayer";
                headPos = _gorillaPlayer.headCollider.transform.position;
                headRot = _gorillaPlayer.headCollider.transform.rotation;
                leftHandPos = _gorillaPlayer.leftHandTransform.position;
                leftHandRot = _gorillaPlayer.leftHandTransform.rotation;
                rightHandPos = _gorillaPlayer.rightHandTransform.position;
                rightHandRot = _gorillaPlayer.rightHandTransform.rotation;
            }
            else if (_handControl != null && _handControl.LTarget != null && _handControl.RTarget != null)
            {
                source = "HandControl";
                headPos = _handControl.Head.transform.position;
                headRot = _handControl.Head.transform.rotation;
                leftHandPos = _handControl.LTarget.transform.position;
                leftHandRot = _handControl.LTarget.transform.rotation;
                rightHandPos = _handControl.RTarget.transform.position;
                rightHandRot = _handControl.RTarget.transform.rotation;
            }
            else if (_mainCamera != null)
            {
                // Fallback to main camera only (no hand tracking)
                source = "MainCamera-NoHands";
                headPos = _mainCamera.transform.position;
                headRot = _mainCamera.transform.rotation;
                // No hand data available, use head position offset
                leftHandPos = headPos + _mainCamera.transform.right * -0.3f + _mainCamera.transform.forward * 0.2f;
                leftHandRot = headRot;
                rightHandPos = headPos + _mainCamera.transform.right * 0.3f + _mainCamera.transform.forward * 0.2f;
                rightHandRot = headRot;
            }
            else
            {
                return; // No source available
            }
            
            // Body position (not used anymore but still sent for compatibility)
            Vector3 bodyPos = headPos - Vector3.up * 0.5f;
            Quaternion bodyRot = Quaternion.Euler(0, headRot.eulerAngles.y, 0);
            
            // Capture hand poses (grip and trigger values for animation)
            float leftGrip = 0f, leftTrigger = 0f, rightGrip = 0f, rightTrigger = 0f;
            CaptureHandPoses(ref leftGrip, ref leftTrigger, ref rightGrip, ref rightTrigger);
            
            _writer.Put(isStanding);
            WriteVector3(_writer, bodyPos);
            WriteQuaternion(_writer, bodyRot);
            WriteVector3(_writer, headPos);
            WriteQuaternion(_writer, headRot);
            WriteVector3(_writer, leftHandPos);
            WriteQuaternion(_writer, leftHandRot);
            WriteVector3(_writer, rightHandPos);
            WriteQuaternion(_writer, rightHandRot);
            
            // Send hand poses
            _writer.Put(leftGrip);
            _writer.Put(leftTrigger);
            _writer.Put(rightGrip);
            _writer.Put(rightTrigger);
            
            // Log every 100 sends (~3 seconds) - debug only
            if (_sendCount % 100 == 1)
            {
                Plugin.LogDebug($"[Send] src={source}");
            }
            
            SendToAllPeers(false);
        }

        private void WriteVector3(PacketWriter writer, Vector3 v)
        {
            writer.Put(v.x);
            writer.Put(v.y);
            writer.Put(v.z);
        }

        private void WriteQuaternion(PacketWriter writer, Quaternion q)
        {
            writer.Put(q.x);
            writer.Put(q.y);
            writer.Put(q.z);
            writer.Put(q.w);
        }

        private Vector3 ReadVector3(PacketReader reader)
        {
            return new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        private Quaternion ReadQuaternion(PacketReader reader)
        {
            return new Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }
        
        private void SendInteractionLock(string interactionId, bool locked)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_INTERACTION_LOCK);
            _writer.Put(interactionId);
            _writer.Put(locked);
            SendToAllPeers(true);
        }
        
        private void HandleSceneChange(PacketReader reader)
        {
            string sceneName = reader.GetString();
            Plugin.Log.LogInfo($"[Client] Received scene change: {sceneName}");
            
            // Only clients should load - host already loaded
            if (!_steam.IsHost)
            {
                string currentScene = SceneManager.GetActiveScene().name;
                
                if (currentScene != sceneName)
                {
                    Plugin.Log.LogInfo($"[Client] Loading scene: {sceneName}");
                    IsLoadingFromSync = true;
                    _lastScene = sceneName;
                    
                    // Start fade effect immediately
                    TriggerClientFade();
                    
                    // Set delay timer and pending scene
                    _sceneLoadDelayTimer = SCENE_LOAD_DELAY;
                    _pendingSceneLoad = sceneName;
                }
                else
                {
                    Plugin.LogDebug($"[Client] Already in scene {sceneName}, ignoring");
                }
            }
        }
        
        // Try to trigger the game's fade effect for smoother transitions
        private void TriggerClientFade()
        {
            try
            {
                // Try to find and trigger the game's fade system
                // Use reflection to avoid type name conflicts
                var fadeControlType = System.Type.GetType("fadeControl, Assembly-CSharp");
                if (fadeControlType == null)
                {
                    // Try finding it in loaded assemblies
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        fadeControlType = asm.GetType("fadeControl");
                        if (fadeControlType != null) break;
                    }
                }
                
                if (fadeControlType != null)
                {
                    var fadeObj = Object.FindObjectOfType(fadeControlType);
                    if (fadeObj != null)
                    {
                        // Try to call fade method via reflection
                        var fadeMethod = fadeControlType.GetMethod("fadeOut", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (fadeMethod != null)
                        {
                            fadeMethod.Invoke(fadeObj, null);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Client] Could not trigger fade: {ex.Message}");
            }
        }
        
        // Pending scene load - set by HandleSceneChange, processed in Update
        private string _pendingSceneLoad = null;
        
        private void HandleTvSync(PacketReader reader)
        {
            long frame = reader.GetLong();
            
            // Only clients sync to host's TV
            if (_steam.IsHost) return;
            
            // Find TV if not cached
            if (_tvVideoPlayer == null)
            {
                var tvControl = Object.FindObjectOfType<tvControl>();
                if (tvControl != null && tvControl.TVVP != null)
                {
                    _tvVideoPlayer = tvControl.TVVP;
                }
                else
                {
                    var moviePlayer = Object.FindObjectOfType<MoviePlayerSample>();
                    if (moviePlayer != null)
                    {
                        _tvVideoPlayer = moviePlayer.GetComponent<UnityEngine.Video.VideoPlayer>();
                    }
                }
            }
            
            if (_tvVideoPlayer != null)
            {
                // Only sync if we're more than 30 frames off (about 1 second at 30fps)
                long diff = System.Math.Abs(_tvVideoPlayer.frame - frame);
                if (diff > 30)
                {
                    _tvVideoPlayer.frame = frame;
                }
            }
        }
        
        private void HandlePaintingSync(PacketReader reader)
        {
            // Only clients sync from host
            if (_steam.IsHost) return;
            
            int tall1 = reader.GetInt();
            int tall2 = reader.GetInt();
            int tall3 = reader.GetInt();
            int square1 = reader.GetInt();
            int square2 = reader.GetInt();
            int square3 = reader.GetInt();
            
            // Find painting controller if not cached
            if (_paintingControl == null)
            {
                _paintingControl = Object.FindObjectOfType<paintingControl>();
            }
            
            if (_paintingControl != null)
            {
                // Update painting IDs and refresh visuals
                bool changed = false;
                
                if (_paintingControl.intpaintingTall1 != tall1)
                {
                    _paintingControl.intpaintingTall1 = tall1;
                    _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall1, tall1, false, true);
                    changed = true;
                }
                if (_paintingControl.intpaintingTall2 != tall2)
                {
                    _paintingControl.intpaintingTall2 = tall2;
                    _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall2, tall2, false, true);
                    changed = true;
                }
                if (_paintingControl.intpaintingTall3 != tall3)
                {
                    _paintingControl.intpaintingTall3 = tall3;
                    _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall3, tall3, false, true);
                    changed = true;
                }
                if (_paintingControl.intpaintingSquare1 != square1)
                {
                    _paintingControl.intpaintingSquare1 = square1;
                    _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare1, square1, false, false);
                    changed = true;
                }
                if (_paintingControl.intpaintingSquare2 != square2)
                {
                    _paintingControl.intpaintingSquare2 = square2;
                    _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare2, square2, false, false);
                    changed = true;
                }
                if (_paintingControl.intpaintingSquare3 != square3)
                {
                    _paintingControl.intpaintingSquare3 = square3;
                    _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare3, square3, false, false);
                    changed = true;
                }
                
                if (changed)
                {
                    Plugin.LogDebug($"[Client] Paintings synced from host");
                }
            }
        }
        
        // Called via Harmony patch when a painting is flashed
        public void SendPaintingFlash(int paintingId)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_PAINTING_FLASH);
            _writer.Put(paintingId);
            SendToAllPeers(true);
        }
        
        // Called when controller spawns a painting entity - sync to other player
        public void SendPaintingEntityState(paintingControl pc)
        {
            if (_steam == null || !_steam.IsRunning || !ShouldControlMonsters) return;
            
            // Get the bool states via reflection (they're private)
            var pcType = typeof(paintingControl);
            bool tall1 = (bool)pcType.GetField("boolpaintingTall1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pc);
            bool tall2 = (bool)pcType.GetField("boolpaintingTall2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pc);
            bool tall3 = (bool)pcType.GetField("boolpaintingTall3", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pc);
            bool square1 = (bool)pcType.GetField("boolpaintingSquare1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pc);
            bool square2 = (bool)pcType.GetField("boolpaintingSquare2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pc);
            bool square3 = (bool)pcType.GetField("boolpaintingSquare3", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(pc);
            
            _writer.Reset();
            _writer.Put(PACKET_PAINTING_ENTITY);
            _writer.Put(tall1);
            _writer.Put(tall2);
            _writer.Put(tall3);
            _writer.Put(square1);
            _writer.Put(square2);
            _writer.Put(square3);
            // Also send the int IDs so client can show correct image
            _writer.Put(pc.intpaintingTall1);
            _writer.Put(pc.intpaintingTall2);
            _writer.Put(pc.intpaintingTall3);
            _writer.Put(pc.intpaintingSquare1);
            _writer.Put(pc.intpaintingSquare2);
            _writer.Put(pc.intpaintingSquare3);
            
            SendToAllPeers(true);
        }
        
        private void HandlePaintingEntity(PacketReader reader)
        {
            bool tall1 = reader.GetBool();
            bool tall2 = reader.GetBool();
            bool tall3 = reader.GetBool();
            bool square1 = reader.GetBool();
            bool square2 = reader.GetBool();
            bool square3 = reader.GetBool();
            int intTall1 = reader.GetInt();
            int intTall2 = reader.GetInt();
            int intTall3 = reader.GetInt();
            int intSquare1 = reader.GetInt();
            int intSquare2 = reader.GetInt();
            int intSquare3 = reader.GetInt();
            
            if (_paintingControl == null)
            {
                _paintingControl = Object.FindObjectOfType<paintingControl>();
            }
            
            if (_paintingControl != null)
            {
                var pcType = typeof(paintingControl);
                
                // Set the bool states
                pcType.GetField("boolpaintingTall1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_paintingControl, tall1);
                pcType.GetField("boolpaintingTall2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_paintingControl, tall2);
                pcType.GetField("boolpaintingTall3", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_paintingControl, tall3);
                pcType.GetField("boolpaintingSquare1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_paintingControl, square1);
                pcType.GetField("boolpaintingSquare2", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_paintingControl, square2);
                pcType.GetField("boolpaintingSquare3", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_paintingControl, square3);
                
                // Update visuals - show entity version if bool is true
                _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall1, intTall1, tall1, true);
                _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall2, intTall2, tall2, true);
                _paintingControl.setPaintingMatSquare(_paintingControl.paintingTall3, intTall3, tall3, true);
                _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare1, intSquare1, square1, false);
                _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare2, intSquare2, square2, false);
                _paintingControl.setPaintingMatSquare(_paintingControl.paintingSquare3, intSquare3, square3, false);
            }
        }
        
        private bool _isReceivingPaintingFlash = false;
        public bool IsReceivingPaintingFlash => _isReceivingPaintingFlash;
        
        private bool _isReceivingJeffFlash = false;
        public bool IsReceivingJeffFlash => _isReceivingJeffFlash;
        
        public List<Vector3> GetRemotePlayerPositions()
        {
            var positions = new List<Vector3>();
            foreach (var remote in _remotePlayers.Values)
            {
                if (remote.Head != null)
                {
                    positions.Add(remote.Head.transform.position);
                }
            }
            return positions;
        }
        
        // Get a specific remote player's position by peer ID (for voice chat)
        public Vector3? GetRemotePlayerPosition(int peerId)
        {
            if (_remotePlayers.TryGetValue(peerId, out var remote) && remote.Head != null)
            {
                return remote.Head.transform.position;
            }
            return null;
        }
        
        // Get remote player positions excluding ghosts (for monster targeting)
        public List<Vector3> GetRemotePlayerPositionsNonGhost()
        {
            var positions = new List<Vector3>();
            foreach (var remote in _remotePlayers.Values)
            {
                if (remote.Head != null && !remote.IsGhost)
                {
                    positions.Add(remote.Head.transform.position);
                }
            }
            return positions;
        }
        
        private void CheckMonsterSync()
        {
            // Only the controller syncs monsters (host, or client if host died)
            if (!ShouldControlMonsters) return;
            
            // Ghost host shouldn't sync monsters — client took over control
            if (_localIsGhost) return;
            
            // Only sync periodically
            if (Time.time - _lastMonsterSyncTime < _monsterSyncInterval) return;
            _lastMonsterSyncTime = Time.time;
            
            // Find monsters if not cached
            FindMonsters();
            
            // Check if synced monsters (Henry/Harold) are near remote players
            CheckSyncedMonsterKills();
            
            // Send monster sync data
            SendMonsterSync();
        }
        
        private void FindMonsters()
        {
            if (_sparky == null) _sparky = Object.FindObjectOfType<sparkyBrain>();
            if (_jeff == null) _jeff = Object.FindObjectOfType<jeffBrain>();
            if (_smile == null) _smile = Object.FindObjectOfType<SmileBrain>();
            if (_henry == null) _henry = Object.FindObjectOfType<henryBrain>();
            if (_harold == null) _harold = Object.FindObjectOfType<mapEnBrain>();
            if (_clown == null) _clown = Object.FindObjectOfType<clownRandom>();
        }
        
        private void SendMonsterSync()
        {
            // Monster sync strategy:
            // - Smile, Jeff, Sparky: TRIGGER synced (spawn at same time), but run LOCALLY (each player has their own)
            // - Henry, Harold, Clown: FULLY synced (same position for all players)
            
            _writer.Reset();
            _writer.Put(PACKET_MONSTER_SYNC);
            
            // Sparky: only sync STATE (when to hunt/wander), position runs locally
            bool hasSparky = _sparky != null;
            _writer.Put(hasSparky);
            if (hasSparky)
            {
                // Sync Sparky's state (1=wander, 2=hunt, 3=retreat) - triggers at same time
                var stateField = typeof(sparkyBrain).GetField("currentState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int state = stateField != null ? (int)stateField.GetValue(_sparky) : 1;
                _writer.Put(state);
            }
            
            // Jeff: only sync STATE (when he appears), position runs locally
            bool hasJeff = _jeff != null;
            _writer.Put(hasJeff);
            if (hasJeff)
            {
                // Sync Jeff's state and body visibility - triggers at same time
                _writer.Put(_jeff.jeffBody != null && _jeff.jeffBody.activeSelf);
                var stateField = typeof(jeffBrain).GetField("currentState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int state = stateField != null ? (int)stateField.GetValue(_jeff) : 1;
                _writer.Put(state);
                
                // Sync flash count so both players need to flash
                var flashField = typeof(jeffBrain).GetField("totalFlashes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int flashes = flashField != null ? (int)flashField.GetValue(_jeff) : 0;
                _writer.Put(flashes);
            }
            
            // Smile: FULLY INDEPENDENT - no sync needed (like Sparky)
            // Each player's EnemyDifMaster triggers Smile locally
            _writer.Put(false); // hasSmile = false (keep packet format compatible)
            
            // Henry: FULLY synced position + rotation + state
            bool hasHenry = _henry != null;
            _writer.Put(hasHenry);
            if (hasHenry)
            {
                WriteVector3(_writer, _henry.transform.position);
                WriteQuaternion(_writer, _henry.transform.rotation);
                
                var resetField = typeof(henryBrain).GetField("resetSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool resetSwitch = resetField != null ? (bool)resetField.GetValue(_henry) : false;
                _writer.Put(resetSwitch);
                
                var chaseField = typeof(henryBrain).GetField("chaseSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool chaseSwitch = chaseField != null ? (bool)chaseField.GetValue(_henry) : false;
                _writer.Put(chaseSwitch);
            }
            
            // Harold: FULLY synced position + rotation
            bool hasHarold = _harold != null;
            _writer.Put(hasHarold);
            if (hasHarold)
            {
                WriteVector3(_writer, _harold.transform.position);
                WriteQuaternion(_writer, _harold.transform.rotation);
            }
            
            // Clown: FULLY synced which clown is active
            bool hasClown = _clown != null;
            _writer.Put(hasClown);
            if (hasClown)
            {
                int activeClown = -1;
                if (_clown.clown1 != null && _clown.clown1.activeSelf) activeClown = 0;
                else if (_clown.clown2 != null && _clown.clown2.activeSelf) activeClown = 1;
                else if (_clown.clown3 != null && _clown.clown3.activeSelf) activeClown = 2;
                else if (_clown.clown4 != null && _clown.clown4.activeSelf) activeClown = 3;
                else if (_clown.clown5 != null && _clown.clown5.activeSelf) activeClown = 4;
                else if (_clown.clown6 != null && _clown.clown6.activeSelf) activeClown = 5;
                else if (_clown.clown7 != null && _clown.clown7.activeSelf) activeClown = 6;
                _writer.Put(activeClown);
            }
            
            SendToAllPeers(false);
        }
        
        /// <summary>
        /// Host checks if synced monsters (Henry/Harold) are near any remote player
        /// and sends targeted kill packets. This prevents double-kills from client-side
        /// distance checks on synced monster positions.
        /// </summary>
        private float _lastTargetedKillTime = 0f;
        private void CheckSyncedMonsterKills()
        {
            if (Time.time - _lastTargetedKillTime < 1f) return;
            if (_remotePlayers.Count == 0) return;
            
            // Henry kill check
            if (_henry != null)
            {
                var resetField = typeof(henryBrain).GetField("resetSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool resetSwitch = resetField != null ? (bool)resetField.GetValue(_henry) : false;
                
                if (!resetSwitch)
                {
                    foreach (var kvp in _remotePlayers)
                    {
                        if (kvp.Value.IsGhost || kvp.Value.Head == null) continue;
                        float dist = Vector3.Distance(kvp.Value.Head.transform.position, _henry.transform.position);
                        if (dist < 0.6f)
                        {
                            Plugin.Log.LogInfo($"[Host] Henry near peer {kvp.Key} (dist={dist:F2}) - sending targeted kill");
                            SendTargetedKillTo(kvp.Key, 4);
                            _lastTargetedKillTime = Time.time;
                            return;
                        }
                    }
                }
            }
            
            // Harold kill check
            if (_harold != null)
            {
                foreach (var kvp in _remotePlayers)
                {
                    if (kvp.Value.IsGhost || kvp.Value.Head == null) continue;
                    float dist = Vector3.Distance(kvp.Value.Head.transform.position, _harold.transform.position);
                    if (dist < 0.6f)
                    {
                        Plugin.Log.LogInfo($"[Host] Harold near peer {kvp.Key} (dist={dist:F2}) - sending targeted kill");
                        SendTargetedKillTo(kvp.Key, 2);
                        _lastTargetedKillTime = Time.time;
                        return;
                    }
                }
            }
        }
        
        private void SendTargetedKillTo(int peerId, int deathType)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_TARGETED_KILL);
            _writer.Put(deathType);
            byte[] data = _writer.GetBytes();
            _steam.SendTo(peerId, data, true);
        }
        
        private void HandleTargetedKill(PacketReader reader)
        {
            int deathType = reader.GetInt();
            
            // Only process if we're alive (not already a ghost)
            if (_localIsGhost) return;
            
            Plugin.Log.LogInfo($"[Client] Received targeted kill from host: deathType={deathType}");
            
            var jsc = Object.FindObjectOfType<jumpscareController>();
            if (jsc == null) return;
            
            switch (deathType)
            {
                case 2: jsc.onDeathHarold(); break;
                case 4: jsc.onDeathHenry(); break;
            }
        }
        
        public void SendJeffFlash()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_JEFF_FLASH);
            SendToAllPeers(true);
        }
        
        private void CheckBatterySync()
        {
            if (Time.time - _lastBatterySyncTime < _batterySyncInterval) return;
            _lastBatterySyncTime = Time.time;
            
            // Ghost players don't have a real battery - don't sync stale/reset state
            if (_localIsGhost) return;
            
            // Get current hand holding state
            var backpack = Object.FindObjectOfType<BackpackControl>();
            bool leftHolding = false;
            bool rightHolding = false;
            
            if (backpack != null)
            {
                var leftHoldingField = typeof(BackpackControl).GetField("leftHoldingBattery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var rightHoldingField = typeof(BackpackControl).GetField("rightHoldingBattery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (leftHoldingField != null)
                    leftHolding = (bool)leftHoldingField.GetValue(backpack);
                if (rightHoldingField != null)
                    rightHolding = (bool)rightHoldingField.GetValue(backpack);
            }
            
            // Check if battery state changed significantly
            bool locationChanged = BackpackControl.batteryLocationID != _lastBatteryLocationID;
            bool backpackChanged = BackpackControl.batteryIsInBackpack != _lastBatteryInBackpack;
            bool chargeChanged = Mathf.Abs(BackpackControl.batteryCharge - _lastBatteryCharge) > 2f;
            bool holdingChanged = leftHolding != _lastLeftHolding || rightHolding != _lastRightHolding;
            
            // Sync if anything changed
            if (locationChanged || backpackChanged || chargeChanged || holdingChanged)
            {
                _lastBatteryLocationID = BackpackControl.batteryLocationID;
                _lastBatteryInBackpack = BackpackControl.batteryIsInBackpack;
                _lastBatteryCharge = BackpackControl.batteryCharge;
                _lastLeftHolding = leftHolding;
                _lastRightHolding = rightHolding;
                SendBatterySync();
            }
        }
        
        private void SendBatterySync()
        {
            _writer.Reset();
            _writer.Put(PACKET_BATTERY_SYNC);
            _writer.Put(BackpackControl.batteryCharge);
            _writer.Put(BackpackControl.batteryLocationID);
            _writer.Put(BackpackControl.batteryIsInBackpack);
            
            // Get which hand is holding the battery directly from BackpackControl
            var backpack = Object.FindObjectOfType<BackpackControl>();
            bool leftHolding = false;
            bool rightHolding = false;
            
            if (backpack != null)
            {
                // Access the private fields via reflection
                var leftHoldingField = typeof(BackpackControl).GetField("leftHoldingBattery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var rightHoldingField = typeof(BackpackControl).GetField("rightHoldingBattery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (leftHoldingField != null)
                    leftHolding = (bool)leftHoldingField.GetValue(backpack);
                if (rightHoldingField != null)
                    rightHolding = (bool)rightHoldingField.GetValue(backpack);
            }
            
            _writer.Put(leftHolding);
            _writer.Put(rightHolding);
            
            SendToAllPeers(true);
        }
        
        private void HandleBatterySync(int peerId, PacketReader reader)
        {
            float charge = reader.GetFloat();
            int locationID = reader.GetInt();
            bool inBackpack = reader.GetBool();
            bool leftHolding = reader.GetBool();
            bool rightHolding = reader.GetBool();
            
            Plugin.LogDebug($"[Battery] Peer {peerId}: charge={charge:F1}, loc={locationID}, backpack={inBackpack}, L={leftHolding}, R={rightHolding}");
            
            // Ignore battery updates from ghosts
            if (_remotePlayers.TryGetValue(peerId, out var remote) && remote.IsGhost)
                return;
            
            // Store battery state keyed by peer ID
            if (!_remoteBatteryStates.ContainsKey(peerId))
                _remoteBatteryStates[peerId] = new RemoteBatteryState();
            
            _remoteBatteryStates[peerId].Charge = charge;
            _remoteBatteryStates[peerId].LocationID = locationID;
            _remoteBatteryStates[peerId].InBackpack = inBackpack;
            _remoteBatteryStates[peerId].LeftHolding = leftHolding;
            _remoteBatteryStates[peerId].RightHolding = rightHolding;
            
            // Update remote player visual to show battery in hand
            if (remote != null)
                remote.SetBatteryState(leftHolding, rightHolding);
        }
        
        // Get remote player's battery state for UI display
        public RemoteBatteryState GetRemoteBatteryState(int peerId)
        {
            if (_remoteBatteryStates.TryGetValue(peerId, out var state))
                return state;
            return null;
        }
        
        // Get first remote player's battery charge (for simple 2-player UI)
        public float GetRemoteBatteryCharge()
        {
            foreach (var state in _remoteBatteryStates.Values)
            {
                return state.Charge;
            }
            return -1f; // No remote player
        }
        
        // Get first remote player's battery location ID
        public int GetRemoteBatteryLocationID()
        {
            foreach (var state in _remoteBatteryStates.Values)
            {
                return state.LocationID;
            }
            return 0; // No remote player
        }
        
        // Check if any remote player has battery at exit door (location 100)
        public bool IsRemoteBatteryAtExit()
        {
            foreach (var state in _remoteBatteryStates.Values)
            {
                if (state.LocationID == 100 && state.Charge >= 0.1f)
                    return true;
            }
            return false;
        }
        
        // Get first remote player's full battery state (for crank visual sync)
        // NOTE: For 2-player games this works fine. For 3+ players, use GetRemoteBatteryAtLocation()
        public RemoteBatteryState GetFirstRemoteBatteryState()
        {
            foreach (var state in _remoteBatteryStates.Values)
            {
                return state;
            }
            return null;
        }
        
        // Get remote battery state at a specific location (supports 3+ players)
        // Returns the first ALIVE remote player who has their battery at the specified location
        public RemoteBatteryState GetRemoteBatteryAtLocation(int locationID)
        {
            foreach (var kvp in _remoteBatteryStates)
            {
                // Skip ghost players - their battery doesn't exist anymore
                if (_remotePlayers.TryGetValue(kvp.Key, out var remote) && remote.IsGhost)
                    continue;
                    
                if (kvp.Value.LocationID == locationID)
                {
                    return kvp.Value;
                }
            }
            return null;
        }
        
        // Crank sync - so partner can see battery in crank, charge display, and crank rotation
        private Quaternion _lastCrankRotation = Quaternion.identity;
        
        private void CheckCrankSync()
        {
            // Ghost players don't interact with the crank
            if (_localIsGhost) return;
            
            // Find crank if not cached
            if (_crankControl == null)
            {
                _crankControl = Object.FindObjectOfType<crankControl>();
            }
            
            if (_crankControl == null) return;
            
            // Check if battery is in crank (location 1)
            bool hasBattery = BackpackControl.batteryLocationID == 1;
            float crankCharge = BackpackControl.batteryCharge;
            Quaternion crankRotation = _crankControl.transform.rotation;
            
            // Sync more frequently when actively charging (holdTimer > 0 means cranking)
            bool isCranking = crankControl.holdTimer > 0;
            float syncInterval = isCranking ? 0.05f : _crankSyncInterval; // 20 times/sec when cranking
            
            if (Time.time - _lastCrankSyncTime < syncInterval) return;
            _lastCrankSyncTime = Time.time;
            
            // Sync if battery state changed, charge changed, or rotation changed significantly while cranking
            bool chargeChanged = hasBattery && Mathf.Abs(crankCharge - _lastCrankCharge) > 0.1f;
            bool rotationChanged = isCranking && Quaternion.Angle(_lastCrankRotation, crankRotation) > 2f;
            
            if (hasBattery != _lastCrankHasBattery || chargeChanged || rotationChanged)
            {
                _lastCrankHasBattery = hasBattery;
                _lastCrankCharge = crankCharge;
                _lastCrankRotation = crankRotation;
                SendCrankSync(hasBattery, crankCharge, crankRotation);
            }
        }
        
        private void SendCrankSync(bool hasBattery, float charge, Quaternion rotation)
        {
            _writer.Reset();
            _writer.Put(PACKET_CRANK_SYNC);
            _writer.Put(hasBattery);
            _writer.Put(charge);
            WriteQuaternion(_writer, rotation);
            SendToAllPeers(true);
        }
        
        private bool _remoteCrankHasBattery = false;
        public bool RemoteHasBatteryInCrank => _remoteCrankHasBattery;
        
        // Interpolated crank charge for smooth remote display
        private float _remoteCrankChargeTarget = 0f;
        private float _remoteCrankChargeDisplay = 0f;
        private Quaternion _remoteCrankRotTarget = Quaternion.identity;
        private Quaternion _remoteCrankRotDisplay = Quaternion.identity;
        
        /// <summary>Get the smoothly interpolated remote crank charge (0-55 range).</summary>
        public float RemoteCrankChargeDisplay => _remoteCrankChargeDisplay;
        
        /// <summary>Get the smoothly interpolated remote crank rotation.</summary>
        public Quaternion RemoteCrankRotationDisplay => _remoteCrankRotDisplay;
        
        /// <summary>Tick interpolation each frame. Called from Update or FixedUpdate.</summary>
        public void UpdateCrankInterpolation()
        {
            // Lerp charge toward target - fast enough to feel responsive, slow enough to be smooth
            _remoteCrankChargeDisplay = Mathf.Lerp(_remoteCrankChargeDisplay, _remoteCrankChargeTarget, Time.deltaTime * 15f);
            _remoteCrankRotDisplay = Quaternion.Slerp(_remoteCrankRotDisplay, _remoteCrankRotTarget, Time.deltaTime * 15f);
        }
        
        // Puzzle sync tracking
        private int _lastTotalCompletedPuzzles = -1;
        
        private void CheckPuzzleInitSync()
        {
            // Only host sends puzzle init
            if (!_steam.IsHost) return;
            
            // Ghost host should NOT re-send puzzle init — the client already has
            // the correct state and re-sending would wipe their in-progress puzzles
            if (_localIsGhost) return;
            
            // If we're a ghost and haven't restored puzzle state yet, don't send reset state
            // The restore happens a couple frames after scene load — wait for it
            if (_pendingGhostPuzzleRestore) return;
            
            // Find puzzle master
            if (_puzzleMaster == null)
            {
                _puzzleMaster = Object.FindObjectOfType<PuzzleMaster>();
            }
            
            if (_puzzleMaster == null) return;
            
            // Send initial puzzle state once
            if (!_puzzleInitSent)
            {
                SendPuzzleInit();
                _puzzleInitSent = true;
                _lastTotalCompletedPuzzles = PuzzleMaster.totalCompletedPuzzles;
            }
            
            // NOTE: We do NOT periodically re-sync puzzle state.
            // The initial SendPuzzleInit handles setup, and PACKET_PUZZLE_COMPLETE
            // handles individual completions. Re-syncing caused double-counting
            // because the client would get both the init (setting the count) and
            // the complete packet (incrementing the count) for the same puzzle.
        }
        
        private void SendPuzzleInit()
        {
            if (_puzzleMaster == null) return;
            
            _writer.Reset();
            _writer.Put(PACKET_PUZZLE_INIT);
            
            // Send which puzzles are active and their preset IDs
            // We need to use reflection to get the private ps1-ps9 bools and puzzlePresetID
            var pmType = typeof(PuzzleMaster);
            var pcType = typeof(PuzzleController);
            
            // Get puzzle active states
            bool[] activeStates = new bool[9];
            int[] presetIDs = new int[9];
            bool[] completedStates = new bool[9];
            
            PuzzleController[] controllers = new PuzzleController[] {
                _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
            };
            
            string[] psFields = { "ps1", "ps2", "ps3", "ps4", "ps5", "ps6", "ps7", "ps8", "ps9" };
            
            for (int i = 0; i < 9; i++)
            {
                // Get active state from PuzzleMaster
                var psField = pmType.GetField(psFields[i], System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                activeStates[i] = (bool)psField.GetValue(_puzzleMaster);
                
                // Get preset ID and completion state from PuzzleController
                if (controllers[i] != null)
                {
                    var presetField = pcType.GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    presetIDs[i] = (int)presetField.GetValue(controllers[i]);
                    
                    var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    completedStates[i] = (bool)completedField.GetValue(controllers[i]);
                }
            }
            
            // Get PuzzleBlock type for reflection
            var pbType = typeof(PuzzleBlock);
            
            // Write data
            for (int i = 0; i < 9; i++)
            {
                _writer.Put(activeStates[i]);
                _writer.Put(presetIDs[i]);
                _writer.Put(completedStates[i]);
                
                // Send current block states for active puzzles
                if (controllers[i] != null && controllers[i].cubeList != null)
                {
                    _writer.Put(controllers[i].cubeList.Length);
                    foreach (var block in controllers[i].cubeList)
                    {
                        _writer.Put(block.blockIDValue);
                    }
                }
                else
                {
                    _writer.Put(0); // No blocks
                }
            }
            
            _writer.Put(PuzzleMaster.totalCompletedPuzzles);
            _writer.Put(PuzzleMaster.requiredPuzzles);
            
            SendToAllPeers(true);
            Plugin.Log.LogInfo($"[Host] Sent puzzle init: required={PuzzleMaster.requiredPuzzles}, completed={PuzzleMaster.totalCompletedPuzzles}");
        }
        
        /// <summary>
        /// Save current puzzle state so it can be restored after ghost scene reload.
        /// Called before ReloadSceneAsGhost() to preserve progress.
        /// </summary>
        private void SavePuzzleStateForGhost()
        {
            if (_puzzleMaster == null)
            {
                _puzzleMaster = Object.FindObjectOfType<PuzzleMaster>();
            }
            if (_puzzleMaster == null)
            {
                Plugin.Log.LogWarning("[Ghost] Can't save puzzle state - PuzzleMaster not found");
                return;
            }
            
            var pmType = typeof(PuzzleMaster);
            var pcType = typeof(PuzzleController);
            
            bool[] activeStates = new bool[9];
            int[] presetIDs = new int[9];
            bool[] completedStates = new bool[9];
            int[][] blockStates = new int[9][];
            
            PuzzleController[] controllers = new PuzzleController[] {
                _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
            };
            
            string[] psFields = { "ps1", "ps2", "ps3", "ps4", "ps5", "ps6", "ps7", "ps8", "ps9" };
            
            for (int i = 0; i < 9; i++)
            {
                var psField = pmType.GetField(psFields[i], System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                activeStates[i] = (bool)psField.GetValue(_puzzleMaster);
                
                if (controllers[i] != null)
                {
                    var presetField = pcType.GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    presetIDs[i] = (int)presetField.GetValue(controllers[i]);
                    
                    var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    completedStates[i] = (bool)completedField.GetValue(controllers[i]);
                    
                    if (controllers[i].cubeList != null)
                    {
                        blockStates[i] = new int[controllers[i].cubeList.Length];
                        for (int j = 0; j < controllers[i].cubeList.Length; j++)
                        {
                            blockStates[i][j] = controllers[i].cubeList[j].blockIDValue;
                        }
                    }
                    else
                    {
                        blockStates[i] = new int[0];
                    }
                }
                else
                {
                    blockStates[i] = new int[0];
                }
            }
            
            _savedGhostPuzzleState = new PendingPuzzleInit
            {
                ActiveStates = activeStates,
                PresetIDs = presetIDs,
                CompletedStates = completedStates,
                BlockStates = blockStates,
                TotalCompleted = PuzzleMaster.totalCompletedPuzzles,
                RequiredPuzzles = PuzzleMaster.requiredPuzzles
            };
            
            Plugin.Log.LogInfo($"[Ghost] Saved puzzle state: completed={PuzzleMaster.totalCompletedPuzzles}, required={PuzzleMaster.requiredPuzzles}");
        }
        
        /// <summary>
        /// Restore puzzle state after ghost scene reload.
        /// Called after PuzzleMaster.Start() has run and reset everything.
        /// </summary>
        public void RestoreGhostPuzzleState()
        {
            if (_savedGhostPuzzleState == null) return;
            
            if (_puzzleMaster == null)
            {
                _puzzleMaster = Object.FindObjectOfType<PuzzleMaster>();
            }
            if (_puzzleMaster == null)
            {
                Plugin.Log.LogWarning("[Ghost] Can't restore puzzle state - PuzzleMaster not found yet");
                return;
            }
            
            var pmType = typeof(PuzzleMaster);
            var pcType = typeof(PuzzleController);
            var saved = _savedGhostPuzzleState;
            
            PuzzleController[] controllers = new PuzzleController[] {
                _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
            };
            
            string[] psFields = { "ps1", "ps2", "ps3", "ps4", "ps5", "ps6", "ps7", "ps8", "ps9" };
            
            for (int i = 0; i < 9; i++)
            {
                var psField = pmType.GetField(psFields[i], System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                psField?.SetValue(_puzzleMaster, saved.ActiveStates[i]);
                
                if (controllers[i] != null)
                {
                    var presetField = pcType.GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    presetField?.SetValue(controllers[i], saved.PresetIDs[i]);
                    
                    var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    completedField?.SetValue(controllers[i], saved.CompletedStates[i]);
                    
                    if (saved.CompletedStates[i] || !saved.ActiveStates[i])
                    {
                        // Completed or inactive — fan on, map indicator on
                        if (controllers[i].fanspin != null)
                            controllers[i].fanspin.isOn = true;
                        controllers[i].thisMapIndicator?.SetActive(true);
                    }
                    else
                    {
                        // Active, not completed — fan off, map indicator off
                        controllers[i].thisMapIndicator?.SetActive(false);
                    }
                    
                    // Restore block states
                    if (saved.BlockStates[i] != null && controllers[i].cubeList != null)
                    {
                        int blockCount = Mathf.Min(saved.BlockStates[i].Length, controllers[i].cubeList.Length);
                        for (int j = 0; j < blockCount; j++)
                        {
                            controllers[i].cubeList[j].setThisID(saved.BlockStates[i][j]);
                        }
                    }
                }
            }
            
            PuzzleMaster.totalCompletedPuzzles = saved.TotalCompleted;
            PuzzleMaster.requiredPuzzles = saved.RequiredPuzzles;
            
            // Also restore the completed puzzle IDs tracking
            _lastTotalCompletedPuzzles = saved.TotalCompleted;
            
            // Repopulate _completedPuzzleIDs from saved state so we don't double-count
            _completedPuzzleIDs.Clear();
            for (int i = 0; i < 9; i++)
            {
                if (saved.CompletedStates[i] && saved.ActiveStates[i] && controllers[i] != null)
                {
                    _completedPuzzleIDs.Add(controllers[i].thisPuzzleID);
                }
            }
            
            Plugin.Log.LogInfo($"[Ghost] Restored puzzle state: completed={saved.TotalCompleted}, required={saved.RequiredPuzzles}");
            
            // Start re-apply timer to catch any late PuzzleController resets
            _puzzleInitCompletedStates = saved.CompletedStates;
            _puzzleInitActiveStates = saved.ActiveStates;
            _puzzleInitReapplyTimer = PUZZLE_INIT_REAPPLY_DURATION;
            _puzzleInitApplied = true;
            
            _savedGhostPuzzleState = null;
        }
        
        private void HandlePuzzleInit(PacketReader reader)
        {
            // Only clients receive puzzle init
            if (_steam.IsHost) return;
            
            Plugin.LogDebug("[Client] Received puzzle init");
            
            // Only apply puzzle init once per scene — subsequent inits would
            // overwrite in-progress puzzle state and cause desync
            if (_puzzleInitApplied)
            {
                // Still need to read all data to not corrupt the stream
                for (int i = 0; i < 9; i++)
                {
                    reader.GetBool(); reader.GetInt(); reader.GetBool();
                    int bc = reader.GetInt();
                    for (int j = 0; j < bc; j++) reader.GetInt();
                }
                reader.GetInt(); reader.GetInt();
                Plugin.LogDebug("[Client] Ignoring duplicate puzzle init (already applied)");
                return;
            }
            
            // Read all the data first (to not corrupt the stream)
            bool[] activeStates = new bool[9];
            int[] presetIDs = new int[9];
            bool[] completedStates = new bool[9];
            int[][] blockStates = new int[9][];
            
            for (int i = 0; i < 9; i++)
            {
                activeStates[i] = reader.GetBool();
                presetIDs[i] = reader.GetInt();
                completedStates[i] = reader.GetBool();
                
                // Read block states
                int blockCount = reader.GetInt();
                blockStates[i] = new int[blockCount];
                for (int j = 0; j < blockCount; j++)
                {
                    blockStates[i][j] = reader.GetInt();
                }
            }
            int totalCompleted = reader.GetInt();
            int requiredPuzzles = reader.GetInt();
            
            // Store for later application (in case we're not in the right scene yet)
            _pendingPuzzleInit = new PendingPuzzleInit
            {
                ActiveStates = activeStates,
                PresetIDs = presetIDs,
                CompletedStates = completedStates,
                BlockStates = blockStates,
                TotalCompleted = totalCompleted,
                RequiredPuzzles = requiredPuzzles
            };
            _puzzleInitStartWaited = false; // Reset frame delay for new data
            
            // Try to apply immediately
            TryApplyPuzzleInit();
        }
        
        private class PendingPuzzleInit
        {
            public bool[] ActiveStates;
            public int[] PresetIDs;
            public bool[] CompletedStates;
            public int[][] BlockStates;
            public int TotalCompleted;
            public int RequiredPuzzles;
        }
        
        private PendingPuzzleInit _pendingPuzzleInit = null;
        private bool _puzzleInitStartWaited = false;
        private bool _puzzleInitApplied = false;
        private float _puzzleInitReapplyTimer = 0f;
        private const float PUZZLE_INIT_REAPPLY_DURATION = 3f; // Re-apply for 3 seconds after init
        private bool[] _puzzleInitCompletedStates = null;
        private bool[] _puzzleInitActiveStates = null;
        public bool PuzzleInitApplied => _puzzleInitApplied || _pendingPuzzleInit != null;
        
        private void TryApplyPuzzleInit()
        {
            if (_pendingPuzzleInit == null) return;
            
            // Find puzzle master
            if (_puzzleMaster == null)
            {
                _puzzleMaster = Object.FindObjectOfType<PuzzleMaster>();
            }
            
            if (_puzzleMaster == null)
            {
                Plugin.Log.LogWarning("[Client] PuzzleMaster not found, will retry later");
                return;
            }
            
            // Wait one frame after finding PuzzleMaster to ensure Start() has run
            // Our PuzzleMasterStartPatch hides all map indicators in Start(),
            // so we need to apply AFTER that to avoid our state being overwritten
            if (!_puzzleInitStartWaited)
            {
                _puzzleInitStartWaited = true;
                return; // Try again next frame
            }
            
            var pmType = typeof(PuzzleMaster);
            var pcType = typeof(PuzzleController);
            
            PuzzleController[] controllers = new PuzzleController[] {
                _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
            };
            
            string[] psFields = { "ps1", "ps2", "ps3", "ps4", "ps5", "ps6", "ps7", "ps8", "ps9" };
            
            _isReceivingPuzzleBlock = true; // Prevent re-sending these changes
            
            for (int i = 0; i < 9; i++)
            {
                bool isActive = _pendingPuzzleInit.ActiveStates[i];
                int presetID = _pendingPuzzleInit.PresetIDs[i];
                bool isCompleted = _pendingPuzzleInit.CompletedStates[i];
                
                // Set active state
                var psField = pmType.GetField(psFields[i], System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                psField?.SetValue(_puzzleMaster, isActive);
                
                // Set preset ID and configure puzzle
                if (controllers[i] != null)
                {
                    var presetField = pcType.GetField("puzzlePresetID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    presetField?.SetValue(controllers[i], presetID);
                    
                    var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    // Don't un-complete a puzzle the client actually completed during gameplay
                    // (tracked in _completedPuzzleIDs). But DO override the random init state.
                    bool completedDuringGameplay = controllers[i] != null && _completedPuzzleIDs.Contains(controllers[i].thisPuzzleID);
                    if (!completedDuringGameplay)
                        completedField?.SetValue(controllers[i], isCompleted);
                    
                    if (isCompleted || completedDuringGameplay)
                    {
                        // Completed puzzle - fan on, map indicator on
                        if (controllers[i].fanspin != null)
                            controllers[i].fanspin.isOn = true;
                        controllers[i].thisMapIndicator?.SetActive(true);
                    }
                    else if (!isActive)
                    {
                        // Inactive puzzle (not selected for this night) - mark as completed with fan on
                        // This matches what enableRest()/thisFan(2) does in the original game
                        completedField?.SetValue(controllers[i], true);
                        if (controllers[i].fanspin != null)
                            controllers[i].fanspin.isOn = true;
                        controllers[i].thisMapIndicator?.SetActive(true);
                    }
                    else
                    {
                        // Active puzzle - not completed yet, needs to be solved
                        // Turn off fan and indicator (client's random init may have turned them on)
                        completedField?.SetValue(controllers[i], false);
                        if (controllers[i].fanspin != null)
                            controllers[i].fanspin.isOn = false;
                        controllers[i].thisMapIndicator?.SetActive(false);
                    }
                    
                    // Apply block states
                    int[] blockStates = _pendingPuzzleInit.BlockStates[i];
                    if (blockStates != null && controllers[i].cubeList != null)
                    {
                        int blockCount = Mathf.Min(blockStates.Length, controllers[i].cubeList.Length);
                        for (int j = 0; j < blockCount; j++)
                        {
                            controllers[i].cubeList[j].setThisID(blockStates[j]);
                        }
                    }
                }
            }
            
            _isReceivingPuzzleBlock = false;
            
            // Never decrease totalCompletedPuzzles — the client may have completed
            // puzzles that the host hasn't processed yet (race condition during ghost reload)
            if (_pendingPuzzleInit.TotalCompleted > PuzzleMaster.totalCompletedPuzzles)
                PuzzleMaster.totalCompletedPuzzles = _pendingPuzzleInit.TotalCompleted;
            PuzzleMaster.requiredPuzzles = _pendingPuzzleInit.RequiredPuzzles;
            
            // Mark all completed puzzles in _completedPuzzleIDs so HandlePuzzleComplete
            // won't double-count them if a PACKET_PUZZLE_COMPLETE arrives for the same puzzle
            for (int j = 0; j < 9; j++)
            {
                if (_pendingPuzzleInit.CompletedStates[j] && controllers[j] != null)
                {
                    _completedPuzzleIDs.Add(controllers[j].thisPuzzleID);
                }
            }
            
            Plugin.LogDebug($"[Client] Applied puzzle init: completed={PuzzleMaster.totalCompletedPuzzles}, required={PuzzleMaster.requiredPuzzles}");
            
            // Apply visual state for any completions that arrived before PuzzleMaster was available
            // HandlePuzzleComplete increments the counter but can't set indicators if _puzzleMaster was null
            foreach (var completedId in _completedPuzzleIDs)
            {
                for (int k = 0; k < 9; k++)
                {
                    if (controllers[k] != null && controllers[k].thisPuzzleID == completedId)
                    {
                        var cField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (cField != null) cField.SetValue(controllers[k], true);
                        if (controllers[k].fanspin != null) controllers[k].fanspin.isOn = true;
                        controllers[k].thisMapIndicator?.SetActive(true);
                        break;
                    }
                }
            }
            
            // Clear pending init — save states for re-application
            _puzzleInitCompletedStates = _pendingPuzzleInit.CompletedStates;
            _puzzleInitActiveStates = _pendingPuzzleInit.ActiveStates;
            _pendingPuzzleInit = null;
            _puzzleInitApplied = true;
            _puzzleInitReapplyTimer = PUZZLE_INIT_REAPPLY_DURATION;
        }
        
        /// <summary>
        /// Re-applies map indicator and fan state for a few seconds after init.
        /// Catches any PuzzleController.Start() or FixedUpdate that runs late and resets state.
        /// </summary>
        private void ReapplyPuzzleIndicators()
        {
            if (_puzzleInitReapplyTimer <= 0f) return;
            _puzzleInitReapplyTimer -= Time.deltaTime;
            
            if (_puzzleMaster == null) return;
            if (_puzzleInitCompletedStates == null || _puzzleInitActiveStates == null) return;
            
            var pcType = typeof(PuzzleController);
            var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            PuzzleController[] controllers = new PuzzleController[] {
                _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
            };
            
            for (int i = 0; i < 9; i++)
            {
                if (controllers[i] == null) continue;
                
                bool shouldBeCompleted = _puzzleInitCompletedStates[i] || !_puzzleInitActiveStates[i];
                
                if (shouldBeCompleted)
                {
                    bool currentCompleted = completedField != null && (bool)completedField.GetValue(controllers[i]);
                    bool indicatorActive = controllers[i].thisMapIndicator != null && controllers[i].thisMapIndicator.activeSelf;
                    
                    // If state was reset by something, re-apply it
                    if (!currentCompleted || !indicatorActive)
                    {
                        if (completedField != null) completedField.SetValue(controllers[i], true);
                        if (controllers[i].fanspin != null) controllers[i].fanspin.isOn = true;
                        controllers[i].thisMapIndicator?.SetActive(true);
                        Plugin.LogDebug($"[PuzzleInit] Re-applied completed state for puzzle index {i} (something reset it)");
                    }
                }
            }
        }
        
        // Exit door progress sync - EITHER player can charge the door
        private void CheckExitDoorSync()
        {
            // Ghost host shouldn't sync exit door — client handles their own charging
            if (_localIsGhost) return;
            
            // Throttle sync
            if (Time.time - _lastDoorSyncTime < 0.1f) return;
            _lastDoorSyncTime = Time.time;
            
            // Only sync if OUR battery is at the exit (location 100)
            if (BackpackControl.batteryLocationID != 100 || BackpackControl.batteryCharge < 0.1f)
            {
                // Don't send a reset if the door was already fully charged
                // Swapping batteries shouldn't wipe the progress
                if (_lastDoorLeaveTimer > 0 && !_exitDoorFullyCharged)
                {
                    _lastDoorLeaveTimer = 0;
                    SendExitDoorProgress(0, 100); // Reset to 0
                }
                return;
            }
            
            // Find leave door if not cached
            if (_leaveDoor == null)
            {
                _leaveDoor = Object.FindObjectOfType<LeaveDoorControl>();
            }
            
            if (_leaveDoor != null)
            {
                // Get the timer via reflection
                var timerField = typeof(LeaveDoorControl).GetField("doorLeaveTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (timerField != null)
                {
                    int currentTimer = (int)timerField.GetValue(_leaveDoor);
                    
                    // Track if door reached full charge
                    if (currentTimer >= _leaveDoor.doorLeaveRequiredTime && _leaveDoor.doorLeaveRequiredTime > 0)
                    {
                        _exitDoorFullyCharged = true;
                    }
                    
                    // Only sync if changed
                    if (currentTimer != _lastDoorLeaveTimer)
                    {
                        _lastDoorLeaveTimer = currentTimer;
                        SendExitDoorProgress(currentTimer, _leaveDoor.doorLeaveRequiredTime);
                    }
                }
            }
        }
        
        private void SendExitDoorProgress(int timer, int requiredTime)
        {
            _writer.Reset();
            _writer.Put(PACKET_EXIT_DOOR_PROGRESS);
            _writer.Put(timer);
            _writer.Put(requiredTime);
            SendToAllPeers(true);
        }
        
        // Track which puzzles have been completed to avoid race conditions
        private HashSet<int> _completedPuzzleIDs = new HashSet<int>();
        
        public void MarkPuzzleCompleted(int puzzleID)
        {
            _completedPuzzleIDs.Add(puzzleID);
            Plugin.LogDebug($"[Puzzle] Marked puzzle {puzzleID} as completed locally");
        }
        
        public bool IsPuzzleCompleted(int puzzleID)
        {
            return _completedPuzzleIDs.Contains(puzzleID);
        }
        
        public void SendPuzzleComplete(int puzzleID)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_PUZZLE_COMPLETE);
            _writer.Put(puzzleID);
            _writer.Put(PuzzleMaster.totalCompletedPuzzles);
            SendToAllPeers(true);
            Plugin.LogDebug($"[Puzzle] Sent puzzle complete: puzzleID={puzzleID}, total={PuzzleMaster.totalCompletedPuzzles}");
        }
        
        private void HandlePuzzleComplete(PacketReader reader)
        {
            int puzzleID = reader.GetInt();
            int remoteTotalCompleted = reader.GetInt();
            
            Plugin.LogDebug($"[Puzzle] Received puzzle complete: puzzleID={puzzleID}, remoteTotal={remoteTotalCompleted}, localTotal={PuzzleMaster.totalCompletedPuzzles}");
            
            // Check if we've already marked this puzzle as complete
            if (_completedPuzzleIDs.Contains(puzzleID))
            {
                Plugin.LogDebug($"[Puzzle] Puzzle {puzzleID} already marked complete, skipping");
                return;
            }
            
            // If the local player currently has their battery in this puzzle,
            // don't force-complete it — let them finish their own attempt.
            // The puzzle will be marked complete when THEY call onWin().
            // We still track it so we don't double-count later.
            bool localBatteryHere = puzzleID == BackpackControl.batteryLocationID && BackpackControl.batteryCharge > 0f;
            if (localBatteryHere)
            {
                Plugin.LogDebug($"[Puzzle] Local battery is in puzzle {puzzleID} — skipping remote completion (local player is solving it)");
                // Don't add to _completedPuzzleIDs — let the local onWin handle it
                // But DO increment the counter since the remote player legitimately completed it
                PuzzleMaster.totalCompletedPuzzles++;
                _completedPuzzleIDs.Add(puzzleID);
                return;
            }
            
            // Mark this puzzle as completed
            _completedPuzzleIDs.Add(puzzleID);
            
            // Increment our local counter (don't just set it to remote value - that causes race conditions!)
            PuzzleMaster.totalCompletedPuzzles++;
            Plugin.LogDebug($"[Puzzle] Incremented local total to {PuzzleMaster.totalCompletedPuzzles}");
            
            // Find the puzzle controller with this ID and mark it complete
            if (_puzzleMaster == null)
            {
                _puzzleMaster = Object.FindObjectOfType<PuzzleMaster>();
            }
            
            if (_puzzleMaster != null)
            {
                PuzzleController[] controllers = new PuzzleController[] {
                    _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                    _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                    _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
                };
                
                foreach (var controller in controllers)
                {
                    if (controller != null && controller.thisPuzzleID == puzzleID)
                    {
                        var pcType = typeof(PuzzleController);
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        var completedField = pcType.GetField("puzzleHasCompleted", flags);
                        completedField.SetValue(controller, true);
                        
                        // Reset totalCompletions so if someone puts battery in this
                        // puzzle later, it won't auto-win from stale completion count
                        var totalCompField = pcType.GetField("totalCompletions", flags);
                        if (totalCompField != null)
                            totalCompField.SetValue(controller, 0);
                        
                        // Clear the board so it doesn't show stale blocks
                        for (int i = 0; i < controller.cubeList.Length; i++)
                            controller.cubeList[i].setThisID(0);
                        
                        if (controller.fanspin != null)
                            controller.fanspin.isOn = true;
                        controller.thisMapIndicator?.SetActive(true);
                        
                        // Play win audio
                        controller.winAudio?.Play();
                        
                        Plugin.LogDebug($"Marked puzzle {puzzleID} as complete (board cleared)");
                        break;
                    }
                }
            }
        }
        
        // NOTE: Puzzle block visual sync has been DISABLED
        // Each player solves puzzles independently - only completion is synced
        // This prevents buggy visual sync where shapes appear in wrong positions
        
        // Flag kept for backwards compatibility (in case old clients send this packet)
        private bool _isReceivingPuzzleBlock = false;
        public bool IsReceivingPuzzleBlock => _isReceivingPuzzleBlock;
        
        // Puzzle block visual sync - sends individual block state changes so the other player
        // can see puzzle progress in real-time
        public void SendPuzzleBlock(int puzzleID, int blockNumber, int blockIDValue)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_PUZZLE_BLOCK);
            _writer.Put(puzzleID);
            _writer.Put(blockNumber);
            _writer.Put(blockIDValue);
            SendToAllPeers(true);
        }
        
        // DISABLED: Ignore incoming puzzle block packets (from old clients)
        private void HandlePuzzleBlock(PacketReader reader)
        {
            int puzzleID = reader.GetInt();
            int blockNumber = reader.GetInt();
            int blockIDValue = reader.GetInt();
            
            // Apply visual-only block state on the matching puzzle
            if (_puzzleMaster == null)
            {
                _puzzleMaster = Object.FindObjectOfType<PuzzleMaster>();
            }
            if (_puzzleMaster == null) return;
            
            PuzzleController[] controllers = new PuzzleController[] {
                _puzzleMaster.pCon1, _puzzleMaster.pCon2, _puzzleMaster.pCon3,
                _puzzleMaster.pCon4, _puzzleMaster.pCon5, _puzzleMaster.pCon6,
                _puzzleMaster.pCon7, _puzzleMaster.pCon8, _puzzleMaster.pCon9
            };
            
            foreach (var controller in controllers)
            {
                if (controller != null && controller.thisPuzzleID == puzzleID)
                {
                    if (blockNumber >= 0 && blockNumber < controller.cubeList.Length)
                    {
                        _isReceivingPuzzleBlock = true;
                        controller.cubeList[blockNumber].setThisID(blockIDValue);
                        _isReceivingPuzzleBlock = false;
                    }
                    break;
                }
            }
        }
        
        // Clown nose honk sync
        private bool _isReceivingHonk = false;
        public bool IsReceivingHonk => _isReceivingHonk;
        
        public void SendClownHonk()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_CLOWN_HONK);
            SendToAllPeers(true);
        }
        
        private void HandleClownHonk(PacketReader reader)
        {
            // Find clown nose and play honk
            var clownNose = Object.FindObjectOfType<clownNose>();
            if (clownNose != null && clownNose.honkSound != null)
            {
                _isReceivingHonk = true;
                clownNose.honkSound.Play();
                _isReceivingHonk = false;
            }
        }
        
        // Clown state sync (which clown is visible)
        private clownRandom _clownRandom;
        
        public void SendClownState(clownRandom clown)
        {
            if (_steam == null || !_steam.IsRunning || !ShouldControlMonsters) return;
            
            _writer.Reset();
            _writer.Put(PACKET_CLOWN_STATE);
            _writer.Put(clown.clown1.activeSelf);
            _writer.Put(clown.clown2.activeSelf);
            _writer.Put(clown.clown3.activeSelf);
            _writer.Put(clown.clown4.activeSelf);
            _writer.Put(clown.clown5.activeSelf);
            _writer.Put(clown.clown6.activeSelf);
            _writer.Put(clown.clown7.activeSelf);
            SendToAllPeers(true);
            Plugin.Log.LogInfo("[Controller] Sent clown state");
        }
        
        public void SendClownAttack()
        {
            if (_steam == null || !_steam.IsRunning || !ShouldControlMonsters) return;
            
            _writer.Reset();
            _writer.Put(PACKET_CLOWN_ATTACK);
            SendToAllPeers(true);
        }
        
        private void HandleClownState(PacketReader reader)
        {
            bool c1 = reader.GetBool();
            bool c2 = reader.GetBool();
            bool c3 = reader.GetBool();
            bool c4 = reader.GetBool();
            bool c5 = reader.GetBool();
            bool c6 = reader.GetBool();
            bool c7 = reader.GetBool();
            
            if (_clownRandom == null)
            {
                _clownRandom = Object.FindObjectOfType<clownRandom>();
            }
            
            if (_clownRandom != null)
            {
                _clownRandom.clown1.SetActive(c1);
                _clownRandom.clown2.SetActive(c2);
                _clownRandom.clown3.SetActive(c3);
                _clownRandom.clown4.SetActive(c4);
                _clownRandom.clown5.SetActive(c5);
                _clownRandom.clown6.SetActive(c6);
                _clownRandom.clown7.SetActive(c7);
            }
        }
        
        private void HandleClownAttack(PacketReader reader)
        {
            if (_clownRandom == null)
            {
                _clownRandom = Object.FindObjectOfType<clownRandom>();
            }
            
            if (_clownRandom != null)
            {
                // Check if the client can currently see any clown - if so, don't start the attack yet
                // In the base game, the clown can't attack while being looked at
                bool canSeeClown = false;
                if (_clownRandom.c1R != null && _clownRandom.c1R.isVisible) canSeeClown = true;
                if (_clownRandom.c2R != null && _clownRandom.c2R.isVisible) canSeeClown = true;
                if (_clownRandom.c3R != null && _clownRandom.c3R.isVisible) canSeeClown = true;
                if (_clownRandom.c4R != null && _clownRandom.c4R.isVisible) canSeeClown = true;
                if (_clownRandom.c5R != null && _clownRandom.c5R.isVisible) canSeeClown = true;
                if (_clownRandom.c6R != null && _clownRandom.c6R.isVisible) canSeeClown = true;
                if (_clownRandom.c7R != null && _clownRandom.c7R.isVisible) canSeeClown = true;
                
                if (canSeeClown)
                {
                    // Client is looking at the clown - ignore this attack
                    // The host will keep re-checking and send another attack when appropriate
                    Plugin.LogDebug("[Client] Ignoring clown attack - client can see the clown");
                    return;
                }
                
                // Hide all clowns (attack mode)
                _clownRandom.clown1.SetActive(false);
                _clownRandom.clown2.SetActive(false);
                _clownRandom.clown3.SetActive(false);
                _clownRandom.clown4.SetActive(false);
                _clownRandom.clown5.SetActive(false);
                _clownRandom.clown6.SetActive(false);
                _clownRandom.clown7.SetActive(false);
                
                // Set attacking state via reflection
                var attackField = typeof(clownRandom).GetField("clownAttackingSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                attackField?.SetValue(_clownRandom, true);
            }
        }
        
        // Smile trigger sync
        public void SendSmileTrigger(Vector3 position)
        {
            // DEPRECATED: Smile is now fully independent per player - no sync needed
        }
        
        private void HandleSmileTrigger(PacketReader reader)
        {
            // DEPRECATED: Smile is now fully independent per player
            // Just read the data to advance the reader (backward compatibility)
            ReadVector3(reader);
        }
        
        // Vent/crawl sound sync
        private bool _isReceivingVentSound = false;
        public bool IsReceivingVentSound => _isReceivingVentSound;
        
        public void SendVentSound(Vector3 position, int soundIndex)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_VENT_SOUND);
            WriteVector3(_writer, position);
            _writer.Put(soundIndex);
            SendToAllPeers(true);
        }
        
        public void SendCrawlSound(Vector3 position, bool inMainRoom)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_VENT_SOUND);
            WriteVector3(_writer, position);
            _writer.Put(2); // soundType 2 = crawl sound with room info
            _writer.Put(inMainRoom);
            SendToAllPeers(true);
        }
        
        private void HandleVentSound(PacketReader reader)
        {
            Vector3 position = ReadVector3(reader);
            int soundType = reader.GetInt();
            
            _isReceivingVentSound = true;
            
            if (soundType == 0)
            {
                // Vent ambient sound - spawn the sound prefab at the received position
                var ventPlayer = Object.FindObjectOfType<ventSoundPlayer>();
                if (ventPlayer != null && ventPlayer.soundPrefab != null)
                {
                    var spawnedSound = Object.Instantiate(ventPlayer.soundPrefab);
                    spawnedSound.transform.position = position;
                }
            }
            else if (soundType == 1)
            {
                // Legacy crawl sound (no room info) - play vent crawl sounds
                PlayRemoteCrawlSound(position, false);
            }
            else if (soundType == 2)
            {
                // Crawl sound with room info
                bool inMainRoom = reader.GetBool();
                PlayRemoteCrawlSound(position, inMainRoom);
            }
            
            _isReceivingVentSound = false;
        }
        
        private void PlayRemoteCrawlSound(Vector3 position, bool inMainRoom)
        {
            var crawlSound = Object.FindObjectOfType<crawlSoundContrl>();
            if (crawlSound == null) return;
            
            AudioSource source;
            if (inMainRoom)
            {
                // In main room, use the walking sound
                source = crawlSound.w1;
            }
            else
            {
                // In vents, pick a random crawl sound
                AudioSource[] sources = { crawlSound.m1, crawlSound.m2, crawlSound.m3, crawlSound.m4 };
                source = sources[UnityEngine.Random.Range(0, sources.Length)];
            }
            
            if (source == null || source.clip == null) return;
            
            // Create a temporary 3D audio source at the remote player's position
            float pitch = UnityEngine.Random.Range(0.8f, 1.2f);
            var tempGO = new GameObject("RemoteCrawlSound");
            tempGO.transform.position = position;
            var tempAudio = tempGO.AddComponent<AudioSource>();
            tempAudio.clip = source.clip;
            tempAudio.pitch = pitch;
            tempAudio.spatialBlend = 1f;
            tempAudio.volume = source.volume;
            tempAudio.maxDistance = 10f;
            tempAudio.rolloffMode = AudioRolloffMode.Linear;
            tempAudio.Play();
            Object.Destroy(tempGO, source.clip.length + 0.1f);
        }
        
        private void HandleFlashlightUpdate(int peerId, PacketReader reader)
        {
            bool isOn = reader.GetBool();
            
            if (_remotePlayers.TryGetValue(peerId, out var remote))
            {
                remote.SetFlashlightState(isOn);
            }
        }

        private void HandlePositionUpdate(int peerId, PacketReader reader)
        {
            if (!_remotePlayers.TryGetValue(peerId, out var remote))
            {
                Plugin.Log.LogWarning($"No remote player for peer {peerId}");
                return;
            }
            
            bool isStanding = reader.GetBool();
            
            var bodyPos = ReadVector3(reader);
            var bodyRot = ReadQuaternion(reader);
            var headPos = ReadVector3(reader);
            var headRot = ReadQuaternion(reader);
            var leftHandPos = ReadVector3(reader);
            var leftHandRot = ReadQuaternion(reader);
            var rightHandPos = ReadVector3(reader);
            var rightHandRot = ReadQuaternion(reader);
            
            // Read hand poses (grip and trigger values)
            float leftGrip = reader.GetFloat();
            float leftTrigger = reader.GetFloat();
            float rightGrip = reader.GetFloat();
            float rightTrigger = reader.GetFloat();
            
            // Log every ~5 seconds
            if (_updateCount % 150 == 0)
            {
                Plugin.LogDebug($"[Recv] peer {peerId}: head={headPos}");
            }
            
            remote.SetTargets(isStanding, bodyPos, bodyRot, headPos, headRot, 
                              leftHandPos, leftHandRot, rightHandPos, rightHandRot,
                              leftGrip, leftTrigger, rightGrip, rightTrigger);
        }
        
        // ==================== DEATH / GHOST SYSTEM ====================
        
        // Capture spawn point when first entering a level (before any movement)
        public void CaptureSpawnPoint()
        {
            if (_spawnPointCaptured) return;
            
            // Only capture after the player has stood up (debugSwitch = true in MoveTypeController)
            // onEnterRoom lifts the player after 3.25s — we need to wait for that
            var mtc = Object.FindObjectOfType<MoveTypeController>();
            if (mtc != null && mtc.debugSwitch && mtc.playerObj != null)
            {
                _levelSpawnPoint = mtc.playerObj.transform.position;
                _spawnPointCaptured = true;
                Plugin.Log.LogInfo($"[Ghost] Captured spawn point from playerObj (standing): {_levelSpawnPoint}");
            }
        }
        
        // Send death notification to other players (for UI display)
        public void SendDeathGhost(bool isGhost, int deathType)
        {
            Plugin.Log.LogInfo($"[Death] SendDeathGhost called: isGhost={isGhost}, deathType={deathType}, steamRunning={_steam?.IsRunning}, localIsGhost={_localIsGhost}, debugForce={DebugForceGhostOnDeath}");
            
            if (_steam == null || !_steam.IsRunning)
            {
                Plugin.Log.LogInfo("[Death] Steam not running, skipping send");
                return;
            }
            
            // Prevent sending multiple death notifications
            if (isGhost && _localIsGhost) 
            {
                Plugin.Log.LogInfo($"[Death] Already a ghost, skipping duplicate death notification");
                return;
            }
            
            _writer.Reset();
            _writer.Put(PACKET_DEATH_GHOST);
            _writer.Put(isGhost);
            _writer.Put(deathType);
            SendToAllPeers(true);
            Plugin.Log.LogInfo($"[Death] Sent death notification: deathType={deathType}");
            
            // Mark ourselves as ghost and trigger scene reload
            if (isGhost)
            {
                IsDyingThisFrame = true;
                BecomeGhost();
            }
        }
        
        public bool DebugForceGhostOnDeath = false;
        
        /// <summary>
        /// Called when local player dies - becomes a ghost and reloads the scene
        /// </summary>
        public void BecomeGhost()
        {
            string currentScene = SceneManager.GetActiveScene().name;
            
            Plugin.Log.LogInfo($"[Ghost] BecomeGhost: scene={currentScene}, alreadyGhost={_localIsGhost}, debugForce={DebugForceGhostOnDeath}, remotePlayers={_remotePlayers.Count}");
            
            if (!currentScene.Contains("Night"))
            {
                Plugin.Log.LogInfo("[Ghost] Not in Night scene, aborting");
                return;
            }
            
            // Check if partner is still alive (skip check if debug flag is set)
            bool partnerAlive = DebugForceGhostOnDeath;
            if (!partnerAlive)
            {
                foreach (var kvp in _remotePlayers)
                {
                    if (!kvp.Value.IsGhost)
                    {
                        partnerAlive = true;
                        break;
                    }
                }
            }
            
            Plugin.Log.LogInfo($"[Ghost] partnerAlive={partnerAlive}");
            
            if (!partnerAlive)
            {
                Plugin.Log.LogInfo("[Ghost] No alive partners - all players dead, going to Home");
                ResetGhostState();
                return;
            }
            
            Plugin.Log.LogInfo($"[Ghost] Partner alive - becoming ghost in {currentScene}");
            _localIsGhost = true;
            DebugForceGhostOnDeath = false; // Clear debug flag after use
            
            // Clear local battery state — ghost has no battery
            BackpackControl.batteryLocationID = 0;
            BackpackControl.batteryCharge = 0f;
            BackpackControl.batteryIsInBackpack = false;
            
            // Set flag so GhostSceneInterceptPatch catches the death coroutine's LoadScene("Home")
            // and re-enables the world without reloading the scene
            IsGhostSceneReload = true;
        }
        
        /// <summary>
        /// Called by the scene load interceptor to reload the current Night scene as ghost
        /// </summary>
        public void ReloadSceneAsGhost()
        {
            if (string.IsNullOrEmpty(_ghostSceneToReload))
            {
                Plugin.Log.LogWarning("[Ghost] ReloadSceneAsGhost called but no scene to reload!");
                return;
            }
            
            // Save puzzle state BEFORE reloading — PuzzleMaster.Start() will reset everything
            SavePuzzleStateForGhost();
            
            Plugin.Log.LogInfo($"[Ghost] ReloadSceneAsGhost - Loading: {_ghostSceneToReload}");
            IsLoadingFromSync = true; // Prevent sync loop
            SceneManager.LoadScene(_ghostSceneToReload);
        }
        
        /// <summary>
        /// Called after scene loads when we're a ghost - teleport to partner
        /// </summary>
        public void HandleGhostSceneLoaded()
        {
            if (!_localIsGhost || !_pendingGhostTeleport) return;
            
            _pendingGhostTeleport = false;
            IsGhostSceneReload = false;
            
            Plugin.Log.LogInfo("[Ghost] Scene loaded as ghost, teleporting to partner");
            
            // Re-send ghost state so remote players know we're a ghost
            // (they recreate their RemotePlayer objects on scene load)
            _writer.Reset();
            _writer.Put(PACKET_DEATH_GHOST);
            _writer.Put(true); // isGhost
            _writer.Put(0); // deathType (0 = ghost respawn)
            SendToAllPeers(true);
            
            // Create ghost vision light so the ghost can see without a flashlight
            CreateGhostVision();
            
            // Schedule puzzle state restore — needs to happen after PuzzleMaster.Start() runs
            // We use a frame delay so Start() has already executed by the time we restore
            _pendingGhostPuzzleRestore = true;
            _ghostPuzzleRestoreFrames = 2; // Wait 2 frames to be safe
            
            // Teleport to partner's position after a short delay (let scene initialize)
            _ghostTeleportDelay = 1.0f;
        }
        
        private bool _pendingGhostPuzzleRestore = false;
        private int _ghostPuzzleRestoreFrames = 0;
        
        private GameObject _ghostVisionLight;
        
        /// <summary>
        /// Create a subtle ambient light attached to the ghost player's head
        /// so they can see even without a flashlight
        /// </summary>
        private void CreateGhostVision()
        {
            // Destroy old one if exists
            if (_ghostVisionLight != null)
            {
                Object.Destroy(_ghostVisionLight);
                _ghostVisionLight = null;
            }
            
            // Find the camera/head to attach to
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            if (cam == null)
            {
                Plugin.Log.LogWarning("[Ghost] No camera found for ghost vision");
                return;
            }
            
            // Create a point light that follows the ghost
            _ghostVisionLight = new GameObject("GhostVision");
            _ghostVisionLight.transform.SetParent(cam.transform);
            _ghostVisionLight.transform.localPosition = Vector3.zero;
            
            var light = _ghostVisionLight.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.6f, 0.6f, 0.8f); // Slight blue tint for ghostly feel
            light.intensity = 0.8f;
            light.range = 6f;
            light.shadows = LightShadows.None;
            
            Plugin.Log.LogInfo("[Ghost] Created ghost vision light");
        }
        
        /// <summary>
        /// Destroy ghost vision light
        /// </summary>
        private void DestroyGhostVision()
        {
            if (_ghostVisionLight != null)
            {
                Object.Destroy(_ghostVisionLight);
                _ghostVisionLight = null;
            }
        }
        
        private float _ghostTeleportDelay = 0f;
        
        /// <summary>
        /// Teleport ghost to partner without scene reload. Called from GhostSceneInterceptPatch.
        /// </summary>
        public void TeleportToPartner()
        {
            Plugin.Log.LogInfo($"[Ghost] TeleportToPartner called, setting delay=1.5s, spawnCaptured={_spawnPointCaptured}, spawnPoint={_levelSpawnPoint}");
            _ghostTeleportDelay = 1.5f;
            _pendingGhostTeleport = false;
            
            // Re-send ghost state so remote players know we're a ghost
            _writer.Reset();
            _writer.Put(PACKET_DEATH_GHOST);
            _writer.Put(true);
            _writer.Put(0); // ghost respawn
            SendToAllPeers(true);
        }
        
        /// <summary>
        /// Update ghost teleport (called from main Update)
        /// </summary>
        private void UpdateGhostTeleport()
        {
            if (_ghostTeleportDelay <= 0f) return;
            
            _ghostTeleportDelay -= Time.deltaTime;
            if (_ghostTeleportDelay > 0f) return;
            
            // Teleport to spawn point at floor level (Y=0) — VR tracking adds head height
            Vector3 targetPos = _levelSpawnPoint;
            
            if (!_spawnPointCaptured)
            {
                // Fallback: try partner position
                foreach (var kvp in _remotePlayers)
                {
                    if (kvp.Value.Head != null)
                    {
                        targetPos = kvp.Value.Head.transform.position;
                        break;
                    }
                }
            }
            
            // Force Y to 0 (floor level) — the spawn point Y includes standing offset
            // which causes clipping when combined with VR head tracking
            targetPos.y = 0f;
            
            Plugin.Log.LogInfo($"[Ghost] Teleporting to {(_spawnPointCaptured ? "spawn" : "partner")} at floor level: {targetPos}");
            TeleportLocalPlayer(targetPos);
        }
        
        /// <summary>
        /// Teleport the local player to a position
        /// </summary>
        private void TeleportLocalPlayer(Vector3 position)
        {
            var mtc = Object.FindObjectOfType<MoveTypeController>();
            
            // Force standing mode
            if (mtc != null)
            {
                mtc.debugSwitch = true;
                if (mtc.ccol != null) mtc.ccol.height = mtc.standingHeight;
                if (mtc.cmpb != null) mtc.cmpb.moveSpeed = mtc.standingSpeed;
                if (_gorillaPlayer != null) _gorillaPlayer.maxArmLength = 0f;
            }
            
            // Find GorillaPlayer fresh — the cached _gorillaPlayer may be null
            var gorillaPlayer = _gorillaPlayer ?? GorillaLocomotion.Player.Instance;
            if (gorillaPlayer == null)
                gorillaPlayer = Object.FindObjectOfType<GorillaLocomotion.Player>();
            
            if (gorillaPlayer != null)
            {
                // Get and reset the rigidbody
                var rbField = typeof(GorillaLocomotion.Player).GetField("playerRigidBody", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var rb = rbField?.GetValue(gorillaPlayer) as Rigidbody;
                
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.position = position;
                    rb.transform.position = position;
                }
                
                gorillaPlayer.transform.position = position;
                
                // Also reset last positions so locomotion doesn't calculate huge deltas
                var lastLeftField = typeof(GorillaLocomotion.Player).GetField("lastLeftHandPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var lastRightField = typeof(GorillaLocomotion.Player).GetField("lastRightHandPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var lastHeadField = typeof(GorillaLocomotion.Player).GetField("lastHeadPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (lastLeftField != null && gorillaPlayer.leftHandTransform != null)
                    lastLeftField.SetValue(gorillaPlayer, gorillaPlayer.leftHandTransform.position);
                if (lastRightField != null && gorillaPlayer.rightHandTransform != null)
                    lastRightField.SetValue(gorillaPlayer, gorillaPlayer.rightHandTransform.position);
                
                var headColliderField = typeof(GorillaLocomotion.Player).GetField("headCollider", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (lastHeadField != null && headColliderField != null)
                {
                    var hc = headColliderField.GetValue(gorillaPlayer) as SphereCollider;
                    if (hc != null) lastHeadField.SetValue(gorillaPlayer, hc.transform.position);
                }
                
                // Update cache
                _gorillaPlayer = gorillaPlayer;
                
                Plugin.Log.LogInfo($"[Ghost] Teleported GorillaPlayer to {position}, rb.position={rb?.position}");
                return;
            }
            
            Plugin.Log.LogWarning("[Ghost] GorillaPlayer not found even via FindObjectOfType!");
        }
        
        /// <summary>
        /// Reset ghost state (called on disconnect or returning to Home normally)
        /// </summary>
        public void ResetGhostState()
        {
            bool wasGhost = _localIsGhost;
            _localIsGhost = false;
            _pendingGhostTeleport = false;
            _pendingHomeLoad = false;
            _pendingHomeLoadTimer = 0f;
            _ghostSceneToReload = null;
            _ghostTeleportDelay = 0f;
            _pendingGhostPuzzleRestore = false;
            _ghostPuzzleRestoreFrames = 0;
            _savedGhostPuzzleState = null;
            IsGhostSceneReload = false;
            DestroyGhostVision();
            
            // IMPORTANT: Tell remote players we're no longer a ghost
            if (wasGhost && _steam != null && _steam.IsRunning)
            {
                _writer.Reset();
                _writer.Put(PACKET_DEATH_GHOST);
                _writer.Put(false);
                _writer.Put(0);
                SendToAllPeers(true);
            }
            
            Plugin.Log.LogInfo("[Ghost] Ghost state reset");
        }
        
        // NOTE: HandleDeathGhost was removed - it was dead code (HandleDeathGhostPacket is the active handler)
        
        #region Version Check
        
        private void SendVersionCheck(int peerId)
        {
            _writer.Reset();
            _writer.Put(PACKET_VERSION_CHECK);
            _writer.Put(PluginInfo.PLUGIN_VERSION);
            _steam.SendTo(peerId, _writer.GetBytes(), true);
            Plugin.Log.LogInfo($"Sent version {PluginInfo.PLUGIN_VERSION} to peer {peerId}");
        }
        
        private void HandleVersionCheck(int peerId, PacketReader reader)
        {
            string theirVersion = reader.GetString();
            _peerVersions[peerId] = theirVersion;
            
            Plugin.Log.LogInfo($"Peer {peerId} version: {theirVersion} (ours: {PluginInfo.PLUGIN_VERSION})");
            
            if (theirVersion != PluginInfo.PLUGIN_VERSION)
            {
                Plugin.Log.LogWarning($"VERSION MISMATCH! Peer {peerId} has {theirVersion}, we have {PluginInfo.PLUGIN_VERSION}");
                OnVersionMismatch?.Invoke(peerId, theirVersion);
            }
        }
        
        #endregion
        
        #region Ping/Pong
        
        private void SendPingToAll()
        {
            float sendTime = Time.realtimeSinceStartup;
            
            foreach (var peerId in _connectedPeerIds)
            {
                _pendingPings[peerId] = sendTime;
                
                _writer.Reset();
                _writer.Put(PACKET_PING);
                _writer.Put(sendTime);
                _steam.SendTo(peerId, _writer.GetBytes(), false);  // Unreliable for speed
            }
        }
        
        private void HandlePing(int peerId, PacketReader reader)
        {
            float theirTime = reader.GetFloat();
            
            // Send pong back with their timestamp
            _writer.Reset();
            _writer.Put(PACKET_PONG);
            _writer.Put(theirTime);
            _steam.SendTo(peerId, _writer.GetBytes(), false);
        }
        
        private void HandlePong(int peerId, PacketReader reader)
        {
            float sentTime = reader.GetFloat();
            float roundTrip = (Time.realtimeSinceStartup - sentTime) * 1000f;  // Convert to ms
            _peerPings[peerId] = roundTrip;
            _pendingPings.Remove(peerId);
        }
        
        #endregion
        
        #region Host Migration
        
        public event Action OnBecameHost;  // Fired when this client becomes the new host
        
        private void HandleHostMigration(int peerId, PacketReader reader)
        {
            // The old host is telling us we're the new host
            bool youAreNewHost = reader.GetBool();
            
            if (youAreNewHost)
            {
                Plugin.Log.LogInfo("HOST MIGRATION: We are now the host!");
                _steam.BecomeHost();
                OnBecameHost?.Invoke();
            }
        }
        
        // Called by host before disconnecting to transfer host to another player
        public void TransferHost(int newHostPeerId)
        {
            if (!_steam.IsHost) return;
            
            _writer.Reset();
            _writer.Put(PACKET_HOST_MIGRATION);
            _writer.Put(true);  // You are the new host
            _steam.SendTo(newHostPeerId, _writer.GetBytes(), true);
            
            Plugin.Log.LogInfo($"Transferred host to peer {newHostPeerId}");
        }
        
        // Get the first connected peer (for host migration)
        public int GetFirstConnectedPeer()
        {
            foreach (var peerId in _connectedPeerIds)
            {
                return peerId;
            }
            return -1;
        }
        
        #endregion
        
        #region Vent Door Sync
        
        // Track vent doors by their instance ID to avoid duplicates
        private Dictionary<int, float> _ventDoorTimers = new Dictionary<int, float>();
        
        public void SendVentDoorTrigger(int ventId, Vector3 position)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_VENT_DOOR);
            _writer.Put(ventId);
            WriteVector3(_writer, position);
            SendToAllPeers(true);
        }
        
        private void HandleVentDoor(PacketReader reader)
        {
            int ventId = reader.GetInt();
            Vector3 position = ReadVector3(reader);
            
            // Find the vent door closest to this position
            var vents = Object.FindObjectsOfType<VentAnimControl>();
            VentAnimControl closest = null;
            float closestDist = float.MaxValue;
            
            foreach (var vent in vents)
            {
                float dist = Vector3.Distance(vent.transform.position, position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = vent;
                }
            }
            
            if (closest != null && closestDist < 2f)
            {
                // Set the timer to trigger the door open
                var timerField = typeof(VentAnimControl).GetField("timer1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (timerField != null)
                {
                    timerField.SetValue(closest, 10);
                }
            }
        }
        
        #endregion
        
        #region Painting Death Sync
        
        public void SendPaintingDeath()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_PAINTING_DEATH);
            SendToAllPeers(true);
            Plugin.Log.LogInfo("[Host] Sent painting death to all players");
        }
        
        private void HandlePaintingDeath()
        {
            Plugin.Log.LogInfo("[Client] Received painting death event");
            
            // Only kill if player is in the main room (same as clown behavior)
            var mtc = Object.FindObjectOfType<MoveTypeController>();
            if (mtc == null || !mtc.isInMainRoom)
                return;
            
            // Find the painting control and jumpscare controller
            var paintingControl = Object.FindObjectOfType<paintingControl>();
            if (paintingControl != null)
            {
                // Get the jumpscare controller reference
                var jscField = typeof(paintingControl).GetField("jsc", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (jscField != null)
                {
                    var jsc = jscField.GetValue(paintingControl) as jumpscareController;
                    if (jsc != null)
                    {
                        // Pick a random death type like the original
                        int deathType = UnityEngine.Random.Range(0, 5);
                        switch (deathType)
                        {
                            case 0: jsc.onDeathHarold(); break;
                            case 1: jsc.onDeathHenry(); break;
                            case 2: jsc.onDeathSmiley(); break;
                            case 3: jsc.onDeathSparky(); break;
                            case 4: jsc.onDeathJeff(); break;
                        }
                    }
                }
            }
        }
        
        #endregion
        
        #region End Scene Sync
        
        private EndControl _endControl;
        private int _lastEndImageID = -1;
        private float _lastEndBarFill = -1f;
        
        public void CheckEndSceneSync()
        {
            // Only host syncs end scene progress
            if (_steam == null || !_steam.IsRunning || !_steam.IsHost) return;
            
            if (_endControl == null)
            {
                _endControl = Object.FindObjectOfType<EndControl>();
            }
            
            if (_endControl == null) return;
            
            // Sync when imageID changes or barFill changes significantly
            bool imageChanged = _endControl.imageID != _lastEndImageID;
            bool barChanged = Mathf.Abs(_endControl.barFill1 - _lastEndBarFill) > 50f;
            
            if (imageChanged || barChanged)
            {
                _lastEndImageID = _endControl.imageID;
                _lastEndBarFill = _endControl.barFill1;
                SendEndProgress(_endControl.imageID, _endControl.barFill1);
            }
        }
        
        private void SendEndProgress(int imageID, float barFill)
        {
            _writer.Reset();
            _writer.Put(PACKET_END_PROGRESS);
            _writer.Put(imageID);
            _writer.Put(barFill);
            SendToAllPeers(true);
        }
        
        private void HandleEndProgress(PacketReader reader)
        {
            int imageID = reader.GetInt();
            float barFill = reader.GetFloat();
            
            if (_endControl == null)
            {
                _endControl = Object.FindObjectOfType<EndControl>();
            }
            
            if (_endControl != null)
            {
                _endControl.imageID = imageID;
                _endControl.barFill1 = barFill;
            }
        }
        
        #endregion
        
        #region Ear Covering Sync
        
        private bool _lastEarCoveringState = false;
        
        private void CheckEarCoveringSync()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            // Ghosts don't sync ear covering
            if (_localIsGhost) return;
            
            bool currentState = earMaster.isCoveringEars;
            if (currentState != _lastEarCoveringState)
            {
                _lastEarCoveringState = currentState;
                SendEarCovering(currentState);
            }
        }
        
        private void SendEarCovering(bool isCovering)
        {
            _writer.Reset();
            _writer.Put(PACKET_EAR_COVERING);
            _writer.Put(isCovering);
            SendToAllPeers(true);
        }
        
        private void HandleEarCovering(PacketReader reader)
        {
            _remotePlayerCoveringEars = reader.GetBool();
        }
        
        #endregion
    }
}
