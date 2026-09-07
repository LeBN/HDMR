using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// XR build: head-locked video quad in front of the camera, drawn with the
    /// stereo-instanced Hdmr/NuvcStereoXR shader so each eye samples its own
    /// half of the SBS capture (un-distorted). Reuses NuvcCapture for the feed
    /// and its S* fields for the calibration params (shared PlayerPrefs with
    /// the 2D build). No OnGUI here — tune with the 2D app or `--es nuvc.*`.
    /// </summary>
    [RequireComponent(typeof(NuvcCapture))]
    public sealed class NuvcXrRig : MonoBehaviour
    {
        public Transform head;          // the XR camera
        public float distance = 2.0f;   // metres in front of the eye
        public float widthDeg = 50f;    // horizontal FOV the panel subtends

        NuvcCapture _cap;
        Material _mat;
        Transform _quad;
        MeshRenderer _mr;

        void Awake()
        {
            _cap = GetComponent<NuvcCapture>();
            if (head == null && Camera.main != null) head = Camera.main.transform;

            var shader = Shader.Find("Hdmr/NuvcStereoXR");
            _mat = shader != null ? new Material(shader) : null;

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "NuvcPanel";
            Destroy(q.GetComponent<Collider>());
            _quad = q.transform;
            _mr = q.GetComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            if (_mat != null) _mr.sharedMaterial = _mat;

            if (head != null)
            {
                _quad.SetParent(head, false);
                LayoutQuad();
            }
        }

        void LayoutQuad()
        {
            float w = 2f * distance * Mathf.Tan(widthDeg * 0.5f * Mathf.Deg2Rad);
            float h = w * 9f / 16f;              // the per-eye image is 16:9
            _quad.localPosition = new Vector3(0f, 0f, distance);
            _quad.localRotation = Quaternion.identity;
            _quad.localScale = new Vector3(w, h, 1f);
        }

        void Update()
        {
            if (_mat == null || _cap == null) return;
            if (head != null && _quad.parent != head) { _quad.SetParent(head, false); LayoutQuad(); }

            var tex = _cap.Output;
            if (tex == null) { _mr.enabled = false; return; }
            _mr.enabled = true;
            _mat.SetTexture("_MainTex", tex);

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
        }
    }
}
