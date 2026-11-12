using UnityEngine;

namespace Hdmr.Home
{
    /// <summary>
    /// The preview panel's top-left resolution chip, per the user's own
    /// paper nav-schema: "dropdown résolution en haut-gauche (liste des
    /// résolutions par jeu, mise à jour en direct)". A real live list needs
    /// the actual capture pipeline (nuvc, a separate app in this project —
    /// see hdmr-project.md) reporting what a plugged game actually
    /// negotiated; nothing here can query that. This is the UI shell —
    /// click cycles through placeholder values so the control at least
    /// reads as real and navigable, not just decorative text.
    /// </summary>
    public class ResolutionChip : MonoBehaviour
    {
        [SerializeField] TMPro.TMP_Text _text;

        // CHANGED 2026-09-17 on direct request ("expose every script's
        // parameters in the Editor"): was `static readonly` (invisible to
        // the Inspector) — now an instance [SerializeField] with the same
        // default list.
        [Tooltip("Placeholder resolutions to cycle through on click (no live capture pipeline is wired into this UI shell — see the class summary).")]
        [SerializeField] string[] _placeholderOptions =
        {
            "1920×1080", "2560×1440", "3840×2160", "1280×720"
        };

        int _index;

        void Awake() => Refresh();

        public void CycleNext()
        {
            if (_placeholderOptions == null || _placeholderOptions.Length == 0) return;
            _index = (_index + 1) % _placeholderOptions.Length;
            Refresh();
        }

        void Refresh()
        {
            if (_text != null && _placeholderOptions != null && _placeholderOptions.Length > 0)
                _text.text = _placeholderOptions[_index] + "  v"; // "v" not the "▾" glyph — Baloo2 SDF lacks it (found live, same class of missing-glyph issue as the status hint's ↵)
        }
    }
}
