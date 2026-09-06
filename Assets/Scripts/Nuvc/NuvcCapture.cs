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

        // Y-plane luminance of the newest frame — tells us if the HDMI source
        // is actually showing something (Switch on) or sending black (asleep /
        // no signal). NV12 black level is ~16; a live image is well above.
        public int LumaMin { get; private set; }
        public int LumaMax { get; private set; }
        public float LumaMean { get; private set; }
        public bool SourceLikelyBlack => LumaMax - LumaMin < 8 && LumaMean < 24f;

        [NonSerialized] public bool WantAudio = true;
        public string AudioStatus { get; private set; } = "off";

        // ---- stereo re-layout stage (Hdmr/NuvcStereo) --------------------
        // The Switch-2 in its VR/stereo render mode sends a side-by-side frame
        // with the lens distortion already baked in. This stage splits it,
        // pans/scales each eye-half so it lands in front of the eye, converges
        // them, and radial-warps to cancel (or add) barrel distortion. On a 2D
        // panel it can only preview one eye / SBS / anaglyph / ghost-overlay;
        // the slider values carry straight over to a true per-eye XR build.
        public RenderTexture StereoOutput { get; private set; }
        [NonSerialized] public bool StereoOn = false;
        [NonSerialized] public int SMode = 0;        // 0 full 1 L 2 R 3 SBS-fit 4 anaglyph 5 overlay
        [NonSerialized] public float SSplitX = 0.5f; // where the two halves meet in the source
        [NonSerialized] public float SScale = 1f;
        [NonSerialized] public float SOffXL = 0f, SOffYL = 0f, SOffXR = 0f, SOffYR = 0f;
        [NonSerialized] public float SConverge = 0f;
        [NonSerialized] public float SK1 = 0f, SK2 = 0f;
        [NonSerialized] public bool SGrid = false;

        [NonSerialized] public bool TestPattern = false;
        Material _tpMat;

        public RenderTexture Display => (StereoOn && StereoOutput != null) ? StereoOutput : Output;

        /// Second Blit: split the SBS source (or test pattern) and lay it out
        /// per the S* params. No-op when StereoOn is false.
        void ApplyStereoStage()
        {
            if (!StereoOn || _sMat == null || Output == null || StereoOutput == null) return;
            bool perEye = SMode >= 1 && SMode <= 5;
            float asp = Height > 0 ? (Width * (perEye ? 0.5f : 1f)) / Height : 1.777f;
            _sMat.SetFloat("_Mode", SMode);
            _sMat.SetFloat("_SplitX", SSplitX);
            _sMat.SetFloat("_Scale", SScale);
            _sMat.SetFloat("_OffXL", SOffXL);
            _sMat.SetFloat("_OffYL", SOffYL);
            _sMat.SetFloat("_OffXR", SOffXR);
            _sMat.SetFloat("_OffYR", SOffYR);
            _sMat.SetFloat("_Converge", SConverge);
            _sMat.SetFloat("_K1", SK1);
            _sMat.SetFloat("_K2", SK2);
            _sMat.SetFloat("_Aspect", asp);
            _sMat.SetFloat("_BgGrid", SGrid ? 1f : 0f);
            Graphics.Blit(Output, StereoOutput, _sMat);
        }

        void EnsureOutput(int w, int h)
        {
            if (Output != null && Width == w && Height == h) return;
            if (Output) Output.Release();
            if (StereoOutput) StereoOutput.Release();
            Width = w; Height = h;
            Output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "NuvcRGB" };
            Output.Create();
            StereoOutput = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "NuvcStereo" };
            StereoOutput.Create();
        }

        void RenderTestPattern()
        {
            EnsureOutput(1280, 720);
            if (_tpMat == null) _tpMat = new Material(Shader.Find("Hdmr/NuvcTestSBS"));
            if (_tpMat != null)
            {
                _tpMat.SetFloat("_Split", SSplitX);
                Graphics.Blit(null, Output, _tpMat);
            }
            Status = "test pattern (no live feed)";
            ApplyStereoStage();
        }

        static readonly string[] SModeNames = { "Full", "Left eye", "Right eye", "SBS fit", "Anaglyph", "Ghost overlay" };
        public string SModeName => SModeNames[Mathf.Clamp(SMode, 0, 5)];

        const string SK_ = "nuvc.st.";
        void LoadStereo()
        {
            StereoOn  = PlayerPrefs.GetInt(SK_ + "on", 0) == 1;
            SMode     = PlayerPrefs.GetInt(SK_ + "mode", 0);
            SSplitX   = PlayerPrefs.GetFloat(SK_ + "split", 0.5f);
            SScale    = PlayerPrefs.GetFloat(SK_ + "scale", 1f);
            SOffXL    = PlayerPrefs.GetFloat(SK_ + "oxl", 0f);
            SOffYL    = PlayerPrefs.GetFloat(SK_ + "oyl", 0f);
            SOffXR    = PlayerPrefs.GetFloat(SK_ + "oxr", 0f);
            SOffYR    = PlayerPrefs.GetFloat(SK_ + "oyr", 0f);
            SConverge = PlayerPrefs.GetFloat(SK_ + "conv", 0f);
            SK1       = PlayerPrefs.GetFloat(SK_ + "k1", 0f);
            SK2       = PlayerPrefs.GetFloat(SK_ + "k2", 0f);
            SGrid     = PlayerPrefs.GetInt(SK_ + "grid", 0) == 1;
            TestPattern = PlayerPrefs.GetInt(SK_ + "testpat", 0) == 1;
        }
        public void SaveStereo()
        {
            PlayerPrefs.SetInt(SK_ + "on", StereoOn ? 1 : 0);
            PlayerPrefs.SetInt(SK_ + "mode", SMode);
            PlayerPrefs.SetFloat(SK_ + "split", SSplitX);
            PlayerPrefs.SetFloat(SK_ + "scale", SScale);
            PlayerPrefs.SetFloat(SK_ + "oxl", SOffXL);
            PlayerPrefs.SetFloat(SK_ + "oyl", SOffYL);
            PlayerPrefs.SetFloat(SK_ + "oxr", SOffXR);
            PlayerPrefs.SetFloat(SK_ + "oyr", SOffYR);
            PlayerPrefs.SetFloat(SK_ + "conv", SConverge);
            PlayerPrefs.SetFloat(SK_ + "k1", SK1);
            PlayerPrefs.SetFloat(SK_ + "k2", SK2);
            PlayerPrefs.SetInt(SK_ + "grid", SGrid ? 1 : 0);
            PlayerPrefs.SetInt(SK_ + "testpat", TestPattern ? 1 : 0);
            PlayerPrefs.Save();
        }
        public void ResetStereo()
        {
            SSplitX = 0.5f; SScale = 1f; SOffXL = SOffYL = SOffXR = SOffYR = 0f;
            SConverge = 0f; SK1 = 0f; SK2 = 0f;
        }

        // NV12 rows are top-down; GL texture sampling is bottom-up -> flip Y by default.
        [NonSerialized] public bool FlipX = false, FlipY = true, FullRange = false;

        // Picture grade — the HDMI source can come in dark (limited-range decode
        // of a full-range signal, or just dark game content). Persisted.
        [NonSerialized] public float Gain = 1f, Gamma = 1f, Lift = 0f;

        const string PK = "nuvc.grade.";
        // true once the user has explicitly saved a grade — suppresses the
        // one-shot auto-grade that otherwise fires when the source comes in dark.
        bool _gradeIsUserSet;
        bool _autoGradeDone;
        int _lumaSamples;
        void LoadGrade()
        {
            FullRange = PlayerPrefs.GetInt(PK + "full", 0) == 1;
            FlipX = PlayerPrefs.GetInt(PK + "flipx", 0) == 1;
            FlipY = PlayerPrefs.GetInt(PK + "flipy", 1) == 1;
            _gradeIsUserSet = PlayerPrefs.GetInt(PK + "userset", 0) == 1;
            Gain = PlayerPrefs.GetFloat(PK + "gain", 1f);
            Gamma = PlayerPrefs.GetFloat(PK + "gamma", 1f);
            Lift = PlayerPrefs.GetFloat(PK + "lift", 0f);
        }
        /// Pick a grade from the latest luma stats: stretch the observed range
        /// toward 0..1 and add a little gamma.
        public void AutoGrade()
        {
            float mn = LumaMin / 255f, mx = LumaMax / 255f;
            if (mx - mn < 0.02f) { Gain = 3f; Gamma = 1.6f; Lift = 0.02f; return; }
            Gain = Mathf.Clamp(0.95f / Mathf.Max(mx, 0.05f), 1f, 6f);
            Lift = Mathf.Clamp(mn * -0.5f + 0.02f, 0f, 0.1f);
            Gamma = 1.4f;
        }

        public void SaveGrade()
        {
            PlayerPrefs.SetInt(PK + "full", FullRange ? 1 : 0);
            PlayerPrefs.SetInt(PK + "flipx", FlipX ? 1 : 0);
            PlayerPrefs.SetInt(PK + "flipy", FlipY ? 1 : 0);
            PlayerPrefs.SetFloat(PK + "gain", Gain);
            PlayerPrefs.SetFloat(PK + "gamma", Gamma);
            PlayerPrefs.SetFloat(PK + "lift", Lift);
            _gradeIsUserSet = true;
            PlayerPrefs.SetInt(PK + "userset", 1);
            PlayerPrefs.Save();
        }

        AndroidJavaObject _nuvc;
        AndroidJavaObject _audio;
        AndroidJavaObject _activity;
        bool _audioStarted;
        float _audioT;
        Material _mat;
        Material _sMat;
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
            var sShader = Shader.Find("Hdmr/NuvcStereo");
            if (sShader != null) _sMat = new Material(sShader);
            LoadGrade();
            LoadStereo();
            _buf = new byte[3840 * 2160 * 2];
            _pin = GCHandle.Alloc(_buf, GCHandleType.Pinned);
            _base = _pin.AddrOfPinnedObject();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _activity = up.GetStatic<AndroidJavaObject>("currentActivity");
                _nuvc = new AndroidJavaObject("com.natis.nuvc.Nuvc");
                _audio = new AndroidJavaObject("com.natis.nuvc.Uac");
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

        // Audio-only-on-launch was a diagnostic (rule out bus contention — it
        // did: the isochronous reads fail with zero video traffic too). It's
        // removed because it had a side effect that now looks like the actual
        // cause: this card only puts audio on the wire once its HDMI capture
        // pipeline is live, i.e. once the UVC video stream is COMMIT-ed and
        // pulling frames. So: start video first, wait for real frames, only
        // then start audio (see ServiceAudio's gate).
        int _framesSeen;

        void Update()
        {
            if (TestPattern)
            {
                RenderTestPattern();
                Tick(true);
                return;
            }

            if (_nuvc == null) return;

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
                // Only start audio once video is genuinely flowing — this card
                // gates the UAC payload on an active HDMI-capture pipeline.
                bool videoLive = Streaming && Seq > 0 && _framesSeen > 30;
                if (WantAudio && videoLive && !_audioStarted && Time.unscaledTime - _audioT > 2f)
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
                SampleLuma(yBytes);
                _texY.LoadRawTextureData(_base, yBytes);
                _texY.Apply(false);
                _texUV.LoadRawTextureData(IntPtr.Add(_base, yBytes), uvBytes);
                _texUV.Apply(false);

                _mat.SetFloat("_FullRange", FullRange ? 1f : 0f);
                _mat.SetFloat("_FlipX", FlipX ? 1f : 0f);
                _mat.SetFloat("_FlipY", FlipY ? 1f : 0f);
                _mat.SetFloat("_Gain", Gain);
                _mat.SetFloat("_Gamma", Gamma);
                _mat.SetFloat("_Lift", Lift);
                Graphics.Blit(_texY, Output, _mat);

                ApplyStereoStage();

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

        float _lumaT;
        void SampleLuma(int yBytes)
        {
            // ~4k samples on a stride — cheap, enough for a black/live verdict
            if (Time.unscaledTime - _lumaT < 0.5f) return;
            _lumaT = Time.unscaledTime;
            int step = Mathf.Max(1, yBytes / 4096);
            int mn = 255, mx = 0; long sum = 0; int n = 0;
            for (int i = 0; i < yBytes; i += step)
            {
                int v = _buf[i];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
                sum += v; n++;
            }
            LumaMin = mn; LumaMax = mx; LumaMean = n > 0 ? (float)sum / n : 0f;

            // One-shot: if the user has never set a grade and, after a few
            // readings, the source is clearly dark (never reaches near-white and
            // isn't a flat black frame), lift it automatically so the panel
            // shows something instead of near-black on first run.
            if (!_gradeIsUserSet && !_autoGradeDone)
            {
                if (mx >= 8 && ++_lumaSamples >= 4)
                {
                    _autoGradeDone = true;
                    if (mx < 150)
                    {
                        AutoGrade();
                        Debug.Log($"[nuvc] auto-grade on dark source (luma {mn}/{LumaMean:0}/{mx}) -> " +
                                  $"gain {Gain:0.00} gamma {Gamma:0.00} lift {Lift:0.00}");
                    }
                }
            }
        }

        // ---- source geometry auto-detect --------------------------------
        // Scan the Y plane for (a) the central dark seam of an SBS frame and
        // (b) the content bounding box inside each half (the Switch's VR mode
        // frames each eye view in a black field). Turn that into starting
        // slider values so the user isn't dialling from zero.
        public string DetectStatus { get; private set; } = "not run";
        public float DetSplitX { get; private set; } = 0.5f;
        public float DetContentW { get; private set; }   // content width as fraction of a half
        public float DetContentH { get; private set; }
        public float DetCenXL { get; private set; }       // content centre in the left half, 0..1
        public float DetCenXR { get; private set; }
        public float DetCenY { get; private set; }
        public bool HasDetection { get; private set; }

        public void AnalyzeSource()
        {
            int w = Width, h = Height;
            if (Fmt != 0 || w < 16 || h < 16 || _buf == null || _buf.Length < w * h)
            {
                DetectStatus = "need a live NV12 frame"; return;
            }

            int cols = Mathf.Min(w, 512), rows = Mathf.Min(h, 288);
            var cmean = new float[cols];
            var rmean = new float[rows];
            for (int cxi = 0; cxi < cols; cxi++)
            {
                int x = (int)((cxi + 0.5f) * w / cols);
                long s = 0; int nn = 0;
                for (int ryi = 0; ryi < rows; ryi++)
                {
                    int y = (int)((ryi + 0.5f) * h / rows);
                    s += _buf[y * w + x]; nn++;
                }
                cmean[cxi] = (float)s / nn;
            }
            for (int ryi = 0; ryi < rows; ryi++)
            {
                int y = (int)((ryi + 0.5f) * h / rows);
                long s = 0; int nn = 0;
                for (int cxi = 0; cxi < cols; cxi++)
                {
                    int x = (int)((cxi + 0.5f) * w / cols);
                    s += _buf[y * w + x]; nn++;
                }
                rmean[ryi] = (float)s / nn;
            }

            float cMax = 1f; foreach (var v in cmean) if (v > cMax) cMax = v;
            float rMax = 1f; foreach (var v in rmean) if (v > rMax) rMax = v;

            // (a) seam: darkest column in the central 40 %
            int lo = cols * 3 / 10, hi = cols * 7 / 10, seam = cols / 2; float seamV = 1e9f;
            for (int i = lo; i < hi; i++) if (cmean[i] < seamV) { seamV = cmean[i]; seam = i; }
            DetSplitX = (seam + 0.5f) / cols;

            // (b) content box per half — first/last column above 12 % of that half's max
            float thr = 0.12f;
            EdgeBox(cmean, 0, seam, cMax * thr, out int lL, out int lR);
            EdgeBox(cmean, seam, cols, cMax * thr, out int rL, out int rR);
            EdgeBox(rmean, 0, rows, rMax * thr, out int tT, out int tB);

            float halfL = seam, halfR = cols - seam;
            DetContentW = Mathf.Clamp01(((lR - lL) / Mathf.Max(halfL, 1f) + (rR - rL) / Mathf.Max(halfR, 1f)) * 0.5f);
            DetContentH = Mathf.Clamp01((tB - tT) / Mathf.Max((float)rows, 1f));
            DetCenXL = Mathf.Clamp01(((lL + lR) * 0.5f) / Mathf.Max(halfL, 1f));
            DetCenXR = Mathf.Clamp01(((rL + rR) * 0.5f - seam) / Mathf.Max(halfR, 1f));
            DetCenY = Mathf.Clamp01(((tT + tB) * 0.5f) / Mathf.Max((float)rows, 1f));

            HasDetection = true;
            DetectStatus = $"split {DetSplitX:0.000}  box {DetContentW:0.00}x{DetContentH:0.00}  " +
                           $"cenL {DetCenXL:0.00}/{DetCenY:0.00} cenR {DetCenXR:0.00}";
            Debug.Log("[nuvc] analyze: " + DetectStatus);
        }

        static void EdgeBox(float[] a, int lo, int hi, float thr, out int first, out int last)
        {
            first = lo; last = hi - 1;
            for (int i = lo; i < hi; i++) { if (a[i] >= thr) { first = i; break; } }
            for (int i = hi - 1; i >= lo; i--) { if (a[i] >= thr) { last = i; break; } }
            if (last < first) { first = lo; last = hi - 1; }
        }

        /// Push the detected geometry into the live sliders.
        public void ApplyDetection()
        {
            if (!HasDetection) { AnalyzeSource(); if (!HasDetection) return; }
            SSplitX = Mathf.Clamp(DetSplitX, 0.3f, 0.7f);
            SScale = Mathf.Clamp(1f / Mathf.Max(DetContentW, 0.2f), 0.4f, 2.5f);
            SOffXL = Mathf.Clamp((DetCenXL - 0.5f), -0.5f, 0.5f);
            SOffXR = Mathf.Clamp((DetCenXR - 0.5f), -0.5f, 0.5f);
            SOffYL = SOffYR = Mathf.Clamp((DetCenY - 0.5f), -0.5f, 0.5f);
            SaveStereo();
        }

        float _logT;
        void Tick(bool got)
        {
            if (got) { _newFrames++; if (_framesSeen < 1000) _framesSeen++; }
            float w = Time.unscaledTime - _fpsT0;
            if (w >= 1f) { DisplayedFps = _newFrames / w; _newFrames = 0; _fpsT0 = Time.unscaledTime; }
            Status = $"streaming {Width}x{Height} {(Fmt == 2 ? "MJPEG" : Fmt == 1 ? "YUY2" : "NV12")}";
            if (Time.unscaledTime - _logT >= 2f)
            {
                _logT = Time.unscaledTime;
                Debug.Log($"[nuvc] {Width}x{Height} video={VideoFps:0.0} disp={DisplayedFps:0.0} " +
                          $"cap→pull={CapToPullMs:0.0}ms(max{CapToPullMaxMs:0}) usbSpan={AsmMs:0.0}ms seq={Seq} " +
                          $"luma {LumaMin}/{LumaMean:0}/{LumaMax}{(SourceLikelyBlack ? " *BLACK*" : "")}");
            }
        }

        void EnsureTextures(int w, int h)
        {
            if (w <= 0 || h <= 0 || (w == Width && h == Height && _texY != null)) return;
            Width = w; Height = h;

            if (_texY) Destroy(_texY);
            if (_texUV) Destroy(_texUV);
            if (Output) Output.Release();
            if (StereoOutput) StereoOutput.Release();

            _texY = new Texture2D(w, h, TextureFormat.R8, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _texUV = new Texture2D(w / 2, h / 2, TextureFormat.RG16, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Output = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "NuvcRGB" };
            Output.Create();
            StereoOutput = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "NuvcStereo" };
            StereoOutput.Create();
            _mat.SetTexture("_YTex", _texY);
            _mat.SetTexture("_UVTex", _texUV);
            Debug.Log($"[nuvc] textures {w}x{h}");
        }
    }
}
