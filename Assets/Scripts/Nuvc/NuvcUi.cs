using System;
using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// On-panel UI: the RGB (or stereo-processed) output drawn edge-to-edge,
    /// a tap-toggled stats strip, and collapsible PICTURE and STEREO panels.
    /// The stereo panel: a slider per parameter, plus a shared fine-editor bar
    /// (−−/−/+/++, keyboard entry, thumbstick nudge) for the selected row.
    /// </summary>
    public sealed class NuvcUi : MonoBehaviour
    {
        public NuvcCapture cap;

        bool _stats = true;
        bool _grade = false;
        bool _stereo = false;
        GUIStyle _label, _small, _sel;

        // stereo fine-editor state
        int _selRow = 3;                 // default-select k1
        TouchScreenKeyboard _kb;         // on-screen numeric keyboard while editing
        int _kbRow = -1;

        void Awake() { _label = null; }

        void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 26, normal = { textColor = Color.white } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 20, normal = { textColor = new Color(1, 1, 1, 0.85f) } };
            _sel = new GUIStyle(_small) { normal = { textColor = new Color(0.5f, 1f, 0.7f) }, fontStyle = FontStyle.Bold };
        }

        struct P
        {
            public string name; public Func<float> get; public Action<float> set;
            public float lo, hi, step; public bool slider;
        }

        P[] Params()
        {
            var c = cap;
            return new[]
            {
                new P { name = "Source split X",   get = () => c.SSplitX,   set = v => c.SSplitX = v,   lo = 0.30f, hi = 0.70f, step = 0.002f, slider = true },
                new P { name = "Scale",            get = () => c.SScale,    set = v => c.SScale = v,    lo = 0.40f, hi = 2.50f, step = 0.01f,  slider = true },
                new P { name = "Convergence",      get = () => c.SConverge, set = v => c.SConverge = v, lo = -0.3f, hi = 0.3f,  step = 0.004f, slider = true },
                new P { name = "Distortion k1",    get = () => c.SK1,       set = v => c.SK1 = v,       lo = -2.5f, hi = 0.6f,  step = 0.01f,  slider = true },
                new P { name = "Distortion k2",    get = () => c.SK2,       set = v => c.SK2 = v,       lo = -2.0f, hi = 2.0f,  step = 0.01f,  slider = true },
                new P { name = "Fill X",           get = () => c.SFillX,    set = v => c.SFillX = v,    lo = 0.5f,  hi = 2.0f,  step = 0.01f,  slider = true },
                new P { name = "Fill Y",           get = () => c.SFillY,    set = v => c.SFillY = v,    lo = 0.5f,  hi = 3.0f,  step = 0.01f,  slider = true },
                new P { name = "Left eye off X",   get = () => c.SOffXL,    set = v => c.SOffXL = v,    lo = -0.5f, hi = 0.5f,  step = 0.004f, slider = true },
                new P { name = "Left eye off Y",   get = () => c.SOffYL,    set = v => c.SOffYL = v,    lo = -0.5f, hi = 0.5f,  step = 0.004f, slider = true },
                new P { name = "Right eye off X",  get = () => c.SOffXR,    set = v => c.SOffXR = v,    lo = -0.5f, hi = 0.5f,  step = 0.004f, slider = false },
                new P { name = "Right eye off Y",  get = () => c.SOffYR,    set = v => c.SOffYR = v,    lo = -0.5f, hi = 0.5f,  step = 0.004f, slider = false },
                new P { name = "Dist centre X L",  get = () => c.SCenXL,    set = v => c.SCenXL = v,    lo = 0.2f,  hi = 0.8f,  step = 0.004f, slider = false },
                new P { name = "Dist centre Y L",  get = () => c.SCenYL,    set = v => c.SCenYL = v,    lo = 0.2f,  hi = 0.8f,  step = 0.004f, slider = false },
                new P { name = "Dist centre X R",  get = () => c.SCenXR,    set = v => c.SCenXR = v,    lo = 0.2f,  hi = 0.8f,  step = 0.004f, slider = false },
                new P { name = "Dist centre Y R",  get = () => c.SCenYR,    set = v => c.SCenYR = v,    lo = 0.2f,  hi = 0.8f,  step = 0.004f, slider = false },
            };
        }

        void OnGUI()
        {
            EnsureStyles();
            float W = Screen.width, H = Screen.height;

            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var tex = cap != null ? cap.Display : null;
            if (tex != null && cap.Width > 0)
            {
                float ta = (float)cap.Width / cap.Height, pa = W / H;
                float rw = W, rh = H;
                if (ta > pa) rh = W / ta; else rw = H * ta;
                GUI.DrawTexture(new Rect((W - rw) * 0.5f, (H - rh) * 0.5f, rw, rh), tex, ScaleMode.StretchToFill, false);
            }
            else
            {
                GUI.Label(new Rect(0, H * 0.45f, W, 40), cap != null ? cap.Status : "no capture", _label);
            }

            if (GUI.Button(new Rect(0, 0, 90, 60), "i")) _stats = !_stats;

            if (_stats && !_stereo && cap != null)
            {
                string link = cap.LinkSpeed == 4 ? "USB3 SuperSpeed" : cap.LinkSpeed == 3 ? "USB2 High" : "link " + cap.LinkSpeed;
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(new Rect(0, H - 200, W, 134), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(24, H - 194, W, 32),
                    $"nuvc · {link} · {cap.Width}x{cap.Height} {(cap.Fmt == 2 ? "MJPEG" : cap.Fmt == 1 ? "YUY2" : "NV12")}", _label);
                GUI.Label(new Rect(24, H - 158, W, 28),
                    $"video {cap.VideoFps:0.0} fps   ·   displayed {cap.DisplayedFps:0.0} fps   ·   seq {cap.Seq}   ·   " +
                    $"cap→pull {cap.CapToPullMs:0.0} ms (max {cap.CapToPullMaxMs:0})   ·   USB {cap.AsmMs:0.0} ms", _small);
                GUI.Label(new Rect(24, H - 128, W, 28), $"{cap.Status}   ·   audio: {cap.AudioStatus}", _small);
                GUI.color = cap.SourceLikelyBlack ? new Color(1f, 0.5f, 0.4f) : new Color(0.5f, 1f, 0.6f);
                GUI.Label(new Rect(24, H - 98, W, 26),
                    $"source luma {cap.LumaMin}/{cap.LumaMean:0}/{cap.LumaMax}   " +
                    $"{(cap.SourceLikelyBlack ? "— LIKELY BLACK (Switch asleep / no HDMI signal?)" : "— live image")}", _small);
                GUI.color = Color.white;
            }

            if (cap != null)
            {
                bool dirty = false;
                float bw = 140, bx = W - 6 * bw - 12;
                if (GUI.Button(new Rect(bx + 0 * bw, H - 62, bw - 8, 54), "Flip X")) { cap.FlipX = !cap.FlipX; dirty = true; }
                if (GUI.Button(new Rect(bx + 1 * bw, H - 62, bw - 8, 54), "Flip Y")) { cap.FlipY = !cap.FlipY; dirty = true; }
                if (GUI.Button(new Rect(bx + 2 * bw, H - 62, bw - 8, 54), cap.FullRange ? "Full rng" : "Limited")) { cap.FullRange = !cap.FullRange; dirty = true; }
                if (GUI.Button(new Rect(bx + 3 * bw, H - 62, bw - 8, 54), _grade ? "Picture ▲" : "Picture ▼")) { _grade = !_grade; _stereo = false; }
                if (GUI.Button(new Rect(bx + 4 * bw, H - 62, bw - 8, 54), _stereo ? "Stereo ▲" : "Stereo ▼")) { _stereo = !_stereo; _grade = false; }
                if (GUI.Button(new Rect(bx + 5 * bw, H - 62, bw - 8, 54), cap.WantAudio ? "Audio ✓" : "Audio ✗")) cap.WantAudio = !cap.WantAudio;
                if (dirty) cap.SaveGrade();
            }

            if (_grade && cap != null)
            {
                float pw = 640, px = W - pw - 30, py = 90;
                GUI.color = new Color(0, 0, 0, 0.85f);
                GUI.DrawTexture(new Rect(px, py, pw, 330), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(px + 20, py + 8, pw, 26), "PICTURE  —  the HDMI feed is dark; lift it", _label);

                bool d = false;
                cap.Gain = LabSlider(px + 24, py + 78, pw - 48, "Exposure / gain", cap.Gain, 0.5f, 8f, ref d);
                cap.Gamma = LabSlider(px + 24, py + 148, pw - 48, "Gamma (mid lift)", cap.Gamma, 0.3f, 3f, ref d);
                cap.Lift = LabSlider(px + 24, py + 218, pw - 48, "Black lift", cap.Lift, 0f, 0.4f, ref d);

                if (GUI.Button(new Rect(px + 24, py + 258, 170, 50), "Auto")) { cap.AutoGrade(); d = true; }
                if (GUI.Button(new Rect(px + 208, py + 258, 170, 50), "Reset")) { cap.Gain = 1; cap.Gamma = 1; cap.Lift = 0; d = true; }
                if (GUI.Button(new Rect(px + 392, py + 258, 200, 50), "Save")) cap.SaveGrade();
                if (d) cap.SaveGrade();
            }

            if (_stereo && cap != null)
                StereoPanel(W, H);
        }

        static float LabSlider(float x, float y, float w, string name, float v, float lo, float hi, ref bool dirty)
        {
            GUI.Label(new Rect(x, y - 24, w, 22),
                $"{name}   {v:0.000}", GUI.skin.label);
            float nv = GUI.HorizontalSlider(new Rect(x, y, w, 28), v, lo, hi);
            if (!Mathf.Approximately(nv, v)) dirty = true;
            return nv;
        }

        float _saveAt = -1f;    // debounced SaveStereo time

        void StereoPanel(float W, float H)
        {
            float pw = 720, px = 24, py = 52, cx = px + 20, cw = pw - 40;
            var ps = Params();
            int nSlider = 0; foreach (var p in ps) if (p.slider) nSlider++;
            float rowTop = py + 196, rowPitch = 34;
            float editTop = rowTop + nSlider * rowPitch + 12;
            float panelH = editTop + 178 - py;

            GUI.color = new Color(0, 0, 0, 0.9f);
            GUI.DrawTexture(new Rect(px, py, pw, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(cx, py + 2, pw, 26), "STEREO  —  split, un-distort & align each eye", _label);

            bool d = false;
            if (GUI.Button(new Rect(cx, py + 32, 210, 40), cap.StereoOn ? "Stereo: ON" : "Stereo: off")) { cap.StereoOn = !cap.StereoOn; d = true; }
            if (GUI.Button(new Rect(cx + 222, py + 32, 240, 40), $"View: {cap.SModeName}")) { cap.SMode = (cap.SMode + 1) % 6; d = true; }
            if (GUI.Button(new Rect(cx + 472, py + 32, cw - 472, 40), cap.SGrid ? "Grid ✓" : "Grid")) { cap.SGrid = !cap.SGrid; d = true; }

            if (GUI.Button(new Rect(cx, py + 78, 200, 38), "Detect geom")) cap.AnalyzeSource();
            if (GUI.Button(new Rect(cx + 210, py + 78, 180, 38), "Apply ▸")) cap.ApplyDetection();
            if (GUI.Button(new Rect(cx + 400, py + 78, 120, 38), cap.TestPattern ? "Patt ✓" : "Test patt")) { cap.TestPattern = !cap.TestPattern; d = true; }
            if (GUI.Button(new Rect(cx + 526, py + 78, 110, 38), "Dump ⇩")) { cap.SaveStereo(); cap.DumpVals(); }
            if (GUI.Button(new Rect(cx + 642, py + 78, cw - 642, 38), "Reset")) { cap.ResetStereo(); d = true; }

            GUI.color = !cap.HasDetection ? Color.white
                      : cap.SourceLooksStereo ? new Color(0.5f, 1f, 0.6f) : new Color(1f, 0.55f, 0.4f);
            GUI.Label(new Rect(cx, py + 122, cw, 20), "detect: " + cap.DetectStatus, _small);
            if (cap.StereoOn && cap.HasDetection && !cap.SourceLooksStereo && !cap.TestPattern)
                GUI.Label(new Rect(cx, py + 142, cw, 20), "⚠ halves differ — is the Switch in VR mode?", _small);
            GUI.color = Color.white;

            // slider rows (tap name to select for the fine editor)
            int shown = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                if (!ps[i].slider) continue;
                float ry = rowTop + shown * rowPitch; shown++;
                bool seld = i == _selRow;
                if (GUI.Button(new Rect(cx, ry, 200, 28), GUIContent.none, GUIStyle.none)) _selRow = i;
                GUI.Label(new Rect(cx, ry + 2, 200, 22), (seld ? "▸ " : "  ") + ps[i].name, seld ? _sel : _small);
                float v = ps[i].get();
                float nv = GUI.HorizontalSlider(new Rect(cx + 214, ry + 7, cw - 214 - 110, 16), v, ps[i].lo, ps[i].hi);
                if (!Mathf.Approximately(nv, v)) { ps[i].set(nv); d = true; _selRow = i; }
                GUI.Label(new Rect(cx + cw - 104, ry + 2, 104, 22), ps[i].get().ToString("0.000"), seld ? _sel : _small);
            }

            // ---- fine editor for the selected row ----
            var sp = ps[_selRow];
            float cur = sp.get();
            GUI.color = new Color(0.12f, 0.17f, 0.14f, 0.97f);
            GUI.DrawTexture(new Rect(cx - 6, editTop - 6, cw + 12, 158), Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (GUI.Button(new Rect(cx, editTop, 40, 26), "◂")) { _selRow = (_selRow - 1 + ps.Length) % ps.Length; }
            if (GUI.Button(new Rect(cx + 44, editTop, 40, 26), "▸")) { _selRow = (_selRow + 1) % ps.Length; }
            GUI.Label(new Rect(cx + 96, editTop + 1, cw, 24), $"EDIT ▸ {sp.name}   [{sp.lo:0.##} … {sp.hi:0.##}]", _sel);

            float ex = cx, eb = 76, eby = editTop + 34;
            if (GUI.Button(new Rect(ex, eby, eb, 46), "−−", _label)) { sp.set(Mathf.Clamp(cur - sp.step * 10, sp.lo, sp.hi)); d = true; }
            if (GUI.Button(new Rect(ex + eb + 6, eby, eb, 46), "−", _label)) { sp.set(Mathf.Clamp(cur - sp.step, sp.lo, sp.hi)); d = true; }

            // keyboard-editable value — tap opens the on-screen numeric keyboard
            if (_kb != null && _kbRow == _selRow)
            {
                if (_kb.status == TouchScreenKeyboard.Status.Visible || _kb.status == TouchScreenKeyboard.Status.Done)
                {
                    if (float.TryParse(_kb.text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float pv))
                    { sp.set(Mathf.Clamp(pv, sp.lo, sp.hi)); d = true; }
                }
                if (_kb.status != TouchScreenKeyboard.Status.Visible) { _kb = null; _kbRow = -1; }
            }
            string vStr = (_kb != null && _kbRow == _selRow) ? _kb.text : cur.ToString("0.###");
            if (GUI.Button(new Rect(ex + 2 * (eb + 6), eby, 230, 46), vStr + "  ⌨", _label))
            {
                _kb = TouchScreenKeyboard.Open(cur.ToString("0.###"),
                    TouchScreenKeyboardType.NumbersAndPunctuation, false, false, false, false);
                _kbRow = _selRow;
            }

            if (GUI.Button(new Rect(ex + 2 * (eb + 6) + 238, eby, eb, 46), "+", _label)) { sp.set(Mathf.Clamp(cur + sp.step, sp.lo, sp.hi)); d = true; }
            if (GUI.Button(new Rect(ex + 3 * (eb + 6) + 238, eby, eb, 46), "++", _label)) { sp.set(Mathf.Clamp(cur + sp.step * 10, sp.lo, sp.hi)); d = true; }
            if (GUI.Button(new Rect(ex + 4 * (eb + 6) + 244, eby, 130, 46), "Save all")) cap.SaveStereo();

            // thumbstick / arrow nudge of the selected row (continuous while held)
            float ax = 0f;
            try { ax = Input.GetAxis("Horizontal"); } catch { }
            if (Mathf.Abs(ax) < 0.15f) ax = 0f;
            if (Input.GetKey(KeyCode.RightArrow)) ax = 1f;
            if (Input.GetKey(KeyCode.LeftArrow)) ax = -1f;
            if (ax != 0f)
            {
                sp.set(Mathf.Clamp(cur + ax * sp.step * Time.unscaledDeltaTime * 12f, sp.lo, sp.hi));
                d = true;
            }
            GUI.Label(new Rect(cx, eby + 54, cw, 20),
                $"◂▸ pick · −/+ step · tap value for keyboard · thumbstick ⇆ nudges (axis {ax:+0.00;-0.00;0})", _small);
            GUI.Label(new Rect(cx, eby + 78, cw, 20),
                $"k1 {cap.SK1:0.00;-0.00;0}  k2 {cap.SK2:0.00;-0.00;0}  fill {cap.SFillX:0.00}/{cap.SFillY:0.00}  " +
                $"scale {cap.SScale:0.00}  split {cap.SSplitX:0.000}  L {cap.SOffXL:0.00;-0.00;0}/{cap.SOffYL:0.00;-0.00;0}", _small);

            // debounced save: ~0.4 s after the last change
            if (d) _saveAt = Time.unscaledTime + 0.4f;
            if (_saveAt > 0f && Time.unscaledTime >= _saveAt) { cap.SaveStereo(); _saveAt = -1f; }
        }
    }
}
