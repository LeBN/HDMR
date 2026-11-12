using UnityEngine;

namespace HdmrNuvc
{
    /// <summary>
    /// XR build: a head-locked video quad (a real virtual screen at
    /// _cap.SXrDist metres, subtending _cap.SXrFov degrees) drawn with the
    /// stereo-instanced Hdmr/NuvcStereoXR shader — each eye samples its own
    /// half of the SBS capture, un-distorted, with the S* params shared with
    /// the 2D build (PlayerPrefs). No HUD, no placeholder — nothing shows
    /// until the feed is live; status goes to logcat.
    /// </summary>
    [RequireComponent(typeof(NuvcCapture))]
    public sealed class NuvcXrRig : MonoBehaviour
    {
        public Transform head;          // the XR camera (auto-found if null)

        NuvcCapture _cap;
        Material _mat;
        Transform _quad;
        MeshRenderer _mr;
        float _logT, _dist, _fov;

        void Awake()
        {
            _cap = GetComponent<NuvcCapture>();
            var shader = Shader.Find("Hdmr/NuvcStereoXR");
            _mat = shader != null ? new Material(shader) : null;
            if (_mat == null) Debug.LogError("[NuvcXrRig] Hdmr/NuvcStereoXR shader not found");

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "NuvcPanel";
            var col = q.GetComponent<Collider>(); if (col) Destroy(col);
            _quad = q.transform;
            _mr = q.GetComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.sharedMaterial = _mat;
            _mr.enabled = false;
        }

        Transform FindHead()
        {
            if (head != null) return head;
            if (Camera.main != null) return Camera.main.transform;
            var cams = Camera.allCameras;
            return cams.Length > 0 ? cams[0].transform : null;
        }

        void Layout(Transform h, float dist, float fovDeg)
        {
            float w = 2f * dist * Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            float ht = w * 9f / 16f;                       // per-eye image is 16:9
            if (_quad.parent != h) _quad.SetParent(h, false);
            _quad.localPosition = new Vector3(0f, 0f, dist);
            _quad.localRotation = Quaternion.Euler(0f, 180f, 0f);  // Quad faces -Z
            _quad.localScale = new Vector3(w, ht, 1f);
            _dist = dist; _fov = fovDeg;
        }

        void Update()
        {
            if (_mat == null || _cap == null) return;

            var h = FindHead();
            if (h == null) return;
            if (_quad.parent != h ||
                !Mathf.Approximately(_dist, _cap.SXrDist) ||
                !Mathf.Approximately(_fov, _cap.SXrFov))
                Layout(h, _cap.SXrDist, _cap.SXrFov);

            var tex = _cap.Output;
            bool live = tex != null && _cap.Width > 0;
            _mr.enabled = live;
            if (!live)
            {
                Beat();
                return;
            }

            _mat.SetTexture("_MainTex", tex);
            _mat.SetFloat("_Opacity", 1f);
            _mat.SetFloat("_Bright", _cap.SBright);
            float asp = _cap.Height > 0 ? (_cap.Width * _cap.SSplitX) / _cap.Height : 0.889f;
            _mat.SetFloat("_Aspect", asp);
            _mat.SetFloat("_SplitX", _cap.SSplitX);
            _mat.SetFloat("_Scale", _cap.SScale);
            // The SBS halves are pre-distorted symmetrically, so the correction
            // offset the user tuned on the left eye is mirrored onto the right:
            // X inverted (both images shift the same way relative to the seam),
            // Y identical. SOffXR/SOffYR stay as an extra per-eye trim on top.
            _mat.SetFloat("_OffXL", _cap.SOffXL);
            _mat.SetFloat("_OffYL", _cap.SOffYL);
            _mat.SetFloat("_OffXR", _cap.SOffXR - _cap.SOffXL);
            _mat.SetFloat("_OffYR", _cap.SOffYR + _cap.SOffYL);
            _mat.SetFloat("_Converge", _cap.SConverge);
            _mat.SetFloat("_K1", _cap.SK1);
            _mat.SetFloat("_K2", _cap.SK2);
            _mat.SetFloat("_CenXL", _cap.SCenXL);
            _mat.SetFloat("_CenYL", _cap.SCenYL);
            _mat.SetFloat("_CenXR", _cap.SCenXR);
            _mat.SetFloat("_CenYR", _cap.SCenYR);
            _mat.SetFloat("_FillX", _cap.SFillX);
            _mat.SetFloat("_FillY", _cap.SFillY);
            // the quad faces the camera via a 180° Y rotation (Layout), which
            // mirrors the texture in X — _FlipX (default on) cancels it.
            _mat.SetFloat("_FlipX", _cap.SXrFlipX ? 1f : 0f);
            _mat.SetFloat("_FlipY", _cap.SXrFlipY ? 1f : 0f);
            _mat.SetFloat("_BgGrid", _cap.SGrid ? 1f : 0f);

            Beat();
        }

        void Beat()
        {
            if (Time.unscaledTime - _logT < 2f) return;
            _logT = Time.unscaledTime;
            Debug.Log($"[NuvcXrRig] {_cap.Status} · {_cap.Width}x{_cap.Height} {_cap.DisplayedFps:0}fps · " +
                      $"luma {_cap.LumaMin}/{_cap.LumaMean:0}/{_cap.LumaMax} · dist {_cap.SXrDist:0.0} fov {_cap.SXrFov:0} · " +
                      $"k1 {_cap.SK1:0.00} k2 {_cap.SK2:0.00} scale {_cap.SScale:0.00} conv {_cap.SConverge:0.00} " +
                      $"fill {_cap.SFillX:0.00}/{_cap.SFillY:0.00}");
        }
    }
}
