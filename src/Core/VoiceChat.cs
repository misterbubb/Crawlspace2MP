using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace Crawlspace2MP
{
    /// <summary>
    /// Steam Voice chat with proximity-based 3D audio
    /// Voice comes from the remote player's position in the world
    /// 
    /// Key fixes from research:
    /// - Proper sample rate handling (Steam decompresses to OptimalSampleRate, Unity plays at outputSampleRate)
    /// - Fixed ring buffer with proper read/write tracking
    /// - Jitter buffer to handle network variance
    /// - Proper audio thread synchronization
    /// </summary>
    public class VoiceChat
    {
        public const byte PACKET_VOICE = 200;
        
        // Settings
        public bool Enabled { get; set; } = true;
        public bool PushToTalk { get; set; } = false;
        public float MaxDistance { get; set; } = 30f;
        public float MinDistance { get; set; } = 2f;
        
        // State
        private bool _isRecording = false;
        private INetworkTransport _steam;
        private Dictionary<int, VoicePlayer> _voicePlayers = new Dictionary<int, VoicePlayer>();
        
        // Steam voice sample rate (set after decompression)
        private int _steamSampleRate = 0;
        
        private PacketWriter _writer = new PacketWriter(1024 * 24);
        
        public void Initialize(INetworkTransport steam)
        {
            _steam = steam;
            Plugin.Log.LogInfo("[Voice] Voice chat initialized");
        }
        
        private void EnsureSteamReady()
        {
            if (_steamSampleRate == 0 && SteamClient.IsValid)
            {
                try
                {
                    _steamSampleRate = (int)SteamUser.OptimalSampleRate;
                    if (_steamSampleRate == 0) _steamSampleRate = 24000;
                    Plugin.Log.LogInfo($"[Voice] Steam sample rate: {_steamSampleRate}, Unity sample rate: {AudioSettings.outputSampleRate}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[Voice] Failed to get sample rate: {ex.Message}");
                    _steamSampleRate = 24000;
                }
            }
        }
        
        public void Update()
        {
            if (!Enabled || _steam == null || !_steam.IsConnected) return;
            
            EnsureSteamReady();
            if (_steamSampleRate == 0) return;
            
            UpdateRecording();
            UpdateVoicePlayerPositions();
        }
        
        private void UpdateRecording()
        {
            bool shouldRecord = !PushToTalk;
            
            // Start/stop recording
            if (shouldRecord && !_isRecording)
            {
                SteamUser.VoiceRecord = true;
                _isRecording = true;
                Plugin.Log.LogInfo("[Voice] Started recording");
            }
            else if (!shouldRecord && _isRecording)
            {
                SteamUser.VoiceRecord = false;
                _isRecording = false;
                Plugin.Log.LogInfo("[Voice] Stopped recording");
            }
            
            // Read and send voice data
            if (_isRecording && SteamUser.HasVoiceData)
            {
                byte[] compressedData = SteamUser.ReadVoiceDataBytes();
                if (compressedData != null && compressedData.Length > 0)
                {
                    SendVoiceData(compressedData);
                }
            }
        }
        
        private void SendVoiceData(byte[] compressedData)
        {
            if (_steam == null || !_steam.IsConnected) return;
            
            _writer.Reset();
            _writer.Put(PACKET_VOICE);
            _writer.Put(compressedData.Length);
            for (int i = 0; i < compressedData.Length; i++)
            {
                _writer.Put(compressedData[i]);
            }
            
            // Unreliable for lower latency
            _steam.SendToAll(_writer.GetBytes(), false);
        }
        
        public void OnVoiceDataReceived(int peerId, PacketReader reader)
        {
            if (!Enabled) return;
            
            EnsureSteamReady();
            
            int compressedLength = reader.GetInt();
            if (compressedLength <= 0 || compressedLength > 20000)
            {
                return;
            }
            
            byte[] compressedData = new byte[compressedLength];
            for (int i = 0; i < compressedLength; i++)
            {
                compressedData[i] = reader.GetByte();
            }
            
            // Decompress the voice data
            using (var outputStream = new System.IO.MemoryStream())
            {
                int bytesWritten = SteamUser.DecompressVoice(compressedData, outputStream);
                
                if (bytesWritten > 0)
                {
                    byte[] pcmData = outputStream.ToArray();
                    
                    // Get or create voice player
                    if (!_voicePlayers.TryGetValue(peerId, out var voicePlayer))
                    {
                        voicePlayer = CreateVoicePlayer(peerId);
                        _voicePlayers[peerId] = voicePlayer;
                    }
                    
                    if (voicePlayer != null)
                    {
                        voicePlayer.QueueAudio(pcmData, _steamSampleRate);
                    }
                }
            }
        }
        
        private VoicePlayer CreateVoicePlayer(int peerId)
        {
            var go = new GameObject($"VoicePlayer_{peerId}");
            UnityEngine.Object.DontDestroyOnLoad(go);
            
            var voicePlayer = go.AddComponent<VoicePlayer>();
            voicePlayer.PeerId = peerId;
            voicePlayer.MinDistance = MinDistance;
            voicePlayer.MaxDistance = MaxDistance;
            
            Plugin.Log.LogInfo($"[Voice] Created voice player for peer {peerId}");
            return voicePlayer;
        }
        
        private void UpdateVoicePlayerPositions()
        {
            var playerSync = MPManager.Instance?.PlayerSync;
            if (playerSync == null) return;
            
            List<int> toRemove = null;
            
            foreach (var kvp in _voicePlayers)
            {
                int peerId = kvp.Key;
                var voicePlayer = kvp.Value;
                
                if (voicePlayer == null || voicePlayer.gameObject == null)
                {
                    if (toRemove == null) toRemove = new List<int>();
                    toRemove.Add(peerId);
                    continue;
                }
                
                Vector3? remotePos = playerSync.GetRemotePlayerPosition(peerId);
                if (remotePos.HasValue)
                {
                    voicePlayer.transform.position = remotePos.Value;
                }
            }
            
            if (toRemove != null)
            {
                foreach (int peerId in toRemove)
                {
                    _voicePlayers.Remove(peerId);
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
            if (_isRecording)
            {
                SteamUser.VoiceRecord = false;
                _isRecording = false;
            }
            
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
    /// Plays voice audio at a 3D position with proper sample rate conversion
    /// and jitter buffering to handle network variance
    /// </summary>
    public class VoicePlayer : MonoBehaviour
    {
        public int PeerId { get; set; }
        public float MinDistance { get; set; } = 2f;
        public float MaxDistance { get; set; } = 30f;
        
        private AudioSource _audioSource;
        
        // Ring buffer for audio samples (stores floats ready for playback)
        private float[] _ringBuffer;
        private int _writePos = 0;
        private int _readPos = 0;
        private int _samplesAvailable = 0;
        private readonly object _bufferLock = new object();
        
        // Buffer size: 2 seconds at Unity's sample rate
        private int _bufferSize;
        
        // Sample rate conversion
        private int _inputSampleRate = 24000;
        private int _outputSampleRate;
        private float _resampleRatio;
        
        // Jitter buffer: don't start playing until we have enough data
        private const float JITTER_BUFFER_MS = 50f; // 50ms jitter buffer (lower latency)
        private int _jitterBufferSamples;
        private bool _isPlaying = false;
        
        // Resampling state (for linear interpolation)
        private float _resamplePos = 0f;
        
        private void Awake()
        {
            _outputSampleRate = AudioSettings.outputSampleRate;
            _bufferSize = _outputSampleRate * 2; // 2 seconds
            _ringBuffer = new float[_bufferSize];
            _jitterBufferSamples = (int)(_outputSampleRate * JITTER_BUFFER_MS / 1000f);
            
            // Create audio source for 3D positional audio
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            _audioSource.minDistance = MinDistance;
            _audioSource.maxDistance = MaxDistance;
            _audioSource.loop = true;
            _audioSource.playOnAwake = false;
            _audioSource.volume = 1f;
            _audioSource.dopplerLevel = 0f;
            _audioSource.priority = 0; // Highest priority for voice
            
            // Create streaming audio clip at Unity's output sample rate
            var clip = AudioClip.Create(
                $"VoiceStream_{PeerId}",
                _outputSampleRate, // 1 second of samples
                1, // Mono
                _outputSampleRate,
                true, // Streaming
                OnAudioRead
            );
            
            _audioSource.clip = clip;
            _audioSource.Play();
            
            Plugin.Log.LogInfo($"[VoicePlayer {PeerId}] Created with output rate {_outputSampleRate}, jitter buffer {_jitterBufferSamples} samples");
        }
        
        /// <summary>
        /// Queue PCM audio data from Steam Voice (16-bit signed, mono)
        /// </summary>
        public void QueueAudio(byte[] pcmData, int sampleRate)
        {
            if (pcmData == null || pcmData.Length < 2) return;
            
            _inputSampleRate = sampleRate;
            _resampleRatio = (float)_inputSampleRate / _outputSampleRate;
            
            int inputSamples = pcmData.Length / 2;
            
            // Convert 16-bit PCM to float and resample to output rate
            lock (_bufferLock)
            {
                for (int i = 0; i < inputSamples; i++)
                {
                    // Convert 16-bit signed PCM to float [-1, 1]
                    short sample = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
                    float floatSample = sample / 32768f;
                    
                    // Simple nearest-neighbor resampling for now
                    // (Linear interpolation happens in OnAudioRead)
                    _ringBuffer[_writePos] = floatSample;
                    _writePos = (_writePos + 1) % _bufferSize;
                    
                    if (_samplesAvailable < _bufferSize)
                        _samplesAvailable++;
                }
            }
        }
        
        /// <summary>
        /// Called by Unity's audio thread to fill the output buffer
        /// </summary>
        private void OnAudioRead(float[] data)
        {
            lock (_bufferLock)
            {
                // Check if we should start playing (jitter buffer filled)
                if (!_isPlaying)
                {
                    if (_samplesAvailable >= _jitterBufferSamples)
                    {
                        _isPlaying = true;
                    }
                    else
                    {
                        // Fill with silence while buffering
                        Array.Clear(data, 0, data.Length);
                        return;
                    }
                }
                
                // Check for buffer underrun
                if (_samplesAvailable < data.Length * _resampleRatio)
                {
                    // Underrun - output silence and reset
                    Array.Clear(data, 0, data.Length);
                    _isPlaying = false;
                    return;
                }
                
                // Read samples with resampling
                for (int i = 0; i < data.Length; i++)
                {
                    if (_samplesAvailable > 1)
                    {
                        // Linear interpolation between samples
                        int idx0 = _readPos;
                        int idx1 = (_readPos + 1) % _bufferSize;
                        float frac = _resamplePos - (int)_resamplePos;
                        
                        float sample0 = _ringBuffer[idx0];
                        float sample1 = _ringBuffer[idx1];
                        data[i] = sample0 + (sample1 - sample0) * frac;
                        
                        // Advance read position by resample ratio
                        _resamplePos += _resampleRatio;
                        
                        // Consume whole samples
                        while (_resamplePos >= 1f)
                        {
                            _resamplePos -= 1f;
                            _readPos = (_readPos + 1) % _bufferSize;
                            _samplesAvailable--;
                            if (_samplesAvailable <= 0)
                            {
                                _samplesAvailable = 0;
                                break;
                            }
                        }
                    }
                    else
                    {
                        data[i] = 0f;
                    }
                }
            }
        }
        
        private void Update()
        {
            if (_audioSource != null)
            {
                _audioSource.minDistance = MinDistance;
                _audioSource.maxDistance = MaxDistance;
            }
        }
        
        private void OnDestroy()
        {
            if (_audioSource != null)
            {
                _audioSource.Stop();
                if (_audioSource.clip != null)
                {
                    Destroy(_audioSource.clip);
                }
            }
        }
    }
}
