using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace Crawlspace2MP
{
    /// <summary>
    /// Handles spectating dead players via low-res camera stream on the Home TV
    /// Uses JPEG compression for efficient bandwidth
    /// </summary>
    public class SpectateSystem
    {
        public const byte PACKET_SPECTATE_FRAME = 30;
        public const byte PACKET_SPECTATE_START = 31;
        public const byte PACKET_SPECTATE_STOP = 32;
        
        // Capture settings - higher res now that we have JPEG compression
        private const int CAPTURE_WIDTH = 320;
        private const int CAPTURE_HEIGHT = 180;
        private const float CAPTURE_INTERVAL = 0.25f; // 4 FPS
        private const int JPEG_QUALITY = 60; // 0-100, lower = smaller file
        
        // Sender state (alive player in Night level)
        private RenderTexture _captureRT;
        private Texture2D _captureTex;
        private Camera _captureCamera;
        private float _lastCaptureTime;
        private bool _isSending = false;
        
        // Receiver state (dead player in Home)
        private Texture2D _receiveTex;
        private MoviePlayerSample _tvPlayer;
        private Renderer _tvRenderer;
        private bool _isReceiving = false;
        private bool _tvWasPlaying = false;
        
        private SteamTransport _steam;
        private PacketWriter _writer = new PacketWriter(1024 * 64); // 64KB buffer
        
        public bool IsSending => _isSending;
        public bool IsReceiving => _isReceiving;
        
        public void Initialize(SteamTransport steam)
        {
            _steam = steam;
            
            // Create capture resources
            _captureRT = new RenderTexture(CAPTURE_WIDTH, CAPTURE_HEIGHT, 16);
            _captureRT.Create();
            
            _captureTex = new Texture2D(CAPTURE_WIDTH, CAPTURE_HEIGHT, TextureFormat.RGB24, false);
            
            // Create receive texture (will be resized on first frame)
            _receiveTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            
            Plugin.Log.LogInfo($"SpectateSystem initialized ({CAPTURE_WIDTH}x{CAPTURE_HEIGHT} @ {1f/CAPTURE_INTERVAL:F0} FPS, JPEG Q{JPEG_QUALITY})");
        }
        
        public void Update()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            if (_isSending)
            {
                UpdateSender();
            }
        }
        
        /// <summary>
        /// Start sending spectate frames (called when partner dies and we're still alive)
        /// </summary>
        public void StartSending()
        {
            if (_isSending) return;
            
            _captureCamera = Camera.main;
            if (_captureCamera == null)
            {
                _captureCamera = UnityEngine.Object.FindObjectOfType<Camera>();
            }
            
            if (_captureCamera == null)
            {
                Plugin.Log.LogWarning("[Spectate] No camera found, can't send frames");
                return;
            }
            
            _isSending = true;
            _lastCaptureTime = 0;
            
            SendSpectateStart();
            Plugin.Log.LogInfo("[Spectate] Started sending frames");
        }
        
        /// <summary>
        /// Stop sending spectate frames
        /// </summary>
        public void StopSending()
        {
            if (!_isSending) return;
            
            _isSending = false;
            _captureCamera = null;
            
            SendSpectateStop();
            Plugin.Log.LogInfo("[Spectate] Stopped sending frames");
        }
        
        /// <summary>
        /// Start receiving spectate frames (called when we die and partner is alive)
        /// </summary>
        public void StartReceiving()
        {
            if (_isReceiving) return;
            
            _tvPlayer = UnityEngine.Object.FindObjectOfType<MoviePlayerSample>();
            if (_tvPlayer == null)
            {
                Plugin.Log.LogWarning("[Spectate] No MoviePlayerSample found in scene");
                return;
            }
            
            _tvRenderer = _tvPlayer.GetComponent<Renderer>();
            if (_tvRenderer == null)
            {
                Plugin.Log.LogWarning("[Spectate] No Renderer on TV");
                return;
            }
            
            // Stop the video player
            _tvWasPlaying = _tvPlayer.IsPlaying;
            _tvPlayer.Stop();
            
            // Set our receive texture as the TV display
            if (_tvRenderer.material != null)
            {
                _tvRenderer.material.mainTexture = _receiveTex;
            }
            
            _isReceiving = true;
            Plugin.Log.LogInfo("[Spectate] Started receiving frames on TV");
        }
        
        /// <summary>
        /// Stop receiving spectate frames and restore TV
        /// </summary>
        public void StopReceiving()
        {
            if (!_isReceiving) return;
            
            _isReceiving = false;
            
            if (_tvPlayer != null && _tvWasPlaying)
            {
                _tvPlayer.Play();
            }
            
            _tvPlayer = null;
            _tvRenderer = null;
            
            Plugin.Log.LogInfo("[Spectate] Stopped receiving, restored TV");
        }
        
        private void UpdateSender()
        {
            if (_captureCamera == null || !_isSending) return;
            
            // Rate limit captures
            if (Time.realtimeSinceStartup - _lastCaptureTime < CAPTURE_INTERVAL) return;
            _lastCaptureTime = Time.realtimeSinceStartup;
            
            // Capture camera view to RenderTexture
            RenderTexture prevRT = _captureCamera.targetTexture;
            _captureCamera.targetTexture = _captureRT;
            _captureCamera.Render();
            _captureCamera.targetTexture = prevRT;
            
            // Read pixels from RenderTexture
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = _captureRT;
            _captureTex.ReadPixels(new Rect(0, 0, CAPTURE_WIDTH, CAPTURE_HEIGHT), 0, 0);
            _captureTex.Apply();
            RenderTexture.active = prevActive;
            
            // Encode to JPEG
            byte[] jpegData = _captureTex.EncodeToJPG(JPEG_QUALITY);
            
            if (jpegData != null && jpegData.Length > 0)
            {
                SendFrame(jpegData);
            }
        }
        
        private void SendFrame(byte[] jpegData)
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_SPECTATE_FRAME);
            _writer.Put(jpegData.Length);
            
            for (int i = 0; i < jpegData.Length; i++)
            {
                _writer.Put(jpegData[i]);
            }
            
            // Send unreliable for lower latency
            _steam.SendToAll(_writer.GetBytes(), false);
        }
        
        private void SendSpectateStart()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_SPECTATE_START);
            _steam.SendToAll(_writer.GetBytes(), true);
        }
        
        private void SendSpectateStop()
        {
            if (_steam == null || !_steam.IsRunning) return;
            
            _writer.Reset();
            _writer.Put(PACKET_SPECTATE_STOP);
            _steam.SendToAll(_writer.GetBytes(), true);
        }
        
        /// <summary>
        /// Handle incoming spectate packets
        /// </summary>
        public void OnPacketReceived(byte packetType, PacketReader reader)
        {
            switch (packetType)
            {
                case PACKET_SPECTATE_FRAME:
                    HandleFrame(reader);
                    break;
                case PACKET_SPECTATE_START:
                    HandleSpectateStart();
                    break;
                case PACKET_SPECTATE_STOP:
                    HandleSpectateStop();
                    break;
            }
        }
        
        private void HandleFrame(PacketReader reader)
        {
            if (!_isReceiving) return;
            
            int length = reader.GetInt();
            if (length <= 0 || length > 1024 * 100) return; // Max 100KB
            
            byte[] jpegData = new byte[length];
            for (int i = 0; i < length; i++)
            {
                jpegData[i] = reader.GetByte();
            }
            
            // Decode JPEG - LoadImage auto-resizes the texture
            _receiveTex.LoadImage(jpegData);
            
            // Update TV material if needed
            if (_tvRenderer != null && _tvRenderer.material != null)
            {
                _tvRenderer.material.mainTexture = _receiveTex;
            }
        }
        
        private void HandleSpectateStart()
        {
            Plugin.Log.LogInfo("[Spectate] Partner started spectate stream");
            
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene.Equals("Home", StringComparison.OrdinalIgnoreCase))
            {
                StartReceiving();
            }
        }
        
        private void HandleSpectateStop()
        {
            Plugin.Log.LogInfo("[Spectate] Partner stopped spectate stream");
            StopReceiving();
        }
        
        public void Cleanup()
        {
            StopSending();
            StopReceiving();
            
            if (_captureRT != null)
            {
                _captureRT.Release();
                UnityEngine.Object.Destroy(_captureRT);
                _captureRT = null;
            }
            
            if (_captureTex != null)
            {
                UnityEngine.Object.Destroy(_captureTex);
                _captureTex = null;
            }
            
            if (_receiveTex != null)
            {
                UnityEngine.Object.Destroy(_receiveTex);
                _receiveTex = null;
            }
            
            Plugin.Log.LogInfo("[Spectate] Cleaned up");
        }
    }
}
