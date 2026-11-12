using UnityEngine;

namespace Hdmr.Home
{
    /// <summary>
    /// Lives directly on the tile-deck backplate GameObject so its color,
    /// corner radius and size are tunable right there in the Inspector --
    /// the standard Unity pattern (select the object, edit its component),
    /// not a separate config asset.
    ///
    /// ADDED 2026-09-17, replacing an earlier ScriptableObject-based
    /// approach the user correctly pushed back on. The one real constraint
    /// that shaped this: HDMRBuildHome.EnsureScene() calls
    /// EditorSceneManager.NewScene(...) on every "Rebuild Home Scene Only"
    /// -- the ENTIRE scene is wiped and regenerated from code each time,
    /// which would silently destroy anything set on this component along
    /// with everything else. HDMRBuildHome.Build() reads this component's
    /// values from the CURRENTLY OPEN scene before that wipe happens and
    /// carries them forward onto the freshly-created backplate -- so
    /// edits made here on the object DO survive a rebuild, same as any
    /// other Unity project, just via an explicit read-before-wipe step
    /// this project's "regenerate everything" build script needs and a
    /// normal hand-authored scene wouldn't.
    /// </summary>
    public class HDMRBackplateStyle : MonoBehaviour
    {
        [Header("Colors (--backplate-hi / --backplate)")]
        public Color topColor = new Color32(0x2c, 0x46, 0x53, 0xFF);
        public Color bottomColor = new Color32(0x21, 0x35, 0x3f, 0xFF);

        [Header("Shape")]
        [Tooltip("Corner radius in px (--deck-radius). Sliced (9-slice) sprite -- stays fixed-size under any resize, doesn't squash.")]
        public float cornerRadius = 20f;

        [Header("Size")]
        [Tooltip("Off (default): size is auto-fitted tightly around the tile content every rebuild, like before. On: 'Manual Size' below is used as-is instead, and your own resize survives rebuilds.")]
        public bool useManualSize = false;
        [Tooltip("Only used when 'Use Manual Size' is on.")]
        public Vector2 manualSize = new Vector2(602f, 519f);
    }
}
