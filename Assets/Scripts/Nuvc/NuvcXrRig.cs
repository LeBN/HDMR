using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// XR build: draws the captured Switch-2 SBS feed as a per-eye screen-space
    /// overlay (Hdmr/NuvcStereoXR — clip-space, no world quad, so the Quest IPD
    /// adds no parallax; the Switch already baked the stereo). Uses NuvcCapture
    /// for the feed and its S* params (shared PlayerPrefs with the 2D build).
    /// Nothing is drawn until the feed is live — no placeholder panel, no HUD
    /// (which was blocking the USB-permission dialog). Status goes to logcat.
    /// </summary>
    [RequireComponent(typeof(NuvcCapture))]
    public sealed class NuvcXrRig : MonoBehaviour
    {
        public Transform head;          // the XR camera (auto-found if null)

        NuvcCapture _cap;
        Material _mat;
        MeshRenderer _mr;
        float _logT;

        void Awake()
        {
            _cap = GetComponent<NuvcCapture>();

            var shader = Shader.Find("Hdmr/NuvcStereoXR");
            _mat = shader != null ? new Material(shader) : null;
            if (_mat == null) Debug.LogError("[NuvcXrRig] Hdmr/NuvcStereoXR shader not found");

            // a quad whose vertex shader ignores the transform (outputs clip
            // space). Parent to the camera and give it huge bounds so it is
            // never frustum-culled.
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "NuvcOverlay";
            var col = q.GetComponent<Collider>(); if (col) Destroy(col);
            _mr = q.GetComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.sharedMaterial = _mat;
            _mr.enabled = false;
            var mf = q.GetComponent<MeshFilter>();
            if (mf != null && mf.mesh != null)
                mf.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);

            var h = head != null ? head : (Camera.main != null ? Camera.main.transform : null);
            if (h != null) q.transform.SetParent(h, false);
            q.transform.localPosition = new Vector3(0f, 0f, 1f);
        }

        void Update()
        {
            if (_mat == null || _cap == null) return;

            var tex = _cap.Output;
            bool live = tex != null && _cap.Width > 0 && !_cap.SourceLikelyBlack;
            _mr.enabled = tex != null && _cap.Width > 0;   // show once frames flow (even if dark)

            if (tex != null && _cap.Width > 0)
            {
                _mat.SetTexture("_MainTex", tex);
                float asp = _cap.Height > 0 ? (_cap.Width * _cap.SSplitX) / _cap.Height : 0.889f;
                _mat.SetFloat("_Aspect", asp);
                _mat.SetFloat("_SplitX", _cap.SSplitX);
                _mat.SetFloat("_Scale", _cap.SScale);
                _mat.SetFloat("_K1", _cap.SK1);
                _mat.SetFloat("_K2", _cap.SK2);
                _mat.SetFloat("_CenXL", _cap.SCenXL);
                _mat.SetFloat("_CenYL", _cap.SCenYL);
                _mat.SetFloat("_CenXR", _cap.SCenXR);
                _mat.SetFloat("_CenYR", _cap.SCenYR);
                _mat.SetFloat("_FillX", _cap.SFillX);
                _mat.SetFloat("_FillY", _cap.SFillY);
                _mat.SetFloat("_FovFrac", _cap.SFovFrac);
                _mat.SetFloat("_EyeShiftX", _cap.SEyeShiftX);
                _mat.SetFloat("_OffXL", _cap.SOffXL);
                _mat.SetFloat("_OffXR", _cap.SOffXR);
                _mat.SetFloat("_OffY", _cap.SOffYL);
                _mat.SetFloat("_Bright", _cap.SBright);
                _mat.SetFloat("_BgGrid", _cap.SGrid ? 1f : 0f);
            }

            if (Time.unscaledTime - _logT > 2f)
            {
                _logT = Time.unscaledTime;
                Debug.Log($"[NuvcXrRig] {_cap.Status} · {_cap.Width}x{_cap.Height} disp {_cap.DisplayedFps:0}fps · " +
                          $"luma {_cap.LumaMin}/{_cap.LumaMean:0}/{_cap.LumaMax}{(live ? "" : " (dark)")} · " +
                          $"k1 {_cap.SK1:0.00} k2 {_cap.SK2:0.00} fov {_cap.SFovFrac:0.00} shiftX {_cap.SEyeShiftX:0.000}");
            }
        }
    }
}
