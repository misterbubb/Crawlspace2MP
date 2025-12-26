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
        private const byte PACKET_PUZZLE_INIT = 12;
        private const byte PACKET_PUZZLE_COMPLETE = 13;
        private const byte PACKET_PUZZLE_BLOCK = 14;
        private const byte PACKET_CLOWN_HONK = 15;
        private const byte PACKET_VENT_SOUND = 16;
        private const byte PACKET_INTERACTION_LOCK = 17;
        private const byte PACKET_EXIT_DOOR_PROGRESS = 18;
        private const byte PACKET_CRANK_SYNC = 19;
        private const byte PACKET_DEATH_GHOST = 20;
        private const byte PACKET_VERSION_CHECK = 21;
        private const byte PACKET_PING = 22;
        private const byte PACKET_PONG = 23;
        private const byte PACKET_HOST_MIGRATION = 24;
        
        private Dictionary<int, RemotePlayer> _remotePlayers = new Dictionary<int, RemotePlayer>();
        
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
        
        // Ghost/death state tracking
        private bool _isLocalPlayerGhost = false;
        private Vector3 _levelSpawnPoint = Vector3.zero;
        private Quaternion _levelSpawnRotation = Quaternion.identity;
        private bool _spawnPointCaptured = false;
        public bool IsLocalPlayerGhost => _isLocalPlayerGhost;
        
        // Track connected peer IDs separately (survives scene changes)
        private HashSet<int> _connectedPeerIds = new HashSet<int>();
        
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
        
        // Battery sync tracking - SEPARATE batteries per player
        // Each player has their own battery state, but we sync slot placements
        private float _lastBatterySyncTime = 0f;
        private float _batterySyncInterval = 0.1f; // Sync battery 10 times per second
        private int _lastBatteryLocationID = -999;
        private bool _lastBatteryInBackpack = false;
        private float _lastBatteryCharge = -1f;
        
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
        private const float LOCK_TIMEOUT = 0.5f; // Lock expires after 0.5 seconds without refresh
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
                            Plugin.Log.LogInfo($"[Lock] Acquired expired lock: {interactionId}");
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
                Plugin.Log.LogInfo($"[Lock] Acquired lock: {interactionId}");
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
                    Plugin.Log.LogInfo($"[Lock] Released lock: {interactionId}");
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
        
        // Exit door progress sync
        private LeaveDoorControl _leaveDoor;
        private int _lastDoorLeaveTimer = 0;
        private float _lastDoorSyncTime = 0f;
        
        private PacketWriter _writer = new PacketWriter(1024);
        private SteamTransport _steam;

        public void Initialize(SteamTransport steam)
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
            
            // Clean up voice player
            MPManager.Instance?.VoiceChat?.OnPeerDisconnected(peerId);
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
                    HandleBatterySyncPacket(peerId, reader);
                    break;
                case PACKET_PUZZLE_INIT:
                    HandlePuzzleInitPacket(reader);
                    break;
                case PACKET_PUZZLE_COMPLETE:
                    HandlePuzzleCompletePacket(reader);
                    break;
                case PACKET_PUZZLE_BLOCK:
                    HandlePuzzleBlockPacket(reader);
                    break;
                case PACKET_CLOWN_HONK:
                    HandleClownHonkPacket();
                    break;
                case PACKET_VENT_SOUND:
                    HandleVentSoundPacket(reader);
                    break;
                case PACKET_INTERACTION_LOCK:
                    HandleInteractionLockPacket(reader);
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
                case VoiceChat.PACKET_VOICE:
                    MPManager.Instance?.VoiceChat?.OnVoiceDataReceived(peerId, reader);
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
            
            remote.SetTargets(isStanding, bodyPos, bodyRot, headPos, headRot, 
                              leftHandPos, leftHandRot, rightHandPos, rightHandRot);
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
            
            if (!_steam.IsHost)
            {
                calenderControl.nightSelected = nightSelected;
                IsLoadingFromSync = true;
                SceneManager.LoadScene(sceneName);
            }
        }
        
        private void HandleNightSelectedPacket(PacketReader reader)
        {
            int night = reader.GetInt();
            Plugin.Log.LogInfo($"Received night selection: {night}");
            calenderControl.nightSelected = night;
        }
        
        private void HandleTvSyncPacket(PacketReader reader)
        {
            double frame = reader.GetDouble();
            if (_tvVideoPlayer != null && _tvVideoPlayer.isPrepared)
            {
                _tvVideoPlayer.frame = (long)frame;
            }
        }
        
        private void HandlePaintingSyncPacket(PacketReader reader)
        {
            if (_paintingControl == null) return;
            _paintingControl.intpaintingSquare1 = reader.GetInt();
            _paintingControl.intpaintingSquare2 = reader.GetInt();
            _paintingControl.intpaintingSquare3 = reader.GetInt();
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
            // Monster sync - client receives positions from host
            // Implementation depends on monster types
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
        
        private void HandlePuzzleInitPacket(PacketReader reader)
        {
            int completed = reader.GetInt();
            int required = reader.GetInt();
            
            if (_puzzleMaster != null)
            {
                PuzzleMaster.totalCompletedPuzzles = completed;
                PuzzleMaster.requiredPuzzles = required;
            }
        }
        
        private void HandlePuzzleCompletePacket(PacketReader reader)
        {
            int puzzleId = reader.GetInt();
            int total = reader.GetInt();
            PuzzleMaster.totalCompletedPuzzles = total;
        }
        
        private void HandlePuzzleBlockPacket(PacketReader reader)
        {
            int blockNumber = reader.GetInt();
            int blockIdValue = reader.GetInt();
            // Apply puzzle block state
        }
        
        private void HandleClownHonkPacket()
        {
            // _clown?.playHonk(); // Method may not exist in current game version
        }
        
        private void HandleVentSoundPacket(PacketReader reader)
        {
            var position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            int soundIndex = reader.GetInt();
            // Play vent sound at position
        }
        
        private void HandleInteractionLockPacket(PacketReader reader)
        {
            string interactionId = reader.GetString();
            bool locked = reader.GetBool();
            int peerId = reader.GetInt(); // Need to read peer ID too
            
            if (locked)
                _interactionLocks[interactionId] = peerId;
            else
                _interactionLocks.Remove(interactionId);
        }
        
        private void HandleExitDoorProgressPacket(PacketReader reader)
        {
            int timer = reader.GetInt();
            int required = reader.GetInt();
            
            // LeaveDoorControl fields may have changed in game update
            // if (_leaveDoor != null)
            // {
            //     _leaveDoor.leaveTimer = timer;
            //     _leaveDoor.requiredTime = required;
            // }
        }
        
        private void HandleCrankSyncPacket(PacketReader reader)
        {
            float rotation = reader.GetFloat();
            // Apply crank rotation
        }
        
        private void HandleDeathGhostPacket(int peerId, PacketReader reader)
        {
            bool isGhost = reader.GetBool();
            int deathType = reader.GetInt();
            
            if (_remotePlayers.TryGetValue(peerId, out var remote))
            {
                remote.SetGhostState(isGhost);
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
            Plugin.Log.LogInfo($"OnSceneLoaded: {scene.name}, lastScene={_lastScene}, IsLoadingFromSync={IsLoadingFromSync}");
            
            // Clean up minimap friend indicator
            MinimapFriendPatch.Cleanup();
            
            // Clear all interaction locks on scene change
            ClearAllLocks();
            
            // Reset ghost state on scene change (new level = alive again)
            _isLocalPlayerGhost = false;
            _spawnPointCaptured = false;
            
            // Only process if this is actually a new scene
            if (scene.name == _lastScene && !IsLoadingFromSync)
            {
                Plugin.Log.LogInfo($"Same scene loaded again, not clearing remote players");
                return;
            }
            
            Plugin.Log.LogInfo($"Scene loaded: {scene.name}, resetting IsLoadingFromSync, recreating remote players");
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
            
            // Clear pending puzzle init
            _pendingPuzzleInit = null;
            
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
            _connectedPeerIds.Clear();
            _remoteBatteryStates.Clear();
            _pendingPuzzleInit = null;
            _isLocalPlayerGhost = false;
            Plugin.Log.LogInfo("PlayerSync cleaned up");
        }
        private int _updateCount = 0;
        
        public void Update()
        {
            _updateCount++;
            
            if (_steam == null) return;
            
            // Send pings periodically to measure latency
            if (_steam.IsRunning && _steam.IsConnected && Time.realtimeSinceStartup - _lastPingTime > PING_INTERVAL)
            {
                _lastPingTime = Time.realtimeSinceStartup;
                SendPingToAll();
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
                    Plugin.Log.LogInfo($"[Client] SceneManager.LoadScene({sceneToLoad}) called successfully");
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
            
            if (!_steam.IsConnected && !_steam.IsHost) return;
            if (!_steam.IsRunning) return;
            
            // Capture spawn point early in the level (before player moves much)
            if (!_spawnPointCaptured && _updateCount > 10) // Wait a few frames for scene to settle
            {
                CaptureSpawnPoint();
            }
            
            // Log every 5 seconds
            if (_updateCount % 300 == 0)
            {
                Plugin.Log.LogInfo($"PlayerSync.Update: gorillaPlayer={_gorillaPlayer != null}, remotePlayers={_remotePlayers.Count}, isGhost={_isLocalPlayerGhost}, ping={AveragePing:F0}ms");
            }
            
            // Cache references if needed - with detailed debugging
            if (_gorillaPlayer == null && _handControl == null && _mainCamera == null && _ovrHead == null)
            {
                Plugin.Log.LogInfo("=== SEARCHING FOR TRACKING REFERENCES ===");
                
                _gorillaPlayer = Player.Instance;
                _handControl = Object.FindObjectOfType<HandControl>();
                _moveController = Object.FindObjectOfType<MoveTypeController>();
                _mainCamera = Camera.main;
                
                // Try to find OVRCameraRig for hand tracking (using reflection since we don't have the Oculus DLL)
                Plugin.Log.LogInfo("Searching for OVRCameraRig...");
                foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                {
                    var type = mb.GetType();
                    if (type.Name == "OVRCameraRig")
                    {
                        Plugin.Log.LogInfo($"Found OVRCameraRig: {mb.gameObject.name}");
                        
                        // Get the anchor transforms via reflection
                        var centerEye = type.GetProperty("centerEyeAnchor")?.GetValue(mb) as Transform;
                        var leftHand = type.GetProperty("leftHandAnchor")?.GetValue(mb) as Transform;
                        var rightHand = type.GetProperty("rightHandAnchor")?.GetValue(mb) as Transform;
                        
                        Plugin.Log.LogInfo($"  centerEyeAnchor: {(centerEye != null ? centerEye.name : "NULL")}");
                        Plugin.Log.LogInfo($"  leftHandAnchor: {(leftHand != null ? leftHand.name : "NULL")}");
                        Plugin.Log.LogInfo($"  rightHandAnchor: {(rightHand != null ? rightHand.name : "NULL")}");
                        
                        if (centerEye != null && leftHand != null && rightHand != null)
                        {
                            _ovrHead = centerEye;
                            _ovrLeftHand = leftHand;
                            _ovrRightHand = rightHand;
                            Plugin.Log.LogInfo($"SUCCESS: Using OVRCameraRig for tracking!");
                        }
                        break;
                    }
                }
                
                // Try BackpackControl for hand references (it has leftHand and rightHand GameObjects)
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    Plugin.Log.LogInfo("Searching for BackpackControl...");
                    var backpack = Object.FindObjectOfType<BackpackControl>();
                    if (backpack != null)
                    {
                        Plugin.Log.LogInfo($"Found BackpackControl on: {backpack.gameObject.name}");
                        Plugin.Log.LogInfo($"  leftHand field: {(backpack.leftHand != null ? backpack.leftHand.name : "NULL")}");
                        Plugin.Log.LogInfo($"  rightHand field: {(backpack.rightHand != null ? backpack.rightHand.name : "NULL")}");
                        Plugin.Log.LogInfo($"  cam field: {(backpack.cam != null ? backpack.cam.name : "NULL")}");
                        
                        if (backpack.leftHand != null)
                            _ovrLeftHand = backpack.leftHand.transform;
                        if (backpack.rightHand != null)
                            _ovrRightHand = backpack.rightHand.transform;
                        if (backpack.cam != null && _ovrHead == null)
                            _ovrHead = backpack.cam.transform;
                        
                        if (_ovrLeftHand != null && _ovrRightHand != null)
                        {
                            Plugin.Log.LogInfo($"SUCCESS: Using BackpackControl for hand tracking!");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogInfo("BackpackControl NOT FOUND in scene!");
                    }
                }
                
                // Try XR controllers directly - search by type name to avoid assembly issues
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    Plugin.Log.LogInfo("Searching for XR controllers by type name...");
                    foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                    {
                        var typeName = mb.GetType().Name;
                        if (typeName == "ActionBasedController" || typeName == "XRController" || typeName == "XRBaseController")
                        {
                            Plugin.Log.LogInfo($"  Found {typeName}: {mb.gameObject.name}, pos={mb.transform.position}");
                            string nameLower = mb.gameObject.name.ToLower();
                            if (nameLower.Contains("left") && _ovrLeftHand == null)
                            {
                                _ovrLeftHand = mb.transform;
                                Plugin.Log.LogInfo($"    -> Using as LEFT hand");
                            }
                            else if (nameLower.Contains("right") && _ovrRightHand == null)
                            {
                                _ovrRightHand = mb.transform;
                                Plugin.Log.LogInfo($"    -> Using as RIGHT hand");
                            }
                        }
                    }
                }
                
                // Try finding any transform with "hand" in the name
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    Plugin.Log.LogInfo("Searching for any GameObjects with 'hand' in name...");
                    var allTransforms = Object.FindObjectsOfType<Transform>();
                    int handCount = 0;
                    foreach (var t in allTransforms)
                    {
                        string nameLower = t.name.ToLower();
                        if (nameLower.Contains("hand") || nameLower.Contains("controller"))
                        {
                            handCount++;
                            if (handCount <= 20) // Limit logging
                            {
                                Plugin.Log.LogInfo($"  Found: {t.name} at {t.position} (parent: {(t.parent != null ? t.parent.name : "none")})");
                            }
                        }
                    }
                    Plugin.Log.LogInfo($"Total hand/controller objects found: {handCount}");
                }
                
                // Try crankControl for hand references (it has handLeft and handRight GameObjects)
                if (_ovrLeftHand == null || _ovrRightHand == null)
                {
                    Plugin.Log.LogInfo("Searching for crankControl...");
                    var crank = Object.FindObjectOfType<crankControl>();
                    if (crank != null)
                    {
                        Plugin.Log.LogInfo($"Found crankControl on: {crank.gameObject.name}");
                        Plugin.Log.LogInfo($"  handLeft field: {(crank.handLeft != null ? crank.handLeft.name : "NULL")}");
                        Plugin.Log.LogInfo($"  handRight field: {(crank.handRight != null ? crank.handRight.name : "NULL")}");
                        
                        if (crank.handLeft != null && _ovrLeftHand == null)
                        {
                            _ovrLeftHand = crank.handLeft.transform;
                            Plugin.Log.LogInfo($"  -> Using handLeft as LEFT hand");
                        }
                        if (crank.handRight != null && _ovrRightHand == null)
                        {
                            _ovrRightHand = crank.handRight.transform;
                            Plugin.Log.LogInfo($"  -> Using handRight as RIGHT hand");
                        }
                        
                        if (_ovrLeftHand != null && _ovrRightHand != null)
                        {
                            Plugin.Log.LogInfo($"SUCCESS: Using crankControl for hand tracking!");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogInfo("crankControl NOT FOUND in scene (only exists in gameplay scenes with charger)");
                    }
                }
                
                // Log final results
                Plugin.Log.LogInfo("=== TRACKING SEARCH RESULTS ===");
                Plugin.Log.LogInfo($"GorillaPlayer: {(_gorillaPlayer != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log.LogInfo($"HandControl: {(_handControl != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log.LogInfo($"MoveController: {(_moveController != null ? "FOUND" : "NOT FOUND")}");
                Plugin.Log.LogInfo($"MainCamera: {(_mainCamera != null ? _mainCamera.name : "NOT FOUND")}");
                Plugin.Log.LogInfo($"OVR Head: {(_ovrHead != null ? _ovrHead.name : "NOT FOUND")}");
                Plugin.Log.LogInfo($"OVR LeftHand: {(_ovrLeftHand != null ? _ovrLeftHand.name : "NOT FOUND")}");
                Plugin.Log.LogInfo($"OVR RightHand: {(_ovrRightHand != null ? _ovrRightHand.name : "NOT FOUND")}");
                
                if (_gorillaPlayer != null)
                {
                    _playerTransform = _gorillaPlayer.transform;
                    Plugin.Log.LogInfo($"GorillaPlayer hands: L={_gorillaPlayer.leftHandTransform?.name}, R={_gorillaPlayer.rightHandTransform?.name}");
                }
                
                if (_handControl != null)
                {
                    Plugin.Log.LogInfo($"HandControl: Head={_handControl.Head != null}, LTarget={_handControl.LTarget != null}, RTarget={_handControl.RTarget != null}");
                }
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
            
            // Update remote player interpolation
            foreach (var remote in _remotePlayers.Values)
            {
                remote.UpdateInterpolation();
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
            
            // Check monster sync (host only)
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
            
            // Check exit door progress sync (host only)
            CheckExitDoorSync();
            
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
                    Plugin.Log.LogInfo($"Found TV VideoPlayer");
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
                            Plugin.Log.LogInfo($"Found MoviePlayerSample VideoPlayer");
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
            Plugin.Log.LogInfo($"[Host] Sent TV sync: frame {frame}");
        }
        
        private void CheckPaintingSync()
        {
            // Only host syncs paintings
            if (!_steam.IsHost) return;
            
            // Only sync periodically
            if (Time.time - _lastPaintingSyncTime < _paintingSyncInterval) return;
            _lastPaintingSyncTime = Time.time;
            
            // Find painting controller if not cached
            if (_paintingControl == null)
            {
                _paintingControl = Object.FindObjectOfType<paintingControl>();
                if (_paintingControl != null)
                {
                    Plugin.Log.LogInfo("Found paintingControl");
                }
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
                Plugin.Log.LogInfo($"Scene changed from {_lastScene} to {currentScene}");
                _lastScene = currentScene;
                
                // Only host broadcasts scene changes, and only if not loading from sync
                if (_steam.IsHost && !IsLoadingFromSync)
                {
                    SendSceneChange(currentScene);
                    Plugin.Log.LogInfo($"[Host] Scene changed to: {currentScene}, broadcasting to clients");
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
                _leaveDoor = null;
                _lastDoorLeaveTimer = 0;
                _crankControl = null;
                _lastCrankCharge = -1f;
                _lastCrankHasBattery = false;
                
                // DON'T clear remote players here - let OnSceneLoaded handle recreation
            }
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
                    Plugin.Log.LogInfo($"Recreating remote player for peer {peerId} after scene load");
                    var remotePlayer = new RemotePlayer(peerId);
                    _remotePlayers[peerId] = remotePlayer;
                }
                else
                {
                    // Try to upgrade visuals if we now have access to real models
                    _remotePlayers[peerId].TryUpgradeVisuals();
                }
            }
            
            Plugin.Log.LogInfo($"Remote players after recreation: {_remotePlayers.Count}");
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
                if (_localFlashlightControl != null)
                {
                    Plugin.Log.LogInfo("Found FlashlightControl");
                }
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
                    if (_localFlashlight != null)
                    {
                        Plugin.Log.LogInfo("Found Flashlight class");
                    }
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
                Plugin.Log.LogInfo($"Flashlight toggled: {currentState}, battery={BackpackControl.batteryCharge:F1}, inBackpack={BackpackControl.batteryIsInBackpack}");
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
            
            _writer.Put(isStanding);
            WriteVector3(_writer, bodyPos);
            WriteQuaternion(_writer, bodyRot);
            WriteVector3(_writer, headPos);
            WriteQuaternion(_writer, headRot);
            WriteVector3(_writer, leftHandPos);
            WriteQuaternion(_writer, leftHandRot);
            WriteVector3(_writer, rightHandPos);
            WriteQuaternion(_writer, rightHandRot);
            
            // Log every 100 sends (~3 seconds)
            if (_sendCount % 100 == 1)
            {
                Plugin.Log.LogInfo($"[Send] src={source} head={headPos}, lHand={leftHandPos}, rHand={rightHandPos}");
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
        
        private void HandleInteractionLock(int peerId, PacketReader reader)
        {
            string interactionId = reader.GetString();
            bool locked = reader.GetBool();
            
            if (locked)
            {
                // Remote player acquired lock
                _interactionLocks[interactionId] = peerId;
                _lockTimestamps[interactionId] = Time.time;
                Plugin.Log.LogInfo($"[Lock] Peer {peerId} acquired lock: {interactionId}");
            }
            else
            {
                // Remote player released lock
                if (_interactionLocks.TryGetValue(interactionId, out int holder) && holder == peerId)
                {
                    _interactionLocks.Remove(interactionId);
                    _lockTimestamps.Remove(interactionId);
                    Plugin.Log.LogInfo($"[Lock] Peer {peerId} released lock: {interactionId}");
                }
            }
        }
        
        private void HandleSceneChange(PacketReader reader)
        {
            string sceneName = reader.GetString();
            Plugin.Log.LogInfo($"[Client] Received scene change from host: {sceneName}");
            
            // Only clients should load - host already loaded
            if (!_steam.IsHost)
            {
                string currentScene = SceneManager.GetActiveScene().name;
                Plugin.Log.LogInfo($"[Client] Current scene check: GetActiveScene={currentScene}, _lastScene={_lastScene}");
                
                if (currentScene != sceneName)
                {
                    Plugin.Log.LogInfo($"[Client] Scheduling scene load: {sceneName} (currently in {currentScene})");
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
                    Plugin.Log.LogInfo($"[Client] Already in scene {sceneName}, ignoring");
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
                            Plugin.Log.LogInfo("[Client] Triggered fade effect");
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
        
        private void HandleNightSelected(PacketReader reader)
        {
            int night = reader.GetInt();
            Plugin.Log.LogInfo($"[Client] Received night selection from host: {night}");
            
            if (!_steam.IsHost)
            {
                calenderControl.nightSelected = night;
                
                // Try to update the calendar UI if it exists
                var calendar = Object.FindObjectOfType<calenderControl>();
                if (calendar != null)
                {
                    calendar.setNightText();
                }
            }
        }
        
        private void HandleTvSync(PacketReader reader)
        {
            long frame = reader.GetLong();
            
            // Only clients sync to host's TV
            if (_steam.IsHost) return;
            
            Plugin.Log.LogInfo($"[Client] Received TV sync: frame {frame}");
            
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
                    Plugin.Log.LogInfo($"[Client] TV synced to frame {frame} (was off by {diff} frames)");
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
                    Plugin.Log.LogInfo($"[Client] Paintings synced: T1={tall1}, T2={tall2}, T3={tall3}, S1={square1}, S2={square2}, S3={square3}");
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
            Plugin.Log.LogInfo($"Sent painting flash: {paintingId}");
        }
        
        private void HandlePaintingFlash(PacketReader reader)
        {
            int paintingId = reader.GetInt();
            Plugin.Log.LogInfo($"Received painting flash: {paintingId}");
            
            // Find painting controller if not cached
            if (_paintingControl == null)
            {
                _paintingControl = Object.FindObjectOfType<paintingControl>();
            }
            
            if (_paintingControl != null)
            {
                // Set flag to prevent re-sending this flash
                _isReceivingPaintingFlash = true;
                _paintingControl.onPaintingFlash(paintingId);
                _isReceivingPaintingFlash = false;
            }
        }
        
        // Flag to prevent infinite loop when receiving painting flash
        private bool _isReceivingPaintingFlash = false;
        public bool IsReceivingPaintingFlash => _isReceivingPaintingFlash;
        
        // Flag to prevent infinite loop when receiving jeff flash
        private bool _isReceivingJeffFlash = false;
        public bool IsReceivingJeffFlash => _isReceivingJeffFlash;
        
        // Get all remote player head positions for monster targeting
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
            // Only host syncs monsters
            if (!_steam.IsHost) return;
            
            // Only sync periodically
            if (Time.time - _lastMonsterSyncTime < _monsterSyncInterval) return;
            _lastMonsterSyncTime = Time.time;
            
            // Find monsters if not cached
            FindMonsters();
            
            // Send monster sync data
            SendMonsterSync();;
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
            _writer.Reset();
            _writer.Put(PACKET_MONSTER_SYNC);
            
            // Sparky: position + state
            bool hasSparky = _sparky != null;
            _writer.Put(hasSparky);
            if (hasSparky)
            {
                WriteVector3(_writer, _sparky.transform.position);
                WriteQuaternion(_writer, _sparky.transform.rotation);
            }
            
            // Jeff: position + body visible
            bool hasJeff = _jeff != null;
            _writer.Put(hasJeff);
            if (hasJeff)
            {
                WriteVector3(_writer, _jeff.transform.position);
                WriteQuaternion(_writer, _jeff.transform.rotation);
                _writer.Put(_jeff.jeffBody != null && _jeff.jeffBody.activeSelf);
            }
            
            // Smile: position + isChasing
            bool hasSmile = _smile != null;
            _writer.Put(hasSmile);
            if (hasSmile)
            {
                WriteVector3(_writer, _smile.transform.position);
                WriteQuaternion(_writer, _smile.transform.rotation);
            }
            
            // Henry: position
            bool hasHenry = _henry != null;
            _writer.Put(hasHenry);
            if (hasHenry)
            {
                WriteVector3(_writer, _henry.transform.position);
                WriteQuaternion(_writer, _henry.transform.rotation);
            }
            
            // Harold: position
            bool hasHarold = _harold != null;
            _writer.Put(hasHarold);
            if (hasHarold)
            {
                WriteVector3(_writer, _harold.transform.position);
                WriteQuaternion(_writer, _harold.transform.rotation);
            }
            
            // Clown: which clown is active (0-6, or -1 if attacking/none)
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
        
        public void SendJeffFlash()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_JEFF_FLASH);
            SendToAllPeers(true);
            Plugin.Log.LogInfo("Sent Jeff flash");
        }
        
        private void HandleMonsterSync(PacketReader reader)
        {
            // Only clients receive monster sync
            if (_steam.IsHost) return;
            
            // Find monsters if not cached
            FindMonsters();
            
            // Sparky
            bool hasSparky = reader.GetBool();
            if (hasSparky)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                if (_sparky != null)
                {
                    _sparky.transform.position = pos;
                    _sparky.transform.rotation = rot;
                    // Disable NavMeshAgent on client so it doesn't fight the sync
                    if (_sparky.agent != null && _sparky.agent.enabled)
                    {
                        _sparky.agent.enabled = false;
                    }
                }
            }
            
            // Jeff
            bool hasJeff = reader.GetBool();
            if (hasJeff)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                bool bodyVisible = reader.GetBool();
                if (_jeff != null)
                {
                    _jeff.transform.position = pos;
                    _jeff.transform.rotation = rot;
                    if (_jeff.jeffBody != null)
                    {
                        _jeff.jeffBody.SetActive(bodyVisible);
                    }
                    if (_jeff.agent != null && _jeff.agent.enabled)
                    {
                        _jeff.agent.enabled = false;
                    }
                }
            }
            
            // Smile
            bool hasSmile = reader.GetBool();
            if (hasSmile)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                if (_smile != null)
                {
                    _smile.transform.position = pos;
                    _smile.transform.rotation = rot;
                }
            }
            
            // Henry
            bool hasHenry = reader.GetBool();
            if (hasHenry)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                if (_henry != null)
                {
                    // Henry uses NavMeshAgent, disable it on client
                    if (_henry.agent != null)
                    {
                        if (_henry.agent.enabled)
                        {
                            _henry.agent.enabled = false;
                            Plugin.Log.LogInfo("[Client] Disabled Henry NavMeshAgent");
                        }
                    }
                    _henry.transform.position = pos;
                    _henry.transform.rotation = rot;
                }
            }
            
            // Harold
            bool hasHarold = reader.GetBool();
            if (hasHarold)
            {
                Vector3 pos = ReadVector3(reader);
                Quaternion rot = ReadQuaternion(reader);
                if (_harold != null)
                {
                    _harold.transform.position = pos;
                    _harold.transform.rotation = rot;
                    if (_harold.agent != null && _harold.agent.enabled)
                    {
                        _harold.agent.enabled = false;
                    }
                }
            }
            
            // Clown
            bool hasClown = reader.GetBool();
            if (hasClown)
            {
                int activeClown = reader.GetInt();
                if (_clown != null)
                {
                    // Set all clowns inactive first
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
        
        private void HandleJeffFlash(PacketReader reader)
        {
            Plugin.Log.LogInfo("Received Jeff flash");
            
            if (_jeff == null)
            {
                _jeff = Object.FindObjectOfType<jeffBrain>();
            }
            
            if (_jeff != null)
            {
                _isReceivingJeffFlash = true;
                _jeff.onFlash();
                _isReceivingJeffFlash = false;
            }
        }
        
        // Battery sync - SEPARATE batteries per player
        // We sync: when battery is placed in a slot, taken from backpack, or charge changes significantly
        private void CheckBatterySync()
        {
            if (Time.time - _lastBatterySyncTime < _batterySyncInterval) return;
            _lastBatterySyncTime = Time.time;
            
            // Check if battery state changed significantly
            bool locationChanged = BackpackControl.batteryLocationID != _lastBatteryLocationID;
            bool backpackChanged = BackpackControl.batteryIsInBackpack != _lastBatteryInBackpack;
            bool chargeChanged = Mathf.Abs(BackpackControl.batteryCharge - _lastBatteryCharge) > 2f; // Only sync if charge changed by 2+
            
            // Always sync location/backpack changes, throttle charge syncs
            if (locationChanged || backpackChanged || chargeChanged)
            {
                // Log significant changes
                if (locationChanged)
                {
                    Plugin.Log.LogInfo($"[Battery] Location changed: {_lastBatteryLocationID} -> {BackpackControl.batteryLocationID}");
                }
                if (backpackChanged)
                {
                    Plugin.Log.LogInfo($"[Battery] Backpack changed: {_lastBatteryInBackpack} -> {BackpackControl.batteryIsInBackpack}");
                }
                
                _lastBatteryLocationID = BackpackControl.batteryLocationID;
                _lastBatteryInBackpack = BackpackControl.batteryIsInBackpack;
                _lastBatteryCharge = BackpackControl.batteryCharge;
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
        
        private void HandleBatterySync(PacketReader reader)
        {
            float charge = reader.GetFloat();
            int locationID = reader.GetInt();
            bool inBackpack = reader.GetBool();
            bool leftHolding = reader.GetBool();
            bool rightHolding = reader.GetBool();
            
            // Store remote player's battery state (for potential UI display)
            // We DON'T apply it to local BackpackControl - each player has their own battery
            
            // Get the peer ID from the current context (we need to track who sent this)
            // For now, just log it - we'll use this for visual feedback later
            Plugin.Log.LogInfo($"[Battery] Remote player: charge={charge:F1}, loc={locationID}, backpack={inBackpack}, L={leftHolding}, R={rightHolding}");
            
            // Update remote battery state for the first remote player (simplified for 2-player)
            if (_remotePlayers.Count > 0)
            {
                foreach (var kvp in _remotePlayers)
                {
                    if (!_remoteBatteryStates.ContainsKey(kvp.Key))
                    {
                        _remoteBatteryStates[kvp.Key] = new RemoteBatteryState();
                    }
                    _remoteBatteryStates[kvp.Key].Charge = charge;
                    _remoteBatteryStates[kvp.Key].LocationID = locationID;
                    _remoteBatteryStates[kvp.Key].InBackpack = inBackpack;
                    _remoteBatteryStates[kvp.Key].LeftHolding = leftHolding;
                    _remoteBatteryStates[kvp.Key].RightHolding = rightHolding;
                    
                    // Update remote player visual to show battery in hand
                    kvp.Value.SetBatteryState(leftHolding, rightHolding);
                    break; // Only first remote player for now
                }
            }
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
        
        // Get first remote player's full battery state (for crank visual sync)
        public RemoteBatteryState GetFirstRemoteBatteryState()
        {
            foreach (var state in _remoteBatteryStates.Values)
            {
                return state;
            }
            return null;
        }
        
        // Crank sync - so partner can see battery in crank, charge display, and crank rotation
        private Quaternion _lastCrankRotation = Quaternion.identity;
        
        private void CheckCrankSync()
        {
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
            bool chargeChanged = hasBattery && Mathf.Abs(crankCharge - _lastCrankCharge) > 0.5f;
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
        
        private void HandleCrankSync(PacketReader reader)
        {
            bool hasBattery = reader.GetBool();
            float charge = reader.GetFloat();
            Quaternion rotation = ReadQuaternion(reader);
            
            Plugin.Log.LogInfo($"[Crank] Received sync: hasBattery={hasBattery}, charge={charge:F1}");
            
            // Store remote crank state for blocking local battery placement
            _remoteCrankHasBattery = hasBattery;
            
            // Find crank if not cached
            if (_crankControl == null)
            {
                _crankControl = Object.FindObjectOfType<crankControl>();
            }
            
            if (_crankControl == null) return;
            
            // Update the crank's visual display directly
            if (_crankControl.batteryFill != null)
            {
                _crankControl.batteryFill.fillAmount = charge / 55f;
            }
            
            if (_crankControl.batteryIMG != null)
            {
                _crankControl.batteryIMG.SetActive(hasBattery);
            }
            
            // Update crank rotation
            _crankControl.transform.rotation = rotation;
        }
        
        // Track if remote player has battery in crank
        private bool _remoteCrankHasBattery = false;
        public bool RemoteHasBatteryInCrank => _remoteCrankHasBattery;
        
        // Puzzle sync tracking
        private float _lastPuzzleStateSyncTime = 0f;
        private float _puzzleStateSyncInterval = 2f; // Re-sync puzzle state every 2 seconds
        private int _lastTotalCompletedPuzzles = -1;
        
        private void CheckPuzzleInitSync()
        {
            // Only host sends puzzle init
            if (!_steam.IsHost) return;
            
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
            
            // Periodically re-sync puzzle completion state to ensure consistency
            if (Time.time - _lastPuzzleStateSyncTime >= _puzzleStateSyncInterval)
            {
                _lastPuzzleStateSyncTime = Time.time;
                
                // Check if completion count changed
                if (PuzzleMaster.totalCompletedPuzzles != _lastTotalCompletedPuzzles)
                {
                    _lastTotalCompletedPuzzles = PuzzleMaster.totalCompletedPuzzles;
                    // Re-send full puzzle state
                    SendPuzzleInit();
                    Plugin.Log.LogInfo($"[Host] Re-synced puzzle state: completed={PuzzleMaster.totalCompletedPuzzles}");
                }
            }
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
            var thisIDField = pbType.GetField("thisID", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Write data
            for (int i = 0; i < 9; i++)
            {
                _writer.Put(activeStates[i]);
                _writer.Put(presetIDs[i]);
                _writer.Put(completedStates[i]);
                
                // Also send current block states for active puzzles
                if (controllers[i] != null && controllers[i].cubeList != null && thisIDField != null)
                {
                    _writer.Put(controllers[i].cubeList.Length);
                    foreach (var block in controllers[i].cubeList)
                    {
                        int blockID = (int)thisIDField.GetValue(block);
                        _writer.Put(blockID);
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
        
        private void HandlePuzzleInit(PacketReader reader)
        {
            // Only clients receive puzzle init
            if (_steam.IsHost) return;
            
            Plugin.Log.LogInfo("[Client] Received puzzle init");
            
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
                    completedField?.SetValue(controllers[i], isCompleted);
                    
                    if (isCompleted)
                    {
                        // Completed puzzle - fan on, map indicator on
                        if (controllers[i].fanspin != null)
                            controllers[i].fanspin.isOn = true;
                        controllers[i].thisMapIndicator?.SetActive(true);
                    }
                    else if (!isActive)
                    {
                        // Inactive puzzle (not selected for this night)
                        controllers[i].thisMapIndicator?.SetActive(false);
                    }
                    else
                    {
                        // Active puzzle - not completed yet
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
                        Plugin.Log.LogInfo($"[Client] Applied {blockCount} block states to puzzle {i + 1}");
                    }
                }
            }
            
            _isReceivingPuzzleBlock = false;
            
            PuzzleMaster.totalCompletedPuzzles = _pendingPuzzleInit.TotalCompleted;
            PuzzleMaster.requiredPuzzles = _pendingPuzzleInit.RequiredPuzzles;
            
            Plugin.Log.LogInfo($"[Client] Applied puzzle init: completed={PuzzleMaster.totalCompletedPuzzles}, required={PuzzleMaster.requiredPuzzles}");
            
            // Clear pending init
            _pendingPuzzleInit = null;
        }
        
        // Exit door progress sync
        private void CheckExitDoorSync()
        {
            // Only host syncs exit door progress
            if (!_steam.IsHost) return;
            
            // Throttle sync
            if (Time.time - _lastDoorSyncTime < 0.2f) return;
            _lastDoorSyncTime = Time.time;
            
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
                    
                    // Only sync if changed significantly
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
        
        private void HandleExitDoorProgress(PacketReader reader)
        {
            int timer = reader.GetInt();
            int requiredTime = reader.GetInt();
            
            // Only clients apply this
            if (_steam.IsHost) return;
            
            // Find leave door if not cached
            if (_leaveDoor == null)
            {
                _leaveDoor = Object.FindObjectOfType<LeaveDoorControl>();
            }
            
            if (_leaveDoor != null)
            {
                // Set the timer via reflection
                var timerField = typeof(LeaveDoorControl).GetField("doorLeaveTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (timerField != null)
                {
                    timerField.SetValue(_leaveDoor, timer);
                }
                
                // Also set loadingBarLock to true if timer > 0
                var lockField = typeof(LeaveDoorControl).GetField("loadingBarLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (lockField != null)
                {
                    lockField.SetValue(_leaveDoor, timer > 0);
                }
                
                Plugin.Log.LogInfo($"[Client] Exit door progress: {timer}/{requiredTime}");
            }
        }
        
        public void SendPuzzleComplete(int puzzleID)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_PUZZLE_COMPLETE);
            _writer.Put(puzzleID);
            _writer.Put(PuzzleMaster.totalCompletedPuzzles);
            SendToAllPeers(true);
            Plugin.Log.LogInfo($"Sent puzzle complete: puzzleID={puzzleID}, total={PuzzleMaster.totalCompletedPuzzles}");
        }
        
        private void HandlePuzzleComplete(PacketReader reader)
        {
            int puzzleID = reader.GetInt();
            int totalCompleted = reader.GetInt();
            
            Plugin.Log.LogInfo($"Received puzzle complete: puzzleID={puzzleID}, total={totalCompleted}");
            
            // Update total completed
            PuzzleMaster.totalCompletedPuzzles = totalCompleted;
            
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
                        var completedField = pcType.GetField("puzzleHasCompleted", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        completedField.SetValue(controller, true);
                        
                        if (controller.fanspin != null)
                            controller.fanspin.isOn = true;
                        controller.thisMapIndicator?.SetActive(true);
                        
                        // Play win audio
                        controller.winAudio?.Play();
                        
                        Plugin.Log.LogInfo($"Marked puzzle {puzzleID} as complete");
                        break;
                    }
                }
            }
        }
        
        // Flag to prevent re-sending block changes we receive
        private bool _isReceivingPuzzleBlock = false;
        public bool IsReceivingPuzzleBlock => _isReceivingPuzzleBlock;
        
        public void SendPuzzleBlock(int puzzleID, int blockNumber, int blockIDValue)
        {
            if (_steam == null || !_steam.IsRunning) return;
            if (_isReceivingPuzzleBlock) return; // Don't re-send received changes
            
            _writer.Reset();
            _writer.Put(PACKET_PUZZLE_BLOCK);
            _writer.Put(puzzleID);
            _writer.Put(blockNumber);
            _writer.Put(blockIDValue);
            SendToAllPeers(true);
        }
        
        private void HandlePuzzleBlock(PacketReader reader)
        {
            int puzzleID = reader.GetInt();
            int blockNumber = reader.GetInt();
            int blockIDValue = reader.GetInt();
            
            // Find the puzzle controller with this ID
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
        
        private void HandleVentSound(PacketReader reader)
        {
            Vector3 position = ReadVector3(reader);
            int soundType = reader.GetInt();
            
            _isReceivingVentSound = true;
            
            if (soundType == 0)
            {
                // Vent sound - find ventSoundPlayer to get the sound prefab
                var ventPlayer = Object.FindObjectOfType<ventSoundPlayer>();
                if (ventPlayer != null && ventPlayer.soundPrefab != null)
                {
                    // Spawn the sound prefab at the received position
                    var spawnedSound = Object.Instantiate(ventPlayer.soundPrefab);
                    spawnedSound.transform.position = position;
                    Plugin.Log.LogInfo($"[VentSound] Played remote vent sound at {position}");
                }
            }
            else if (soundType == 1)
            {
                // Crawl sound - play one of the crawl sounds at the remote position
                var crawlSound = Object.FindObjectOfType<crawlSoundContrl>();
                if (crawlSound != null)
                {
                    // Pick a random crawl sound to play
                    AudioSource[] sources = { crawlSound.m1, crawlSound.m2, crawlSound.m3, crawlSound.m4 };
                    int idx = UnityEngine.Random.Range(0, sources.Length);
                    var source = sources[idx];
                    
                    if (source != null && source.clip != null)
                    {
                        // Play at the remote player's position with random pitch like the original
                        float pitch = UnityEngine.Random.Range(0.8f, 1.2f);
                        
                        // Create a temporary audio source at the position
                        var tempGO = new GameObject("RemoteCrawlSound");
                        tempGO.transform.position = position;
                        var tempAudio = tempGO.AddComponent<AudioSource>();
                        tempAudio.clip = source.clip;
                        tempAudio.pitch = pitch;
                        tempAudio.spatialBlend = 1f;  // 3D sound
                        tempAudio.volume = source.volume;
                        tempAudio.Play();
                        Object.Destroy(tempGO, source.clip.length + 0.1f);
                    }
                }
            }
            
            _isReceivingVentSound = false;
        }
        
        private void HandleFlashlightUpdate(int peerId, PacketReader reader)
        {
            bool isOn = reader.GetBool();
            Plugin.Log.LogInfo($"Received flashlight state from peer {peerId}: {isOn}");
            
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
            
            // Log every ~5 seconds
            if (_updateCount % 150 == 0)
            {
                Plugin.Log.LogInfo($"[Recv] peer {peerId}: head={headPos}, lHand={leftHandPos}, rHand={rightHandPos}");
            }
            
            remote.SetTargets(isStanding, bodyPos, bodyRot, headPos, headRot, 
                              leftHandPos, leftHandRot, rightHandPos, rightHandRot);
        }
        
        // ==================== DEATH / GHOST SYSTEM ====================
        
        // Capture spawn point when first entering a level (before any movement)
        public void CaptureSpawnPoint()
        {
            if (_spawnPointCaptured) return;
            
            // Try to find player position from various sources
            var backpack = Object.FindObjectOfType<BackpackControl>();
            if (backpack != null && backpack.cam != null)
            {
                _levelSpawnPoint = backpack.cam.transform.position;
                _levelSpawnRotation = backpack.cam.transform.rotation;
                _spawnPointCaptured = true;
                Plugin.Log.LogInfo($"[Ghost] Captured spawn point: {_levelSpawnPoint}");
            }
            else if (_mainCamera != null)
            {
                _levelSpawnPoint = _mainCamera.transform.position;
                _levelSpawnRotation = _mainCamera.transform.rotation;
                _spawnPointCaptured = true;
                Plugin.Log.LogInfo($"[Ghost] Captured spawn point from camera: {_levelSpawnPoint}");
            }
        }
        
        // Called when local player dies - becomes a ghost instead of going to Home
        public void OnLocalPlayerDeath(int deathType)
        {
            if (_steam == null || !_steam.IsRunning) return;
            if (_isLocalPlayerGhost) return; // Already a ghost
            
            Plugin.Log.LogInfo($"[Ghost] Local player died (type {deathType}), becoming ghost");
            _isLocalPlayerGhost = true;
            
            // Send death notification to other players
            SendDeathGhost(true, deathType);
            
            // Respawn at level start as ghost (handled by the patch that calls this)
        }
        
        // Check if ALL players (local + remote) are ghosts
        public bool AreAllPlayersGhosts()
        {
            // Local player must be a ghost
            if (!_isLocalPlayerGhost) return false;
            
            // All remote players must be ghosts too
            foreach (var remote in _remotePlayers.Values)
            {
                if (!remote.IsGhost) return false;
            }
            
            // If we have no remote players, just local being ghost is enough to "end"
            // But typically in multiplayer we'd have at least one remote player
            Plugin.Log.LogInfo($"[Ghost] All players check: local={_isLocalPlayerGhost}, remotes={_remotePlayers.Count} (all ghosts)");
            return true;
        }
        
        // Teleport player back to spawn point
        public void RespawnAtSpawnPoint()
        {
            if (!_spawnPointCaptured)
            {
                Plugin.Log.LogWarning("[Ghost] No spawn point captured, can't respawn");
                return;
            }
            
            Plugin.Log.LogInfo($"[Ghost] RespawnAtSpawnPoint called, target={_levelSpawnPoint}");
            
            // FIRST: Reset ALL velocity sources before teleporting
            // This is critical - player may have been falling during jumpscare
            
            // Reset GorillaLocomotion.Player velocity
            if (_gorillaPlayer != null)
            {
                // Reset velocity fields via reflection (they're private)
                var velField = typeof(Player).GetField("currentVelocity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (velField != null)
                {
                    velField.SetValue(_gorillaPlayer, Vector3.zero);
                    Plugin.Log.LogInfo("[Ghost] Reset GorillaLocomotion currentVelocity");
                }
                
                var bodyVelField = typeof(Player).GetField("bodyVelocity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (bodyVelField != null)
                {
                    bodyVelField.SetValue(_gorillaPlayer, Vector3.zero);
                    Plugin.Log.LogInfo("[Ghost] Reset GorillaLocomotion bodyVelocity");
                }
                
                // Also try public velocity if it exists
                var pubVelField = typeof(Player).GetField("velocity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (pubVelField != null)
                {
                    pubVelField.SetValue(_gorillaPlayer, Vector3.zero);
                }
            }
            
            // Find the player's transform to teleport
            var backpack = Object.FindObjectOfType<BackpackControl>();
            Transform playerRoot = null;
            
            if (backpack != null)
            {
                playerRoot = backpack.transform.root;
            }
            
            // Reset ALL Rigidbodies on the player
            if (playerRoot != null)
            {
                var rigidbodies = playerRoot.GetComponentsInChildren<Rigidbody>(true);
                foreach (var rb in rigidbodies)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    Plugin.Log.LogInfo($"[Ghost] Reset Rigidbody velocity on {rb.gameObject.name}");
                }
            }
            
            // Reset CharacterController and teleport
            if (playerRoot != null)
            {
                var charController = playerRoot.GetComponentInChildren<CharacterController>();
                if (charController != null)
                {
                    // Disable, move, then re-enable (standard Unity teleport pattern)
                    charController.enabled = false;
                    charController.transform.position = _levelSpawnPoint;
                    charController.enabled = true;
                    Plugin.Log.LogInfo($"[Ghost] Teleported via CharacterController to {_levelSpawnPoint}");
                }
                else
                {
                    playerRoot.position = _levelSpawnPoint;
                    Plugin.Log.LogInfo($"[Ghost] Teleported player root to {_levelSpawnPoint}");
                }
            }
            
            // Also try MoveTypeController which handles player movement
            var moveController = Object.FindObjectOfType<MoveTypeController>();
            if (moveController != null)
            {
                // Reset any velocity on MoveTypeController
                var rb = moveController.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                moveController.transform.position = _levelSpawnPoint;
                Plugin.Log.LogInfo($"[Ghost] Teleported MoveTypeController to {_levelSpawnPoint}");
            }
            
            // Final position verification
            if (backpack != null && backpack.cam != null)
            {
                Plugin.Log.LogInfo($"[Ghost] After teleport, cam position: {backpack.cam.transform.position}");
            }
        }
        
        // Send death/ghost state to other players
        public void SendDeathGhost(bool isGhost, int deathType)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_DEATH_GHOST);
            _writer.Put(isGhost);
            _writer.Put(deathType);
            SendToAllPeers(true);
            Plugin.Log.LogInfo($"[Ghost] Sent ghost state: isGhost={isGhost}, deathType={deathType}");
        }
        
        // Handle receiving death/ghost state from another player
        private void HandleDeathGhost(int peerId, PacketReader reader)
        {
            bool isGhost = reader.GetBool();
            int deathType = reader.GetInt();
            
            Plugin.Log.LogInfo($"[Ghost] Peer {peerId} ghost state: isGhost={isGhost}, deathType={deathType}");
            
            if (_remotePlayers.TryGetValue(peerId, out var remote))
            {
                remote.SetGhostState(isGhost);
            }
        }
        
        // Check if a position is a ghost player (for monster targeting)
        public bool IsPositionGhostPlayer(Vector3 position)
        {
            foreach (var remote in _remotePlayers.Values)
            {
                if (remote.IsGhost && remote.Head != null)
                {
                    float dist = Vector3.Distance(position, remote.Head.transform.position);
                    if (dist < 1f) return true;
                }
            }
            return false;
        }
        
        // Flag to allow scene load even when ghost (used when all players are ghosts)
        public static bool ForceSceneLoadAllowed = false;
        
        // Force load a scene, bypassing the ghost block
        public void ForceLoadScene(string sceneName)
        {
            Plugin.Log.LogInfo($"[Ghost] Force loading scene: {sceneName}");
            ForceSceneLoadAllowed = true;
            SceneManager.LoadScene(sceneName);
            ForceSceneLoadAllowed = false;
        }
        
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
    }
}
