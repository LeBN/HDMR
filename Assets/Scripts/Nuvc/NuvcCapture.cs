using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// Drives the from-scratch nuvc plugin: open the capture card by fd, bulk-stream,
    /// pull the newest frame each Unity frame, upload NV12 as two planes and convert
    /// to RGB via the Hdmr/NV12 shader. No aandusb, no decode for raw modes.
    /// </summary>
    public sealed class NuvcCapture : MonoBehaviour
    {
        public RenderTexture Output { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Fmt { get; private set; }          // 0 NV12, 1 YUY2, 2 MJPEG
        public int LinkSpeed { get; private set; }
        public float VideoFps { get; private set; }
        public long Seq { get; private set; }
        public float DisplayedFps { get; private set; }
        public string Status { get; private set; } = "starting…";
        public bool Streaming { get; private set; }

        // latency: EOF of the newest frame -> the moment Unity grabbed it (smoothed + 1s max)
        public float CapToPullMs { get; private set; }
        public float CapToPullMaxMs { get; private set; }
        public float AsmMs { get; private set; }        // USB receive span of a frame

        [NonSerialized] public bool WantAudio = true;
        public string AudioStatus { get; private set; } = "off";

        // NV12 rows are top-down; GL texture sampling is bottom-up -> flip Y by default.
        [NonSerialized] public bool FlipX = false, FlipY = true, FullRange = false;

        AndroidJavaObject _nuvc;
        AndroidJavaObject _audio;
        AndroidJavaObject _activity;
        bool _audioStarted;
        float _audioT;
        Material _mat;
        Texture2D _texY, _texUV;
        byte[] _buf;
        GCHandle _pin;
        IntPtr _base;
        long _lastSeq;
        int _newFrames;
        float _fpsT0, _retryT;
        bool _startCalled;
        float _latMaxAccum, _latMaxT;

        void Awake()
        {
            _mat = new Material(Shader.Find("Hdmr/NV12"));
            _buf = new byte[3840 * 2160 * 2];
            _pin = GCHandle.Alloc(_buf, GCHandleType.Pinned);
            _base = _pin.AddrOfPinnedObject();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _activity = up.GetStatic<AndroidJavaObject>("currentActivity");
                _nuvc = new AndroidJavaObject("com.natis.nuvc.Nuvc");
                _audio = new AndroidJavaObject("com.natis.nuvc.UsbAudio");
            }
            catch (Exception e) { Status = "bridge init failed: " + e.Message; Debug.LogError("[nuvc] " + e); }
#else
            Status = "editor — no device";
#endif
        }

        void OnDestroy()
        {
            try { _audio?.Call("stop"); } catch { }
            try { _nuvc?.Call("stop"); _nuvc?.Call("close"); } catch { }
            if (_pin.IsAllocated) _pin.Free();
            if (Output) Output.Release();
        }

        // DIAG (temporary): hold video off for a few seconds on launch so audio
        // gets a clean shot at the USB with zero video bulk traffic — tells us
        // whether the PAL isochronous-read failures are caused by bus
        // contention with our video ring, or happen regardless.
        float _diagAudioOnlyUntil;

        void Update()
        {
            if (_nuvc == null) return;

            if (_diagAudioOnlyUntil == 0f) _diagAudioOnlyUntil = Time.unscaledTime + 10f;
            if (Time.unscaledTime < _diagAudioOnlyUntil)
            {
                ServiceAudio();
                Status = $"DIAG audio-only window {(_diagAudioOnlyUntil - Time.unscaledTime):0}s…";
                return;
            }

            if (!Streaming)
            {
                if (Time.unscaledTime - _retryT < 0.5f) return;
                _retryT = Time.unscaledTime;
                try
                {
                    bool open = _nuvc.Call<bool>("ensureOpen", _activity);
                    if (!open)
                    {
                        Status = _nuvc.Call<bool>("permissionDenied")
                            ? "USB permission denied — replug the card"
                            : "waiting for card / USB permission…";
                        return;
                    }
                    try { _nuvc.Call("dumpAll"); } catch { }   // DIAG: descriptor dump for UAC audio planning
                    if (!_startCalled) _startCalled = _nuvc.Call<bool>("start");
                    if (!_startCalled) { Status = "start failed — see logcat"; return; }
                    Streaming = true;
                    _fpsT0 = Time.unscaledTime;
                }
                catch (Exception e) { Status = "open/start error: " + e.Message; Debug.LogError("[nuvc] " + e); return; }
            }

            PullAndUpload();
            ServiceAudio();
        }

        void ServiceAudio()
        {
            if (_audio == null) return;
            try
            {
                if (WantAudio && !_audioStarted && Time.unscaledTime - _audioT > 2f)
                {
                    _audioT = Time.unscaledTime;
                    _audioStarted = _audio.Call<bool>("start", _activity);
                }
                else if (!WantAudio && _audioStarted)
                {
                    _audio.Call("stop");
                    _audioStarted = false;
                }
                AudioStatus = WantAudio ? _audio.Get<string>("status") : "off";
            }
            catch (Exception e) { AudioStatus = "err: " + e.Message; }
        }

        void PullAndUpload()
        {
            long seq;
            try { seq = _nuvc.Call<long>("latestIntoAddr", (long)_base, (long)_buf.Length, _lastSeq); }
            catch (Exception e) { Status = "pull error: " + e.Message; return; }

            if ((Time.frameCount & 7) == 0)
            {
                try
                {
                    int[] s = _nuvc.Call<int[]>("stats");
                    if (s != null && s.Length >= 9)
                    {
                        LinkSpeed = s[7]; Fmt = s[8]; VideoFps = s[3] / 100f;
                        EnsureTextures(s[4], s[5]);
                    }
                }
                catch { }
            }

            if (seq == 0 || seq == _lastSeq || _texY == null) { Tick(false); return; }
            _lastSeq = seq; Seq = seq;

            int yBytes = Width * Height;
            int uvBytes = yBytes / 2;
            if (Fmt == 0 && _buf.Length >= yBytes + uvBytes)
            {
                _texY.LoadRawTextureData(_base, yBytes);
                _texY.Apply(false);
                _texUV.LoadRawTextureData(IntPtr.Add(_base, yBytes), uvBytes);
                _texUV.Apply(false);

                _mat.SetFloat("_FullRange", FullRange ? 1f : 0f);
                _mat.SetFloat("_FlipX", FlipX ? 1f : 0f);
                _mat.SetFloat("_FlipY", FlipY ? 1f : 0f);
                Graphics.Blit(_texY, Output, _mat);

                try
                {
                    long[] l = _nuvc.Call<long[]>("lat");
                    if (l != null && l.Length == 3 && l[1] != 0)
                    {
                        float pullMs = (l[0] - l[1]) / 1e6f;
                        if (pullMs >= 0f && pullMs < 500f)
                        {
                            CapToPullMs = Mathf.Lerp(CapToPullMs <= 0 ? pullMs : CapToPullMs, pullMs, 0.1f);
                            _latMaxAccum = Mathf.Max(_latMaxAccum, pullMs);
                        }
                        AsmMs = l[2] / 1000f;
                    }
                }
                catch { }
                if (Time.unscaledTime - _latMaxT >= 1f) { CapToPullMaxMs = _latMaxAccum; _latMaxAccum = 0f; _latMaxT = Time.unscaledTime; }
            }
            Tick(true);
        }

        float _logT;
        void Tick(bool got)
        {
            if (got) _newFrames++;
            float w = Time.unscaledTime - _fpsT0;
            if (w >= 1f) { DisplayedFps = _newFrames / w; _newFrames = 0; _fpsT0 = Time.unscaledTime; }
            Status = $"streaming {Width}x{Height} {(Fmt == 2 ? "MJPEG" : Fmt == 1 ? "YUY2" : "NV12")}";
            if (Time.unscaledTime - _logT >= 2f)
            {
                _logT = Time.unscaledTime;
                Debug.Log($"[nuvc] {Width}x{Height} video={VideoFps:0.0} disp={DisplayedFps:0.0} " +
                          $"cap→pull={CapToPullMs:0.0}ms(max{CapToPullMaxMs:0}) usbSpan={AsmMs:0.0}ms seq={Seq}");
            }
        }

        void EnsureTextures(int w, int h)
        {
            if (w <= 0 || h <= 0 || (w == Width && h == Height && _texY != null)) return;
            Width = w; Height = h;

            if (_texY) Destroy(_texY);
            if (_texUV) Destroy(_texUV);
            if (Output) Output.Release();

            _texY = new Texture2D(w, h, TextureFormat.R8, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _texUV = new Texture2D(w / 2, h / 2, TextureFormat.RG16, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "NuvcRGB" };
            Output.Create();
            _mat.SetTexture("_YTex", _texY);
            _mat.SetTexture("_UVTex", _texUV);
            Debug.Log($"[nuvc] textures {w}x{h}");
        }
    }
}
