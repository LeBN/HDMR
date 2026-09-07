using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// XR build: head-locked video quad in front of the camera, drawn with the
    /// stereo-instanced Hdmr/NuvcStereoXR shader so each eye samples its own
    /// half of the SBS capture (un-distorted). Reuses NuvcCapture for the feed
    /// and its S* fields for the calibration params (shared PlayerPrefs with
    /// the 2D build).
    /// </summary>
    [RequireComponent(typeof(NuvcCapture))]
    public sealed class NuvcXrRig : MonoBehaviour
    {
        public Transform head;          // the XR camera (auto-found if null)
        public float distance = 2.0f;   // metres in front of the eye
        public float widthDeg = 50f;    // horizontal FOV the panel subtends

        NuvcCapture _cap;
        Material _mat;
        Transform _quad;
        MeshRenderer _mr;
        bool _placed;
        float _t;

        // exposed so NuvcUi can show a status line
        public string RigStatus { get; private set; } = "starting";

        void Awake()
        {
            _cap = GetComponent<NuvcCapture>();

            var shader = Shader.Find("Hdmr/NuvcStereoXR");
            _mat = shader != null ? new Material(shader) : null;
            if (_mat == null) Debug.LogError("[NuvcXrRig] Hdmr/NuvcStereoXR shader not found — quad will be blank");

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "NuvcPanel";
            var col = q.GetComponent<Collider>(); if (col) Destroy(col);
            _quad = q.transform;
            _mr = q.GetComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            if (_mat != null) _mr.sharedMaterial = _mat;
        }

        Transform FindHead()
        {
            if (head != null) return head;
            if (Camera.main != null) return Camera.main.transform;
            var cams = Camera.allCameras;
            return cams.Length > 0 ? cams[0].transform : null;
        }

        void Place(Transform h)
        {
            float w = 2f * distance * Mathf.Tan(widthDeg * 0.5f * Mathf.Deg2Rad);
            float ht = w * 9f / 16f;                       // the per-eye image is 16:9
            _quad.SetParent(h, false);
            _quad.localPosition = new Vector3(0f, 0f, distance);
            // Unity Quad faces -Z; rotate 180° so its front faces the camera (+Z look).
            _quad.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _quad.localScale = new Vector3(w, ht, 1f);
            _placed = true;
        }

        void Update()
        {
            if (_mat == null || _cap == null) return;

            var h = FindHead();
            if (h == null) { RigStatus = "no camera"; return; }
            if (!_placed || _quad.parent != h) Place(h);

            var tex = _cap.Output;
            _t += Time.unscaledDeltaTime;

            if (tex == null || _cap.Width <= 0)
            {
                // keep the panel visible (grey, gently pulsing) so it's findable
                // and the view isn't "just black" while we wait for the card
                _mr.enabled = true;
                _mat.SetTexture("_MainTex", Texture2D.grayTexture);
                _mat.SetFloat("_SplitX", 0.5f); _mat.SetFloat("_K1", 0f); _mat.SetFloat("_K2", 0f);
                _mat.SetFloat("_Scale", 1f); _mat.SetFloat("_Converge", 0f);
                _mat.SetFloat("_OffXL", 0f); _mat.SetFloat("_OffYL", 0f);
                _mat.SetFloat("_OffXR", 0f); _mat.SetFloat("_OffYR", 0f);
                _mat.SetFloat("_CenXL", 0.5f); _mat.SetFloat("_CenYL", 0.5f);
                _mat.SetFloat("_CenXR", 0.5f); _mat.SetFloat("_CenYR", 0.5f);
                _mat.SetFloat("_FillX", 1f); _mat.SetFloat("_FillY", 1f);
                _mat.SetFloat("_Aspect", 0.5f);
                _mat.SetFloat("_Opacity", 0.35f + 0.15f * Mathf.PingPong(_t, 1f));
                RigStatus = _cap.Status;
                return;
            }

            _mr.enabled = true;
            _mat.SetTexture("_MainTex", tex);
            _mat.SetFloat("_Opacity", 1f);

            float asp = _cap.Height > 0 ? (_cap.Width * _cap.SSplitX) / _cap.Height : 0.889f;
            _mat.SetFloat("_Aspect", asp);
            _mat.SetFloat("_SplitX", _cap.SSplitX);
            _mat.SetFloat("_Scale", _cap.SScale);
            _mat.SetFloat("_OffXL", _cap.SOffXL);
            _mat.SetFloat("_OffYL", _cap.SOffYL);
            _mat.SetFloat("_OffXR", _cap.SOffXR);
            _mat.SetFloat("_OffYR", _cap.SOffYR);
            _mat.SetFloat("_Converge", _cap.SConverge);
            _mat.SetFloat("_K1", _cap.SK1);
            _mat.SetFloat("_K2", _cap.SK2);
            _mat.SetFloat("_CenXL", _cap.SCenXL);
            _mat.SetFloat("_CenYL", _cap.SCenYL);
            _mat.SetFloat("_CenXR", _cap.SCenXR);
            _mat.SetFloat("_CenYR", _cap.SCenYR);
            _mat.SetFloat("_FillX", _cap.SFillX);
            _mat.SetFloat("_FillY", _cap.SFillY);
            _mat.SetFloat("_BgGrid", _cap.SGrid ? 1f : 0f);

            RigStatus = $"{_cap.Width}x{_cap.Height} {_cap.DisplayedFps:0} fps  k1 {_cap.SK1:0.00} k2 {_cap.SK2:0.00}";
        }

        // small screen-space HUD — visible in the VR eye buffer. No fullscreen
        // fill (that would black out the view); just a corner panel.
        GUIStyle _hud;
        public bool ShowHud = true;
        void OnGUI()
        {
            if (!ShowHud || _cap == null) return;
            if (_hud == null)
                _hud = new GUIStyle(GUI.skin.label) { fontSize = 22, normal = { textColor = Color.white }, richText = true };

            float w = 720, h = 150;
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(12, 12, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
            bool live = _cap.Output != null && _cap.Width > 0;
            string ls = _cap.SourceLikelyBlack
                ? "<color=#ff8866>LIKELY BLACK (Switch asleep / no signal)</color>"
                : "<color=#88ff99>live</color>";
            GUI.Label(new Rect(24, 18, w, 30), $"HDMR nuvc XR  ·  {RigStatus}", _hud);
            GUI.Label(new Rect(24, 48, w, 30), $"capture: {_cap.Status}", _hud);
            GUI.Label(new Rect(24, 78, w, 30), $"audio: {_cap.AudioStatus}   ·   luma {_cap.LumaMin}/{_cap.LumaMean:0}/{_cap.LumaMax}  {(live ? ls : "")}", _hud);
            GUI.Label(new Rect(24, 108, w, 30),
                $"k1 {_cap.SK1:0.00} k2 {_cap.SK2:0.00} scale {_cap.SScale:0.00} fill {_cap.SFillX:0.00}/{_cap.SFillY:0.00} conv {_cap.SConverge:0.00} split {_cap.SSplitX:0.000}", _hud);
        }
    }
}
