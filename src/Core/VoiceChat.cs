using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace Crawlspace2MP
{
    public class VoiceChat
    {
        public const byte PACKET_VOICE = 200;
        
        public bool Enabled { get; set; } = true;
        public bool PushToTalk { get; set; } = false;
        public float MaxDistance { get; set; } = 30f;
        public float MinDistance { get; set; } = 2f;
        
        private bool _isRecording = false;
        private INetworkTransport _steam;
        private Dictionary<int, VoicePlayer> _voicePlayers = new Dictionary<int, VoicePlayer>();
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
                    Plugin.Log.LogInfo($"[Voice] Steam rate={_steamSampleRate}, Unity rate={AudioSettings.outputSampleRate}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[Voice] Sample rate error: {ex.Message}");
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
            
            if (_isRecording && SteamUser.HasVoiceData)
            {
                byte[] compressed = SteamUser.ReadVoiceDataBytes();
                if (compressed != null && compressed.Length > 0)
                {
                    _writer.Reset();
                    _writer.Put(PACKET_VOICE);
                    _writer.Put(compressed.Length);
                    for (int i = 0; i < compressed.Length; i++)
                        _writer.Put(compressed[i]);
                    _steam.SendToAll(_writer.GetBytes(), false);
                }
            }
        }

        public void OnVoiceDataReceived(int peerId, PacketReader reader)
        {
            if (!Enabled) return;
            EnsureSteamReady();
            
            int len = reader.GetInt();
            if (len <= 0 || len > 20000) return;
            
            byte[] compressed = new byte[len];
            for (int i = 0; i < len; i++)
                compressed[i] = reader.GetByte();
            
            using (var ms = new System.IO.MemoryStream())
            {
                int written = SteamUser.DecompressVoice(compressed, ms);
                if (written > 0)
                {
                    byte[] pcm = ms.ToArray();
                    
                    if (!_voicePlayers.TryGetValue(peerId, out var vp))
                    {
                        vp = CreateVoicePlayer(peerId);
                        _voicePlayers[peerId] = vp;
                    }
                    vp?.QueueAudio(pcm, _steamSampleRate);
                }
            }
        }
        
        private VoicePlayer CreateVoicePlayer(int peerId)
        {
            var go = new GameObject($"VoicePlayer_{peerId}");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var vp = go.AddComponent<VoicePlayer>();
            vp.PeerId = peerId;
            vp.MinDistance = MinDistance;
            vp.MaxDistance = MaxDistance;
            Plugin.Log.LogInfo($"[Voice] Created voice player for peer {peerId}");
            return vp;
        }
        
        private void UpdateVoicePlayerPositions()
        {
            var ps = MPManager.Instance?.PlayerSync;
            if (ps == null) return;
            
            List<int> dead = null;
            foreach (var kvp in _voicePlayers)
            {
                if (kvp.Value == null || kvp.Value.gameObject == null)
                {
                    if (dead == null) dead = new List<int>();
                    dead.Add(kvp.Key);
                    continue;
                }
                Vector3? pos = ps.GetRemotePlayerPosition(kvp.Key);
                if (pos.HasValue) kvp.Value.transform.position = pos.Value;
            }
            if (dead != null)
                foreach (int id in dead) _voicePlayers.Remove(id);
        }
        
        public void OnPeerDisconnected(int peerId)
        {
            if (_voicePlayers.TryGetValue(peerId, out var vp))
            {
                if (vp != null && vp.gameObject != null)
                    UnityEngine.Object.Destroy(vp.gameObject);
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
            foreach (var vp in _voicePlayers.Values)
                if (vp != null && vp.gameObject != null)
                    UnityEngine.Object.Destroy(vp.gameObject);
            _voicePlayers.Clear();
        }
    }

    /// <summary>
    /// Plays voice audio at a 3D position with proper sample rate conversion.
    /// Uses a streaming ring buffer with resampling from Steam's rate to Unity's rate.
    /// </summary>
    public class VoicePlayer : MonoBehaviour
    {
        public int PeerId { get; set; }
        public float MinDistance { get; set; } = 2f;
        public float MaxDistance { get; set; } = 30f;
        
        private AudioSource _audioSource;
        
        // Ring buffer stores RESAMPLED floats ready for Unity playback
        private float[] _ringBuffer;
        private volatile int _writePos = 0;
        private volatile int _readPos = 0;
        private volatile int _samplesAvailable = 0;
        private readonly object _lock = new object();
        
        private int _bufferSize;
        private int _outputRate;
        
        // Resampling state
        private float _resampleStep;
        private float _resampleFrac = 0f;
        
        // Jitter buffer
        private const int JITTER_MS = 20; // 20ms — minimal buffering
        private int _jitterSamples;
        private bool _playing = false;
        private float _silenceTimer = 0f;
        
        private void Awake()
        {
            _outputRate = AudioSettings.outputSampleRate;
            _bufferSize = _outputRate * 2; // 2 seconds ring buffer
            _ringBuffer = new float[_bufferSize];
            _jitterSamples = _outputRate * JITTER_MS / 1000;
            
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            _audioSource.minDistance = MinDistance;
            _audioSource.maxDistance = MaxDistance;
            _audioSource.loop = true;
            _audioSource.playOnAwake = false;
            _audioSource.volume = 1f;
            _audioSource.dopplerLevel = 0f;
            _audioSource.priority = 0;
            
            // Shorter clip = less latency. 1024 samples per read chunk.
            var clip = AudioClip.Create(
                $"Voice_{PeerId}", 1024, 1, _outputRate, true, OnAudioRead);
            _audioSource.clip = clip;
            _audioSource.Play();
        }
        
        /// <summary>
        /// Queue PCM data from Steam (16-bit signed mono at Steam's sample rate).
        /// Resamples to Unity's output rate before storing in the ring buffer.
        /// </summary>
        public void QueueAudio(byte[] pcm, int inputRate)
        {
            if (pcm == null || pcm.Length < 2) return;
            
            _resampleStep = (float)inputRate / _outputRate;
            int inputSamples = pcm.Length / 2;
            
            // Pre-convert all input to float
            float[] input = new float[inputSamples];
            for (int i = 0; i < inputSamples; i++)
            {
                short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                input[i] = s / 32768f;
            }
            
            // Resample and write to ring buffer
            lock (_lock)
            {
                while (_resampleFrac < inputSamples - 1)
                {
                    int idx = (int)_resampleFrac;
                    float frac = _resampleFrac - idx;
                    
                    float sample;
                    if (idx + 1 < inputSamples)
                        sample = input[idx] * (1f - frac) + input[idx + 1] * frac;
                    else
                        sample = input[idx];
                    
                    _ringBuffer[_writePos] = sample;
                    _writePos = (_writePos + 1) % _bufferSize;
                    if (_samplesAvailable < _bufferSize)
                        _samplesAvailable++;
                    
                    _resampleFrac += _resampleStep;
                }
                // Keep fractional remainder for next chunk
                _resampleFrac -= (int)_resampleFrac;
                if (_resampleFrac < 0) _resampleFrac = 0;
            }
            
            _silenceTimer = 0f;
        }
        
        /// <summary>
        /// Unity audio thread callback — fills output buffer from ring buffer.
        /// </summary>
        private void OnAudioRead(float[] data)
        {
            lock (_lock)
            {
                if (!_playing)
                {
                    if (_samplesAvailable >= _jitterSamples)
                        _playing = true;
                    else
                    {
                        Array.Clear(data, 0, data.Length);
                        return;
                    }
                }
                
                for (int i = 0; i < data.Length; i++)
                {
                    if (_samplesAvailable > 0)
                    {
                        data[i] = _ringBuffer[_readPos];
                        _readPos = (_readPos + 1) % _bufferSize;
                        _samplesAvailable--;
                    }
                    else
                    {
                        data[i] = 0f;
                        _playing = false;
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
            
            // Track silence — if no data for 1 second, reset buffer to avoid stale audio
            _silenceTimer += Time.deltaTime;
            if (_silenceTimer > 1f)
            {
                lock (_lock)
                {
                    _samplesAvailable = 0;
                    _readPos = _writePos;
                    _playing = false;
                }
            }
        }
        
        private void OnDestroy()
        {
            if (_audioSource != null)
            {
                _audioSource.Stop();
                if (_audioSource.clip != null) Destroy(_audioSource.clip);
            }
        }
    }
}
