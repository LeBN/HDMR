using UnityEngine;
using UnityEditor;

namespace Hdmr.Editor
{
    // ADDED 2026-09-17 on direct request: "où dois-je changer la couleur et
    // les angles de la backplate du menu ?" -- the answer used to be "in
    // HDMRBuildHome.cs's source", which the user rightly called out as not
    // adaptable work. These are the actual designer-tunable visual tokens
    // (colors, corner radii) HDMRBuildHome.cs used to have hardcoded as C#
    // literals scattered across ~10 call sites. Now they live on this one
    // asset instead -- change them in the Inspector, no code edit, no
    // recompile, then run "HDMR/Rebuild Home Scene Only" to see the result.
    //
    // HDMRBuildHome.cs loads the asset at
    // Assets/Editor/HDMRHomeDesignConfig.asset (creating it with these
    // defaults on first run if it doesn't exist yet) via LoadOrCreate()
    // below, rather than requiring a manual drag-and-drop reference --
    // there's exactly one Home scene, so exactly one config asset makes
    // sense, and a missing-reference build failure would be a worse
    // failure mode than "silently use the documented defaults".
    [CreateAssetMenu(fileName = "HDMRHomeDesignConfig", menuName = "HDMR/Home Design Config")]
    public class HDMRHomeDesignConfig : ScriptableObject
    {
        public const string AssetPath = "Assets/Editor/HDMRHomeDesignConfig.asset";

        [Header("Menu (tile deck) backplate")]
        [Tooltip("--backplate-hi -- gradient top color.")]
        public Color tileBackplateTop = new Color32(0x2c, 0x46, 0x53, 0xFF);
        [Tooltip("--backplate -- gradient bottom color.")]
        public Color tileBackplateBottom = new Color32(0x21, 0x35, 0x3f, 0xFF);
        [Tooltip("--deck-radius -- corner radius in px. Sliced 9-slice, so this stays fixed-size under any resize.")]
        public float tileBackplateRadius = 20f;

        [Header("Preview (capture showcase) backplate")]
        [Tooltip("Flat background color behind the capture feed (--showcase's own background, not the shared deck gradient).")]
        public Color previewBackplateColor = new Color32(0x0c, 0x15, 0x19, 0xFF);
        [Tooltip("--showcase border-radius, in px.")]
        public float previewBackplateRadius = 16f;

        [Header("Hinge")]
        [Tooltip("Same gradient colors as the deck by default (see tileBackplateTop/Bottom) -- kept separate so the hinge can be tuned independently if wanted.")]
        public Color hingeTop = new Color32(0x2c, 0x46, 0x53, 0xFF);
        public Color hingeBottom = new Color32(0x21, 0x35, 0x3f, 0xFF);

        [Header("Accents")]
        [Tooltip("--amber -- select/decide glow, primary-tile icons, the 'Placeholder' badge.")]
        public Color amberAccent = new Color32(0xff, 0x9d, 0x4d, 0xFF);
        [Tooltip("--aqua -- hover cursor highlight (HomeTileFocus's own default, kept in sync here for reference).")]
        public Color aquaAccent = new Color32(0x47, 0xc4, 0xcf, 0xFF);

        [Header("Badge / chip")]
        [Tooltip("Corner radius for the 'Placeholder' badge and the resolution chip, in px.")]
        public float badgeChipRadius = 5f;

        static HDMRHomeDesignConfig _cached;

        public static HDMRHomeDesignConfig LoadOrCreate()
        {
            if (_cached != null) return _cached;
            _cached = AssetDatabase.LoadAssetAtPath<HDMRHomeDesignConfig>(AssetPath);
            if (_cached == null)
            {
                _cached = CreateInstance<HDMRHomeDesignConfig>();
                AssetDatabase.CreateAsset(_cached, AssetPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[HDMRHomeDesignConfig] created default config asset at {AssetPath} -- edit it there instead of in HDMRBuildHome.cs.");
            }
            return _cached;
        }
    }
}
