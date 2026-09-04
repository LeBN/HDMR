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
        GUIStyle _label, _small;

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
                GUI.color = new Color(0, 0, 0, 0.55f);
                GUI.DrawTexture(new Rect(0, H - 132, W, 132), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(24, H - 126, W, 32),
                    $"nuvc · {link} · {cap.Width}x{cap.Height} {(cap.Fmt == 2 ? "MJPEG" : cap.Fmt == 1 ? "YUY2" : "NV12")}", _label);
                GUI.Label(new Rect(24, H - 90, W, 28),
                    $"video {cap.VideoFps:0.0} fps   ·   displayed {cap.DisplayedFps:0.0} fps   ·   seq {cap.Seq}", _small);
                GUI.Label(new Rect(24, H - 58, W, 28), cap.Status, _small);

                if (GUI.Button(new Rect(W - 430, H - 96, 130, 56), "Flip X")) cap.FlipX = !cap.FlipX;
                if (GUI.Button(new Rect(W - 290, H - 96, 130, 56), "Flip Y")) cap.FlipY = !cap.FlipY;
                if (GUI.Button(new Rect(W - 150, H - 96, 130, 56), cap.FullRange ? "Full" : "Limited")) cap.FullRange = !cap.FullRange;
            }
        }
    }
}
