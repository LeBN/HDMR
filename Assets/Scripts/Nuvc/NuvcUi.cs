using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// On-panel UI: the RGB (or stereo-processed) output drawn edge-to-edge,
    /// a tap-toggled stats strip, and collapsible PICTURE and STEREO panels
    /// full of sliders.
    /// </summary>
    public sealed class NuvcUi : MonoBehaviour
    {
        public NuvcCapture cap;

        bool _stats = true;
        bool _grade = false;
        bool _stereo = false;
        GUIStyle _label, _small;

        void Awake() { _label = null; }

        void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 26, normal = { textColor = Color.white } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 20, normal = { textColor = new Color(1, 1, 1, 0.85f) } };
        }

        // labelled horizontal slider; returns the (possibly) new value
        static float Row(float x, float y, float w, string name, float v, float lo, float hi, GUIStyle st, ref bool dirty)
        {
            GUI.Label(new Rect(x, y - 24, w, 22), $"{name}   {v:0.000}", st);
            float nv = GUI.HorizontalSlider(new Rect(x, y, w, 28), v, lo, hi);
            if (!Mathf.Approximately(nv, v)) dirty = true;
            return nv;
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
                cap.Gain = Row(px + 24, py + 78, pw - 48, "Exposure / gain", cap.Gain, 0.5f, 8f, _small, ref d);
                cap.Gamma = Row(px + 24, py + 148, pw - 48, "Gamma (mid lift)", cap.Gamma, 0.3f, 3f, _small, ref d);
                cap.Lift = Row(px + 24, py + 218, pw - 48, "Black lift", cap.Lift, 0f, 0.4f, _small, ref d);

                if (GUI.Button(new Rect(px + 24, py + 258, 170, 50), "Auto")) { cap.AutoGrade(); d = true; }
                if (GUI.Button(new Rect(px + 208, py + 258, 170, 50), "Reset")) { cap.Gain = 1; cap.Gamma = 1; cap.Lift = 0; d = true; }
                if (GUI.Button(new Rect(px + 392, py + 258, 200, 50), "Save")) cap.SaveGrade();
                if (d) cap.SaveGrade();
            }

            if (_stereo && cap != null)
            {
                // left-anchored so it clears the bottom stats strip and the
                // top-right Picture panel
                float pw = 660, px = 24, py = 76, cx = px + 20, cw = pw - 40;
                GUI.color = new Color(0, 0, 0, 0.86f);
                GUI.DrawTexture(new Rect(px, py, pw, 700), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(cx, py + 6, pw, 26), "STEREO  —  split & align the SBS source", _label);

                bool d = false;
                if (GUI.Button(new Rect(cx, py + 38, 240, 44), cap.StereoOn ? "Stereo: ON" : "Stereo: off")) { cap.StereoOn = !cap.StereoOn; d = true; }
                if (GUI.Button(new Rect(cx + 254, py + 38, cw - 254, 44), $"View: {cap.SModeName}")) { cap.SMode = (cap.SMode + 1) % 6; d = true; }

                if (GUI.Button(new Rect(cx, py + 90, 300, 42), "Detect geometry")) cap.AnalyzeSource();
                if (GUI.Button(new Rect(cx + 314, py + 90, cw - 314, 42), "Apply ▸ sliders")) cap.ApplyDetection();
                GUI.Label(new Rect(cx, py + 134, cw, 20), "detect: " + cap.DetectStatus, _small);

                float r = py + 184; const float P = 46;
                cap.SSplitX   = Row(cx, r + 0 * P, cw, "Source split X (L|R meet)", cap.SSplitX, 0.3f, 0.7f, _small, ref d);
                cap.SScale    = Row(cx, r + 1 * P, cw, "Scale (zoom each eye)", cap.SScale, 0.4f, 2.5f, _small, ref d);
                cap.SOffXL    = Row(cx, r + 2 * P, cw, "Left eye offset X", cap.SOffXL, -0.5f, 0.5f, _small, ref d);
                cap.SOffYL    = Row(cx, r + 3 * P, cw, "Left eye offset Y", cap.SOffYL, -0.5f, 0.5f, _small, ref d);
                cap.SOffXR    = Row(cx, r + 4 * P, cw, "Right eye offset X", cap.SOffXR, -0.5f, 0.5f, _small, ref d);
                cap.SOffYR    = Row(cx, r + 5 * P, cw, "Right eye offset Y", cap.SOffYR, -0.5f, 0.5f, _small, ref d);
                cap.SConverge = Row(cx, r + 6 * P, cw, "Convergence (toe-in)", cap.SConverge, -0.3f, 0.3f, _small, ref d);
                cap.SK1       = Row(cx, r + 7 * P, cw, "Distortion k1 (- barrel / + pincushion)", cap.SK1, -0.6f, 0.6f, _small, ref d);
                cap.SK2       = Row(cx, r + 8 * P, cw, "Distortion k2", cap.SK2, -0.4f, 0.4f, _small, ref d);

                float by = r + 9 * P + 4;
                if (GUI.Button(new Rect(cx, by, 140, 44), cap.SGrid ? "Grid ✓" : "Grid")) { cap.SGrid = !cap.SGrid; d = true; }
                if (GUI.Button(new Rect(cx + 154, by, 150, 44), "Reset")) { cap.ResetStereo(); d = true; }
                if (GUI.Button(new Rect(cx + 318, by, cw - 318, 44), "Save")) cap.SaveStereo();
                if (d) cap.SaveStereo();
            }
        }
    }
}
