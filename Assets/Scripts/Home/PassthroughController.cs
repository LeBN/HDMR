using UnityEngine;

namespace Hdmr.Home
{
    /// <summary>
    /// Owns the three things that change together when the user toggles the
    /// "Mode MR" tile: the real Insight Passthrough layer, the center-eye
    /// camera's background (opaque aura vs. transparent-for-passthrough),
    /// and the reference floor plane's visibility. Grouped in one place
    /// instead of scattering the three across separate handlers, since
    /// leaving any one of them out of sync (e.g. floor still visible with
    /// real passthrough on) reads as a visible bug, not three small ones.
    ///
    /// Off (default): opaque aura background, reference floor visible —
    /// same "clearly below the camera" world-lock anchor this project has
    /// used since EnsureScene() switched to EyeLevel tracking (see
    /// HDMRBuildHome.cs).
    /// On: real camera passthrough, aura hidden (camera background made
    /// transparent per Meta's own "Enable based on system recommendation"
    /// sample — CameraClearFlags.SolidColor + Color.clear), reference
    /// floor hidden (the real floor is now visible through passthrough,
    /// our stand-in plane would either double it or clip through it).
    /// </summary>
    public class PassthroughController : MonoBehaviour
    {
        [SerializeField] OVRPassthroughLayer _passthroughLayer;
        [SerializeField] Camera _centerEyeCamera;
        [SerializeField] GameObject _referenceFloor;
        [SerializeField] GameObject _auraBackground;

        // Unity's default (VR) camera background — the "aura" quad is drawn
        // separately, so the camera itself just needs to not paint a solid
        // colour over it; SolidColor + this colour's alpha=0 lets the aura
        // quad (or, before that quad exists, plain black) show through.
        // CHANGED 2026-09-17 on direct request ("expose every script's
        // parameters in the Editor"): was `static readonly` (invisible to
        // the Inspector) — now an instance [SerializeField] with the same
        // default.
        [Tooltip("Camera clear color while passthrough is OFF (the aura quad, or plain black before it exists, shows through).")]
        [SerializeField] Color _auraCameraClear = new Color(0f, 0f, 0f, 1f);

        public bool PassthroughEnabled { get; private set; }

        void Awake()
        {
            // Reflects the authored default (off) even if this component's
            // own enabled state changes before Start would normally run.
            Apply(false);
        }

        public void TogglePassthrough() => SetPassthrough(!PassthroughEnabled);

        public void SetPassthrough(bool enabled)
        {
            PassthroughEnabled = enabled;
            Apply(enabled);
        }

        void Apply(bool enabled)
        {
            if (_passthroughLayer != null) _passthroughLayer.enabled = enabled;

            if (_centerEyeCamera != null)
            {
                _centerEyeCamera.clearFlags = CameraClearFlags.SolidColor;
                _centerEyeCamera.backgroundColor = enabled ? Color.clear : _auraCameraClear;
            }

            if (_referenceFloor != null) _referenceFloor.SetActive(!enabled);
            if (_auraBackground != null) _auraBackground.SetActive(!enabled);
        }
    }
}
