using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// Minimal on-panel UI: the RGB output drawn edge-to-edge (letterboxed to
    /// aspect), a tap-toggled stats strip, and flip / range toggles.
    /// </summary>
    public sealed class NuvcUi : MonoBehaviour
    {
        public NuvcCapture cap;

        bool _stats = true;
        bool _grade = false;
        GUIStyle _label, _small;

        static float Slider(Rect r, string name, float v, float lo, float hi, GUIStyle st)
        {
            GUI.Label(new Rect(r.x, r.y - 22, r.width, 20), $"{name}  {v:0.00}", st);
            return GUI.HorizontalSlider(r, v, lo, hi);
        }

        void Awake()
        {
            _label = null;
        }

        void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 26, normal = { textColor = Color.white } };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 20, normal = { textColor = new Color(1, 1, 1, 0.85f) } };
        }

        void OnGUI()
        {
            EnsureStyles();
            float W = Screen.width, H = Screen.height;

            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var tex = cap != null ? cap.Output : null;
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

            // top-left toggle
            if (GUI.Button(new Rect(0, 0, 90, 60), "i")) _stats = !_stats;

            if (_stats && cap != null)
            {
                string link = cap.LinkSpeed == 4 ? "USB3 SuperSpeed" : cap.LinkSpeed == 3 ? "USB2 High" : "link " + cap.LinkSpeed;
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(new Rect(0, H - 164, W, 164), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(24, H - 158, W, 32),
                    $"nuvc · {link} · {cap.Width}x{cap.Height} {(cap.Fmt == 2 ? "MJPEG" : cap.Fmt == 1 ? "YUY2" : "NV12")}", _label);
                GUI.Label(new Rect(24, H - 122, W, 28),
                    $"video {cap.VideoFps:0.0} fps   ·   displayed {cap.DisplayedFps:0.0} fps   ·   seq {cap.Seq}", _small);
                GUI.Label(new Rect(24, H - 92, W, 28),
                    $"capture→pull {cap.CapToPullMs:0.0} ms  (max {cap.CapToPullMaxMs:0} )   ·   USB span {cap.AsmMs:0.0} ms", _small);
                GUI.Label(new Rect(24, H - 62, W, 28), $"{cap.Status}   ·   audio: {cap.AudioStatus}", _small);
                GUI.color = cap.SourceLikelyBlack ? new Color(1f, 0.5f, 0.4f) : new Color(0.5f, 1f, 0.6f);
                GUI.Label(new Rect(24, H - 190, W, 26),
                    $"source luma {cap.LumaMin}/{cap.LumaMean:0}/{cap.LumaMax}   {(cap.SourceLikelyBlack ? "— LIKELY BLACK (Switch asleep / no HDMI signal?)" : "— live image")}", _small);
                GUI.color = Color.white;

                bool dirty = false;
                if (GUI.Button(new Rect(W - 700, H - 100, 120, 56), "Flip X")) { cap.FlipX = !cap.FlipX; dirty = true; }
                if (GUI.Button(new Rect(W - 574, H - 100, 120, 56), "Flip Y")) { cap.FlipY = !cap.FlipY; dirty = true; }
                if (GUI.Button(new Rect(W - 448, H - 100, 130, 56), cap.FullRange ? "Full range" : "Limited")) { cap.FullRange = !cap.FullRange; dirty = true; }
                if (GUI.Button(new Rect(W - 312, H - 100, 150, 56), _grade ? "Picture ▲" : "Picture ▼")) _grade = !_grade;
                if (GUI.Button(new Rect(W - 156, H - 100, 148, 56), cap.WantAudio ? "Audio ✓" : "Audio ✗")) cap.WantAudio = !cap.WantAudio;
                if (dirty) cap.SaveGrade();
            }

            if (_grade && cap != null)
            {
                float pw = 560, px = W - pw - 30, py = H - 470;
                GUI.color = new Color(0, 0, 0, 0.82f);
                GUI.DrawTexture(new Rect(px, py, pw, 300), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(px + 20, py + 8, pw, 26), "PICTURE  —  source is dark; lift these", _label);

                bool d = false;
                float g;
                g = Slider(new Rect(px + 20, py + 70, pw - 40, 30), "Exposure / gain", cap.Gain, 0.5f, 6f, _small);
                if (!Mathf.Approximately(g, cap.Gain)) { cap.Gain = g; d = true; }
                g = Slider(new Rect(px + 20, py + 135, pw - 40, 30), "Gamma (mid lift)", cap.Gamma, 0.3f, 3f, _small);
                if (!Mathf.Approximately(g, cap.Gamma)) { cap.Gamma = g; d = true; }
                g = Slider(new Rect(px + 20, py + 200, pw - 40, 30), "Black lift", cap.Lift, 0f, 0.4f, _small);
                if (!Mathf.Approximately(g, cap.Lift)) { cap.Lift = g; d = true; }

                if (GUI.Button(new Rect(px + 20, py + 240, 160, 48), "Auto")) { cap.AutoGrade(); d = true; }
                if (GUI.Button(new Rect(px + 190, py + 240, 160, 48), "Reset")) { cap.Gain = 1; cap.Gamma = 1; cap.Lift = 0; d = true; }
                if (GUI.Button(new Rect(px + 360, py + 240, 180, 48), "Save")) cap.SaveGrade();
                if (d) cap.SaveGrade();
            }
        }
    }
}
