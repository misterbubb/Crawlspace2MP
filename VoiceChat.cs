using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace Crawlspace2MP
{
    /// <summary>
    /// Steam Voice chat with proximity-based 3D audio
    /// Voice comes from the remote player's position in the world
    /// </summary>
    public class VoiceChat
    {
        // Voice packet type (high number to avoid collision with game packets)
        public const byte PACKET_VOICE = 200;
        
        // Settings
        public bool Enabled { get; set; } = true;
        public bool PushToTalk { get; set; } = false;  // If false, always transmit
        public float MaxDistance { get; set; } = 30f;  // Max distance to hear voice
        public float MinDistance { get; set; } = 2f;   // Distance at which voice is full volume
        
        // State
        private bool _isRecording = false;
        private SteamTransport _steam;
        private Dictionary<int, VoicePlayer> _voicePlayers = new Dictionary<int, VoicePlayer>();
        private byte[] _voiceBuffer = new byte[1024 * 20];  // 20KB buffer for compressed voice
        
        // Audio settings (Steam voice is 11025 Hz mono by default, but we request optimal)
        private int _optimalSampleRate;
        
        private PacketWriter _writer = new PacketWriter(1024 * 24);
        
        public void Initialize(SteamTransport steam)
        {
            _steam = steam;
            
            // Don't get sample rate yet - Steam might not be initialized
            // We'll get it lazily when we first need it
            _optimalSampleRate = 0;
            
            Plugin.Log.LogInfo("Voice chat created (will initialize when Steam is ready)");
        }
        
        private void EnsureInitialized()
        {
            if (_optimalSampleRate == 0 && SteamClient.IsValid)
            {
                try
                {
                    _optimalSampleRate = (int)SteamUser.OptimalSampleRate;
                    if (_optimalSampleRate == 0) _optimalSampleRate = 24000;  // Fallback
                    Plugin.Log.LogInfo($"Voice chat initialized. Sample rate: {_optimalSampleRate}");
                }
                catch
                {
                    _optimalSampleRate = 24000;  // Fallback on error
                }
            }
        }
        
        public void Update()
        {
            if (!Enabled || _steam == null || !_steam.IsConnected) return;
            
            // Lazy initialization when Steam is ready
            EnsureInitialized();
            if (_optimalSampleRate == 0) return;  // Still not ready
            
            // Handle recording
            UpdateRecording();
            
            // Update voice player positions
            UpdateVoicePlayerPositions();
        }
        
        private void UpdateRecording()
        {
            bool shouldRecord = !PushToTalk;  // Always record if not push-to-talk
            
            // TODO: Add push-to-talk key check here if PushToTalk is true
            // For now, always transmit when enabled
            
            if (shouldRecord && !_isRecording)
            {
                SteamUser.VoiceRecord = true;
                _isRecording = true;
            }
            else if (!shouldRecord && _isRecording)
            {
                SteamUser.VoiceRecord = false;
                _isRecording = false;
            }
            
            // Check for available voice data
            if (_isRecording)
            {
                // Read voice data using Facepunch API
                byte[] voiceData = SteamUser.ReadVoiceDataBytes();
                
                if (voiceData != null && voiceData.Length > 0)
                {
                    SendVoiceData(voiceData, voiceData.Length);
                }
            }
        }
        
        private void SendVoiceData(byte[] data, int length)
        {
            if (_steam == null || !_steam.IsConnected) return;
            
            _writer.Reset();
            _writer.Put(PACKET_VOICE);
            _writer.Put(length);
            
            // Write raw bytes
            for (int i = 0; i < length; i++)
            {
                _writer.Put(data[i]);
            }
            
            // Send unreliable for lower latency (voice can tolerate some loss)
            _steam.SendToAll(_writer.GetBytes(), false);
        }
        
        /// <summary>
        /// Called when voice data is received from a peer
        /// </summary>
        public void OnVoiceDataReceived(int peerId, PacketReader reader)
        {
            if (!Enabled) return;
            
            int compressedLength = reader.GetInt();
            if (compressedLength <= 0 || compressedLength > _voiceBuffer.Length) return;
            
            // Read compressed data
            for (int i = 0; i < compressedLength; i++)
            {
                _voiceBuffer[i] = reader.GetByte();
            }
            
            // Create a byte array of the exact size
            byte[] compressedData = new byte[compressedLength];
            Array.Copy(_voiceBuffer, compressedData, compressedLength);
            
            // Decompress using MemoryStream
            using (var outputStream = new System.IO.MemoryStream())
            {
                int bytesWritten = SteamUser.DecompressVoice(compressedData, outputStream);
                
                if (bytesWritten > 0)
                {
                    byte[] decompressedData = outputStream.ToArray();
                    
                    // Get or create voice player for this peer
                    if (!_voicePlayers.TryGetValue(peerId, out var voicePlayer))
                    {
                        voicePlayer = CreateVoicePlayer(peerId);
                        _voicePlayers[peerId] = voicePlayer;
                    }
                    
                    // Queue the audio data
                    voicePlayer.QueueAudio(decompressedData, decompressedData.Length, _optimalSampleRate);
                }
            }
        }
        
        private VoicePlayer CreateVoicePlayer(int peerId)
        {
            var go = new GameObject($"VoicePlayer_{peerId}");
            var voicePlayer = go.AddComponent<VoicePlayer>();
            voicePlayer.PeerId = peerId;
            voicePlayer.MinDistance = MinDistance;
            voicePlayer.MaxDistance = MaxDistance;
            
            Plugin.Log.LogInfo($"Created voice player for peer {peerId}");
            return voicePlayer;
        }
        
        private void UpdateVoicePlayerPositions()
        {
            // Update voice player positions to match remote player positions
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync == null) return;
            
            foreach (var kvp in _voicePlayers)
            {
                int peerId = kvp.Key;
                var voicePlayer = kvp.Value;
                
                // Get remote player position
                Vector3? remotePos = playerSync.GetRemotePlayerPosition(peerId);
                if (remotePos.HasValue)
                {
                    voicePlayer.transform.position = remotePos.Value;
                }
            }
        }
        
        public void OnPeerDisconnected(int peerId)
        {
            if (_voicePlayers.TryGetValue(peerId, out var voicePlayer))
            {
                if (voicePlayer != null && voicePlayer.gameObject != null)
                {
                    UnityEngine.Object.Destroy(voicePlayer.gameObject);
                }
                _voicePlayers.Remove(peerId);
            }
        }
        
        public void Cleanup()
        {
            // Stop recording
            if (_isRecording)
            {
                SteamUser.VoiceRecord = false;
                _isRecording = false;
            }
            
            // Destroy all voice players
            foreach (var voicePlayer in _voicePlayers.Values)
            {
                if (voicePlayer != null && voicePlayer.gameObject != null)
                {
                    UnityEngine.Object.Destroy(voicePlayer.gameObject);
                }
            }
            _voicePlayers.Clear();
        }
    }
    
    /// <summary>
    /// Component that plays voice audio at a 3D position
    /// </summary>
    public class VoicePlayer : MonoBehaviour
    {
        public int PeerId { get; set; }
        public float MinDistance { get; set; } = 2f;
        public float MaxDistance { get; set; } = 30f;
        
        private AudioSource _audioSource;
        private AudioClip _streamingClip;
        private float[] _audioBuffer;
        private int _writePosition = 0;
        private int _sampleRate = 24000;
        private const int BUFFER_SECONDS = 2;  // 2 second circular buffer
        
        private void Awake()
        {
            // Create audio source for 3D positional audio
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;  // Full 3D
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            _audioSource.minDistance = MinDistance;
            _audioSource.maxDistance = MaxDistance;
            _audioSource.loop = true;
            _audioSource.playOnAwake = false;
            _audioSource.volume = 1f;
            _audioSource.dopplerLevel = 0f;  // Disable doppler for voice
        }
        
        public void QueueAudio(byte[] pcmData, int length, int sampleRate)
        {
            _sampleRate = sampleRate;
            
            // Initialize buffer and clip if needed
            if (_audioBuffer == null || _streamingClip == null || _streamingClip.frequency != sampleRate)
            {
                InitializeAudioBuffer(sampleRate);
            }
            
            // Convert bytes to float samples (16-bit PCM)
            int sampleCount = length / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
                float normalizedSample = sample / 32768f;
                
                _audioBuffer[_writePosition] = normalizedSample;
                _writePosition = (_writePosition + 1) % _audioBuffer.Length;
            }
            
            // Update the clip data
            _streamingClip.SetData(_audioBuffer, 0);
            
            // Start playing if not already
            if (!_audioSource.isPlaying)
            {
                _audioSource.Play();
            }
        }
        
        private void InitializeAudioBuffer(int sampleRate)
        {
            int bufferSize = sampleRate * BUFFER_SECONDS;
            _audioBuffer = new float[bufferSize];
            
            // Create streaming audio clip
            _streamingClip = AudioClip.Create("VoiceStream", bufferSize, 1, sampleRate, false);
            _audioSource.clip = _streamingClip;
            _writePosition = 0;
            
            Plugin.Log.LogInfo($"Voice buffer initialized: {sampleRate}Hz, {bufferSize} samples");
        }
        
        private void Update()
        {
            // Update 3D audio settings
            _audioSource.minDistance = MinDistance;
            _audioSource.maxDistance = MaxDistance;
        }
        
        private void OnDestroy()
        {
            if (_streamingClip != null)
            {
                Destroy(_streamingClip);
            }
        }
    }
}
