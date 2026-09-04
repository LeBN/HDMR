using UnityEngine;
using Serenegiant.UVC;

namespace Hdmr
{
    /// <summary>3-page in-headset UI (flat 2D app -> OnGUI is fine).</summary>
    public class HdmrUi : MonoBehaviour
    {
        public HdmrController ctrl;
        public HdmrQuadDrawer drawer;
        public HdmrSelfTest selfTest;

        Vector2 _modeScroll, _repScroll;
        GUIStyle _title, _body, _btn, _btnBig, _small;
        bool _ready;
        UVCVideoSize _picked;

        void Styles()
        {
            if (_ready) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 30, wordWrap = true, richText = true };
            _title.normal.textColor = Color.white;
            _body = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
            _body.normal.textColor = new Color(.88f, .92f, .96f);
            _small = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            _small.normal.textColor = new Color(.7f, .78f, .85f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 20, fixedHeight = 44 };
            _btnBig = new GUIStyle(GUI.skin.button) { fontSize = 26, fixedHeight = 62, fontStyle = FontStyle.Bold };
            _ready = true;
        }

        void OnGUI()
        {
            Styles();
            switch (ctrl.Page)
            {
                case HdmrPage.NoDevice: DrawNoDevice(); break;
                case HdmrPage.Config: DrawConfig(); break;
                case HdmrPage.Streaming: DrawStreaming(); break;
            }
        }

        // ---------------------------------------------------------- page 1
        void DrawNoDevice()
        {
            float w = Screen.width, h = Screen.height;
            GUI.color = new Color(0.03f, 0.04f, 0.06f); GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var r = new Rect(w * 0.15f, h * 0.32f, w * 0.7f, h * 0.4f);
            GUILayout.BeginArea(r);
            GUILayout.Label("<b>Aucune source HDMI détectée</b>", _title);
            GUILayout.Space(10);
            GUILayout.Label("Branchez la carte de capture (USB-C) avec la console allumée.\n" +
                            "La détection est automatique.", _body);
            GUILayout.Space(20);
            GUILayout.Label(ctrl.DeviceLine, _small);
            if (GUILayout.Button("Recharger", _btn, GUILayout.Width(200))) ctrl.ReloadScene();
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------- page 2
        void DrawConfig()
        {
            float w = Screen.width, h = Screen.height;
            GUI.color = new Color(0.04f, 0.05f, 0.08f); GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUILayout.BeginArea(new Rect(w * 0.08f, 24, w * 0.84f, h - 48));
            GUILayout.Label("<b>Réglages du flux</b>", _title);
            GUILayout.Label(ctrl.DeviceLine, _small);
            GUILayout.Space(8);

            // options row
            GUILayout.BeginHorizontal();
            bool fx = GUILayout.Toggle(HdmrSession.FlipX, "  miroir horizontal", _body, GUILayout.Width(260));
            bool fy = GUILayout.Toggle(HdmrSession.FlipY, "  miroir vertical", _body, GUILayout.Width(240));
            if (fx != HdmrSession.FlipX || fy != HdmrSession.FlipY) ctrl.SetFlip(fx, fy);
            GUILayout.EndHorizontal();
            // son retiré : le chemin UAC du plugin fait planter la boucle USB
            // (SIGSEGV reap_bulk_transfer) ~1s après le start. → plugin natif.

            GUILayout.Space(8);
            GUILayout.Label($"Choisissez un format / résolution  ({ctrl.Sizes.Count} disponibles) :", _body);

            _modeScroll = GUILayout.BeginScrollView(_modeScroll, GUILayout.Height(h * 0.42f));
            foreach (var s in ctrl.Sizes)
            {
                bool sel = _picked != null && s.IsSameValue(_picked);
                bool cur = ctrl.Cam != null && s.IsSameValue(ctrl.Cam.CurrentSize);
                var label = (sel ? "▶ " : "   ") + HdmrController.Describe(s) + (cur ? "   (actuel)" : "");
                if (GUILayout.Button(label, _btn)) _picked = s;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUI.enabled = _picked != null;
            if (GUILayout.Button("▶  Démarrer le flux", _btnBig, GUILayout.Width(360)))
                ctrl.StartStream(_picked);
            GUI.enabled = true;

            if (selfTest != null)
            {
                GUI.enabled = !selfTest.Running && ctrl.Sizes.Count > 0;
                if (GUILayout.Button(selfTest.Running ? "self-test…" : "Self-test (tous les modes)", _btn, GUILayout.Width(320)))
                    selfTest.Run();
                GUI.enabled = true;
            }
            if (GUILayout.Button("Recharger", _btn, GUILayout.Width(160))) ctrl.ReloadScene();
            GUILayout.EndHorizontal();

            if (selfTest != null && selfTest.Running)
                GUILayout.Label(selfTest.Progress + "  (résultats dans hdmr-log.txt [SELFTEST])", _small);

            GUILayout.EndArea();
        }

        // ---------------------------------------------------------- page 3
        bool _overlayOn = true;

        void DrawStreaming()
        {
            float w = Screen.width, h = Screen.height;

            GUI.color = Color.black; GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // whole video, centred, no crop — edges of the stream = edges of the
            // panel (the panel is built 16:9, so a 16:9 stream fills it exactly)
            var tex = drawer != null ? drawer.CurrentTex : null;
            if (tex != null)
            {
                float ta = (float)tex.width / tex.height, pa = w / h;
                float rw = w, rh = h;
                if (ta > pa) rh = w / ta; else rw = h * ta;
                var rect = new Rect((w - rw) * 0.5f, (h - rh) * 0.5f, rw, rh);
                var uv = new Rect(HdmrSession.FlipX ? 1f : 0f, HdmrSession.FlipY ? 1f : 0f,
                                  HdmrSession.FlipX ? -1f : 1f, HdmrSession.FlipY ? -1f : 1f);
                GUI.DrawTextureWithTexCoords(rect, tex, uv);
            }
            else
            {
                GUILayout.BeginArea(new Rect(w * 0.2f, h * 0.45f, w * 0.6f, 80));
                GUILayout.Label("signal perdu — vérifiez la console / la carte", _body);
                GUILayout.EndArea();
            }

            // tap the top-left corner to toggle the debug strip
            if (GUI.Button(new Rect(0, 0, 90, 60), GUIContent.none, GUIStyle.none)) _overlayOn = !_overlayOn;
            if (!_overlayOn) return;

            var box = new Rect(12, 12, 440, 116);
            GUI.color = new Color(0, 0, 0, 0.55f); GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(box.x + 12, box.y + 8, box.width - 24, box.height - 16));
            GUILayout.Label($"{ctrl.CurrentModeLabel}   ·   app {drawer.AppFps:0} fps" +
                            (drawer.AudioPlaying ? "   ·   son" : "   ·   pas de son"), _small);
            GUILayout.Label($"{drawer.LastEvent}", _small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀ Réglages", _btn, GUILayout.Width(150))) ctrl.BackToConfig();
            bool fy = GUILayout.Toggle(HdmrSession.FlipY, "flipV", _small, GUILayout.Width(70));
            bool fx = GUILayout.Toggle(HdmrSession.FlipX, "flipH", _small, GUILayout.Width(70));
            if (fx != HdmrSession.FlipX || fy != HdmrSession.FlipY) ctrl.SetFlip(fx, fy);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

    }
}
