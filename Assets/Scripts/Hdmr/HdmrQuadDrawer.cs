using System;
using UnityEngine;
using UnityEngine.Rendering;
using Serenegiant.UVC;

namespace Hdmr
{
    /// <summary>
    /// IUVCDrawer for HDMR: video Texture -> quad, optional UAC audio -> AudioSource,
    /// plus a cheap non-blocking frame-liveness / fps estimate via AsyncGPUReadback.
    /// </summary>
    public class HdmrQuadDrawer : MonoBehaviour, IUVCDrawer
    {
        public Renderer quad;
        public AudioSource audioSource;

        public bool Streaming { get; private set; }
        public bool FramesLive { get; private set; }
        public float Fps { get; private set; }
        public Texture CurrentTex { get; private set; }
        public string LastEvent { get; private set; } = "(none)";
        public bool AudioPlaying => audioSource != null && audioSource.isPlaying;

        public bool QuadVisible
        {
            get => quad != null && quad.enabled;
            set { if (quad != null) quad.enabled = value; }
        }

        public float AppFps { get; private set; }
        public float FrameIntervalMs { get; private set; }   // measured video frame-to-frame

        bool _flipX, _flipY;
        RenderTexture _rt;
        int _lastHash;
        float _lastChangeT, _fpsWindowStart;
        int _frameCount;
        bool _readbackPending;
        float _lastSampleT;
        double _lastDistinctFrameTime;
        float _appFpsAccum; int _appFpsFrames;
        const int SW = 32, SH = 18;

        void Awake()
        {
            _rt = new RenderTexture(SW, SH, 0, RenderTextureFormat.ARGB32) { name = "HdmrLiveness" };
            if (quad != null) quad.enabled = false;
        }

        void OnDestroy() { if (_rt) _rt.Release(); }

        public void SetFlip(bool x, bool y)
        {
            _flipX = x; _flipY = y;
            ApplyFlip();
        }

        void ApplyFlip()
        {
            if (quad == null || CurrentTex == null) return;
            var m = quad.material;
            m.mainTextureScale = new Vector2(_flipX ? -1f : 1f, _flipY ? -1f : 1f);
            m.mainTextureOffset = new Vector2(_flipX ? 1f : 0f, _flipY ? 1f : 0f);
        }

        // ---- IUVCDrawer -----------------------------------------------------
        public bool OnUVCAttachEvent(UVCManager m, UVCDevice d)
        {
            LastEvent = $"attach {d.vid:X4}:{d.pid:X4} {d.name}";
            Debug.Log("[HDMR] " + LastEvent);
            return true;
        }

        public void OnUVCDetachEvent(UVCManager m, UVCDevice d)
        {
            LastEvent = "detach";
            Debug.Log("[HDMR] " + LastEvent);
            Streaming = false; FramesLive = false; Fps = 0; CurrentTex = null;
            if (quad != null) { quad.material.mainTexture = null; quad.enabled = false; }
        }

        public bool IsUVCEnabled(UVCManager m, UVCDevice d) => true;

        public void OnUVCStartEvent(UVCManager m, UVCDevice d, Texture tex)
        {
            CurrentTex = tex;
            Streaming = true;
            LastEvent = $"start preview {tex.width}x{tex.height}";
            Debug.Log("[HDMR] " + LastEvent);
            if (quad != null) quad.material.mainTexture = tex;
            ApplyFlip();
            _frameCount = 0; _fpsWindowStart = Time.time; _lastChangeT = Time.time; _lastHash = 0;
        }

        public void OnUVCStopEvent(UVCManager m, UVCDevice d)
        {
            LastEvent = "stop preview";
            Debug.Log("[HDMR] " + LastEvent);
            Streaming = false; FramesLive = false; Fps = 0;
        }

        public bool IsUACEnabled(UVCManager m, UVCDevice d) => HdmrSession.WantSound && d.isUAC;

        public void OnUACStartEvent(UVCManager m, UVCDevice d, AudioClip clip)
        {
            LastEvent = "UAC start";
            Debug.Log("[HDMR] " + LastEvent + " " + (clip != null ? $"{clip.channels}ch {clip.frequency}Hz" : "null"));
            if (audioSource != null && clip != null)
            {
                audioSource.Stop();
                audioSource.clip = clip;
                audioSource.loop = true;
                audioSource.spatialBlend = 0f;
                audioSource.Play();
            }
        }

        public void OnUACStopEvent(UVCManager m, UVCDevice d)
        {
            Debug.Log("[HDMR] UAC stop");
            if (audioSource != null) { audioSource.Stop(); audioSource.clip = null; }
        }

        // ---- non-blocking liveness + fps --------------------------------
        void Update()
        {
            // app render fps (smoothed over 0.5s)
            _appFpsAccum += Time.unscaledDeltaTime; _appFpsFrames++;
            if (_appFpsAccum >= 0.5f) { AppFps = _appFpsFrames / _appFpsAccum; _appFpsAccum = 0; _appFpsFrames = 0; }

            if (!Streaming || CurrentTex == null) { FramesLive = false; return; }
            if (_readbackPending || Time.unscaledTime - _lastSampleT < 0.05f) return;
            _lastSampleT = Time.unscaledTime;

            // sample the video texture ~20x/s (AsyncGPUReadback is non-blocking)
            try
            {
                Graphics.Blit(CurrentTex, _rt);
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    _readbackPending = true;
                    AsyncGPUReadback.Request(_rt, 0, TextureFormat.RGBA32, OnReadback);
                }
            }
            catch (Exception e) { Debug.LogWarning("[HDMR] liveness: " + e.Message); }
        }

        void OnReadback(AsyncGPUReadbackRequest req)
        {
            _readbackPending = false;
            if (req.hasError) return;
            Consume(req.GetData<byte>().ToArray());
        }

        void Consume(byte[] raw)
        {
            int h = 17;
            for (int i = 0; i < raw.Length; i += 4) h = h * 31 + raw[i] + raw[i + 1] * 7;
            double now = Time.timeAsDouble;
            if (h != _lastHash)
            {
                _lastHash = h;
                _lastChangeT = Time.time;
                _frameCount++;
                if (_lastDistinctFrameTime > 0)
                {
                    float dtMs = (float)((now - _lastDistinctFrameTime) * 1000.0);
                    FrameIntervalMs = Mathf.Lerp(FrameIntervalMs <= 0 ? dtMs : FrameIntervalMs, dtMs, 0.15f);
                }
                _lastDistinctFrameTime = now;
            }
            float win = Time.time - _fpsWindowStart;
            if (win >= 1f) { Fps = _frameCount / win; _frameCount = 0; _fpsWindowStart = Time.time; }
            FramesLive = (Time.time - _lastChangeT) < 1.0f;
        }
    }
}
