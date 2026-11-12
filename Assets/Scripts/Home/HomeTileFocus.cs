using UnityEngine;
using UnityEngine.UI;
using Oculus.Interaction;

namespace Hdmr.Home
{
    /// <summary>
    /// Draws real hover/select visual feedback on a single Home tile button
    /// (Tile_KEY, see HDMRBuildHome.AddTileRow) -- an aqua outline while the
    /// tile is the one currently pointed at, an amber outline while it's
    /// actually pressed/selected. The prototype's own design intent for aqua
    /// is spelled out directly in HDMRBuildHome.cs's AddTileRow comment: "a
    /// moving cursor highlight between focused tiles" -- this is that state
    /// machine, which didn't exist yet when that comment was written.
    ///
    /// The only place this state can come from is PointableCanvasModule's
    /// four STATIC events (WhenSelectableHovered / WhenSelectableUnhovered /
    /// WhenSelected / WhenUnselected -- Runtime/Scripts/Unity/
    /// PointableCanvasModule.cs). There is exactly one PointableCanvasModule
    /// for the whole scene (its own Awake() asserts at most one instance may
    /// exist), so every tile's HomeTileFocus instance receives every OTHER
    /// tile's events too -- every handler below checks identity against
    /// args.Hovered before reacting to anything, not just "an event fired".
    ///
    /// One of those four events can't be identity-checked at all. Read
    /// PointableCanvasModule.HandleSelectableHover directly (the method that
    /// raises WhenSelectableUnhovered): it calls
    /// pointer.SetHoveredSelectable(newHoveredSelectable) FIRST, and only
    /// then builds the event args from pointer.HoveredSelectable. In the
    /// unhover branch newHoveredSelectable is null, so by the time the args
    /// are constructed pointer.HoveredSelectable already reads null too --
    /// WhenSelectableUnhovered's args.Hovered is unconditionally null, for
    /// every tile, every time. Confirmed by reading that exact method body
    /// this pass, not trusted from an earlier pass's notes. The only usable
    /// signal on that event is "something stopped being hovered", so this
    /// component tracks its OWN focus state locally and only reacts while
    /// that local state says WE were the hovered one -- see HandleUnhovered.
    ///
    /// STATUS 2026-09-16 (night cycle 2): drafted and reasoned through from
    /// source only (Unity was unavailable — a Build()/workflow concurrency
    /// crash took the Editor down mid-cycle, see hdmr-project.md). NOT YET
    /// wired into HDMRBuildHome.cs, NOT YET compiled, NOT YET run. Do not
    /// trust this file works until it's been through that — same discipline
    /// as everything else tonight.
    /// </summary>
    // Attach with a single AddComponent line where each tile is actually
    // built -- HDMRBuildHome.AddTileRow, right after
    // `tileGo.name = "Tile_" + key;`:
    //     tileGo.AddComponent(typeof(Hdmr.Home.HomeTileFocus));
    // Everything else here (finding the Button, finding the Background Image
    // to draw the frame on) is self-contained on purpose -- AddTileRow
    // builds every tile from one shared prefab in a loop, so this can't rely
    // on any per-tile wiring the build script doesn't already do for its own
    // reasons.
    public class HomeTileFocus : MonoBehaviour
    {
        // Prototype's real palette -- same source as HDMRBuildHome.cs's
        // ApplyUiTheme/AddTileRow comments (the published "HDMR Home"
        // Artifact's CSS custom properties, read 2026-09-16). Re-typed here
        // rather than shared via a reference so this runtime script keeps no
        // compile-time dependency on the Editor-only HDMRBuildHome class.
        //
        // CHANGED 2026-09-17 on direct request ("expose every script's
        // parameters in the Editor"): these were `static readonly` --
        // correct for a shared design-system color, but static fields are
        // invisible to Unity's Inspector (only instance fields serialize).
        // Now instance [SerializeField]s defaulting to the exact same
        // values, so every tile can still share the design system's colors
        // out of the box, but any tile can also be tuned individually from
        // its own Inspector without touching code.
        [Header("Colors")]
        [Tooltip("Outline color while hovered (prototype's --aqua).")]
        [SerializeField] Color _aquaCursor = new Color32(0x47, 0xc4, 0xcf, 0xFF);
        [Tooltip("Outline color while selected (prototype's --amber).")]
        [SerializeField] Color _amberSelect = new Color32(0xff, 0x9d, 0x4d, 0xFF);

        [Tooltip("Outline effectDistance (px, each axis) while this tile is hovered but not pressed.")]
        [SerializeField] float _hoverOutlineWidth = 6f;

        [Tooltip("Outline effectDistance (px, each axis) while this tile is pressed/selected.")]
        [SerializeField] float _selectOutlineWidth = 9f;

        // ADDED 2026-09-16 — the prototype's "Lévitation" idle animation
        // (IDLE_MODES[4], the DEFAULT idleMode in the prototype's own
        // DEFAULTS object — this was never ported to Unity before; the
        // whole idle-motion system didn't exist here at all). Exact
        // formula read directly from the prototype's startIdle()
        // "Lévitation" branch, not approximated:
        //   const up = (s + 1) / 2;           // 0..1, one cycle per idlePeriod
        //   const y = -lift - up * k * 5;     // CSS: negative Y = up on screen
        // "floats further above the lifted baseline and settles back down
        // to it — never below" (prototype's own comment). Unity's
        // RectTransform.anchoredPosition uses +Y = up (opposite sign
        // convention from CSS translateY), so the ported formula below
        // ADDS instead of SUBtracts — same motion, correct axis direction
        // for this engine, not a different animation.
        [Tooltip("Prototype's --sel-lift: px the tile rises (in anchoredPosition Y) the instant it's selected, before the idle float starts.")]
        [SerializeField] float _selectLiftPx = 9f;
        [Tooltip("Prototype's --idle-amp default (0.6) — generic idle intensity, scaled ×5 for Lévitation same as the source.")]
        [SerializeField] float _idleAmp = 0.6f;
        [Tooltip("Prototype's --idle-period default (1.7s) — one full float-up-and-settle cycle.")]
        [SerializeField] float _idlePeriodSeconds = 1.7f;

        RectTransform _rt;
        Vector2 _restAnchoredPosition;
        float _selectStartTime;

        // ADDED 2026-09-16, UI QA pass: hover/select used to be a hard cut
        // (Outline.enabled flips + effectColor set with no interpolation,
        // 0ms). The prototype's own cursor frame has a real transition
        // (--move-dur: 100ms) and NN/g's guidance puts perceived-responsive
        // UI transitions at ~200-500ms — 0ms reads as a glitch, not a
        // deliberate design choice. Chosen duration (150ms) matches the
        // prototype's own selDur rather than picking an arbitrary value.
        // Only fades IN on entering a new visible state (color/width lerp
        // from whatever it was to the new target) — leaving back to None
        // stays an instant disable, since a fade-out on dismissal reads as
        // sluggish, not smoother, for a cursor-style highlight.
        [Tooltip("Fade duration when the outline appears or changes state, seconds.")]
        [SerializeField] float _transitionDurationSeconds = 0.15f;
        float _transitionStartTime;
        Color _transitionFromColor;
        float _transitionFromWidth;
        Color _transitionToColor;
        float _transitionToWidth;
        bool _transitioning;

        // The Selectable PointableCanvasModule actually reports as
        // args.Hovered: it finds it via
        // ExecuteEvents.GetEventHandler<ISelectHandler>(raycastHitGo), which
        // walks UP from whatever was actually raycast-hit (could be this
        // tile's Icon or label child) to the nearest ancestor implementing
        // ISelectHandler. UnityEngine.UI.Selectable (confirmed by reading
        // Core/Selectable.cs's own class declaration) implements
        // ISelectHandler, and Button derives from Selectable -- so this
        // resolves to the tile's Button GameObject, which per
        // HDMRBuildHome.AddTileRow's own
        // tileGo.GetComponentInChildren<Button>(true) lookup isn't
        // guaranteed to BE tileGo. Cache it once instead of assuming.
        Selectable _selectable;

        // The Graphic the Outline vertex effect rides on. "Background" is
        // the plate Image AddTileRow already colors per-tile
        // (bg.color = plateColor there) -- reusing it means the frame traces
        // the tile's real visible shape for free, no extra child GameObject
        // needed.
        Outline _outline;

        // ADDED 2026-09-16, UI QA pass (§G "effets de lumières" — there was
        // no elevation/shadow system at all before). Prototype's own spec
        // for the select pop, read from reference-pack.md: "l'ombre portée
        // grandit (blur + offset), le tile passe légèrement devant" — the
        // shadow grows on select, reinforcing the "lifted toward the
        // viewer" read. Unity UI's Shadow component has no blur control
        // (a hard-edged offset duplicate, not a true soft shadow) — an
        // honest partial port, not the full effect, flagged here rather
        // than silently presented as equivalent.
        Shadow _shadow;
        [Header("Shadow")]
        [Tooltip("Shadow offset while selected — grows from near-zero to read as 'lifted'.")]
        [SerializeField] Vector2 _selectShadowOffset = new Vector2(3f, -3f);
        [Tooltip("Shadow color/opacity, at rest and while selected.")]
        [SerializeField] Color _tileShadowColor = new Color(0f, 0f, 0f, 0.35f);

        // FOUND 2026-09-16 via an actual rendered comparison against the
        // real prototype (not just its CSS read as text): `.tile` has a
        // CONSTANT `box-shadow: 0 10px 22px -12px rgba(0,0,0,.55)` at
        // rest, every tile, all the time — not just while selected. This
        // component originally only ever showed a shadow during Selected
        // (zero the rest of the time), missing the baseline entirely.
        // CHANGED 2026-09-17: was `static readonly` (invisible to the
        // Inspector, same reasoning as AquaCursor/AmberSelect above) — now
        // an instance [SerializeField] with the same default.
        [Tooltip("Shadow offset at rest, before hover/select (CSS's +10px-down becomes Unity's -Y).")]
        [SerializeField] Vector2 _restShadowOffset = new Vector2(0f, -4f);

        enum FocusState { None, Hovered, Selected }
        FocusState _state = FocusState.None;

        public bool DebugIsOutlineEnabled => _outline != null && _outline.enabled;
        public Color DebugOutlineColor => _outline != null ? _outline.effectColor : default;
        public string DebugStateName => _state.ToString();
        public Vector2 DebugOffsetFromRest => _rt != null ? _rt.anchoredPosition - _restAnchoredPosition : Vector2.zero;
        public bool DebugIsTransitioning => _transitioning;
        public Vector2 DebugShadowDistance => _shadow != null ? _shadow.effectDistance : Vector2.zero;

        void Awake()
        {
            _selectable = GetComponent<Selectable>();
            if (_selectable == null) _selectable = GetComponentInChildren<Selectable>(true);
            if (_selectable == null)
            {
                Debug.LogWarning($"[HomeTileFocus] no Selectable found on/under '{name}' -- " +
                    "nothing for PointableCanvasModule's events to match against, disabling.");
                enabled = false;
                return;
            }

            var background = FindChildByName(transform, "Background");
            var frameGraphic = background != null ? background.GetComponent<Graphic>() : null;
            if (frameGraphic == null)
            {
                // GetComponentInChildren has no guaranteed ordering -- if this
                // ever actually fires (today's AddTileRow prefab always has a
                // real "Background" Image, so it shouldn't), it could silently
                // bind the cursor frame to the wrong child (e.g. the Icon)
                // with no trail explaining why. Log so that's diagnosable.
                frameGraphic = GetComponentInChildren<Graphic>(true);
                if (frameGraphic != null)
                    Debug.LogWarning($"[HomeTileFocus] no 'Background' child with a Graphic found under '{name}' -- " +
                        $"falling back to first Graphic found ('{frameGraphic.name}'), which may not be the intended frame surface.");
            }
            if (frameGraphic == null)
            {
                Debug.LogWarning($"[HomeTileFocus] no Graphic found under '{name}' for the cursor " +
                    "frame's Outline effect to ride on, disabling.");
                enabled = false;
                return;
            }

            _outline = frameGraphic.GetComponent<Outline>();
            if (_outline == null) _outline = frameGraphic.gameObject.AddComponent<Outline>();

            _shadow = frameGraphic.GetComponent<Shadow>();
            if (_shadow == null) _shadow = frameGraphic.gameObject.AddComponent<Shadow>();
            _shadow.useGraphicAlpha = false; // same reasoning as the Outline above -- must stay visible on the transparent/near-transparent profile tiles
            _shadow.effectColor = _tileShadowColor;
            _shadow.effectDistance = _restShadowOffset;

            // Full-strength frame color regardless of the tile's own alpha --
            // matters for the empty-profile tile (AddTileRow sets its
            // Background to Color.clear, alpha 0) and the regular profile
            // tiles (alpha .045): Shadow.ApplyShadowZeroAlloc (Outline's base
            // class, confirmed by reading Shadow.cs directly) multiplies the
            // effect color's own alpha by the source vertex alpha when
            // useGraphicAlpha is true, which would make the cursor frame
            // itself invisible on exactly those tiles.
            _outline.useGraphicAlpha = false;

            _rt = GetComponent<RectTransform>();
            if (_rt != null) _restAnchoredPosition = _rt.anchoredPosition;

            ApplyState(FocusState.None);
        }

        void Update()
        {
            if (_transitioning)
            {
                float t = _transitionDurationSeconds > 0f ? (Time.time - _transitionStartTime) / _transitionDurationSeconds : 1f;
                if (t >= 1f) { t = 1f; _transitioning = false; }
                _outline.effectColor = Color.Lerp(_transitionFromColor, _transitionToColor, t);
                float w = Mathf.Lerp(_transitionFromWidth, _transitionToWidth, t);
                _outline.effectDistance = new Vector2(w, w);
            }

            if (_state != FocusState.Selected || _rt == null) return;
            float phase = (Time.time - _selectStartTime) / _idlePeriodSeconds * Mathf.PI * 2f;
            float s = Mathf.Sin(phase);
            float up = (s + 1f) / 2f; // 0..1, one smooth cycle per idlePeriod — matches prototype exactly
            float y = _selectLiftPx + up * _idleAmp * 5f; // never below the lift baseline, floats further above it
            _rt.anchoredPosition = _restAnchoredPosition + new Vector2(0f, y);

            // Shadow grows/shrinks in sync with the same "up" phase as the
            // float itself — physically coherent (shadow reads as
            // strongest when the tile is at the top of its float, weakest
            // when it settles back toward the lifted baseline), rather
            // than a separate, unrelated timing.
            if (_shadow != null)
            {
                float shadowK = 0.4f + up * 0.6f; // never fully gone while selected (0.4 floor), grows toward 1.0
                _shadow.effectDistance = _selectShadowOffset * shadowK;
            }
        }

        // Starts (or retargets) the color/width fade toward the given
        // target, from whatever the outline is currently showing (mid-fade
        // retargeting reads better than snapping to the old target first).
        void StartTransition(Color toColor, float toWidth)
        {
            _transitionFromColor = _outline.effectColor;
            _transitionFromWidth = _outline.effectDistance.x;
            _transitionToColor = toColor;
            _transitionToWidth = toWidth;
            _transitionStartTime = Time.time;
            _transitioning = true;
        }

        void OnEnable()
        {
            PointableCanvasModule.WhenSelectableHovered += HandleHovered;
            PointableCanvasModule.WhenSelectableUnhovered += HandleUnhovered;
            PointableCanvasModule.WhenSelected += HandleSelected;
            PointableCanvasModule.WhenUnselected += HandleUnselected;
        }

        void OnDisable()
        {
            PointableCanvasModule.WhenSelectableHovered -= HandleHovered;
            PointableCanvasModule.WhenSelectableUnhovered -= HandleUnhovered;
            PointableCanvasModule.WhenSelected -= HandleSelected;
            PointableCanvasModule.WhenUnselected -= HandleUnselected;
            if (_outline != null) _outline.enabled = false;
        }

        // Test/verification hooks — PointableCanvasModule's own hover/select
        // events are C# `event`s, so nothing outside that class can raise
        // them for real (no real controller/hand to generate one here
        // either — see hdmr-project.md's XR Simulator/Operator notes).
        // These call the exact same private handlers a real event would,
        // so driving them exercises the real reactive logic (identity
        // checks, state machine, Outline color/enabled), not a faked
        // visual. Left in as small, honest public surface rather than a
        // one-off reflection hack, since the same need will come back the
        // next time this needs verifying without a headset.
        public void DebugSimulateHover() => HandleHovered(new PointableCanvasEventArgs(null, _selectable.gameObject, false, 0));
        public void DebugSimulateUnhover() => HandleUnhovered(new PointableCanvasEventArgs(null, null, false, 0));
        public void DebugSimulateSelect() => HandleSelected(new PointableCanvasEventArgs(null, _selectable.gameObject, false, 0));
        public void DebugSimulateUnselect(bool stillHovered) => HandleUnselected(new PointableCanvasEventArgs(null, stillHovered ? _selectable.gameObject : null, false, 0));

        void HandleHovered(PointableCanvasEventArgs args)
        {
            if (args.Hovered != _selectable.gameObject) return; // some other tile
            if (_state == FocusState.Selected) return; // select outranks hover -- don't downgrade an already-pressed tile's frame
            ApplyState(FocusState.Hovered);
        }

        // args.Hovered is ALWAYS null on this event (see the class header
        // comment) -- there is no identity to check here. The only thing
        // this can safely mean is "the tile that was hovered just stopped
        // being hovered"; only react if OUR OWN state says that was us. If
        // we're currently None (never hovered, or already downgraded by a
        // select->unselect sequence -- see HandleUnselected) or Selected
        // (still pressed; the eventual Unselect handles clearing that), this
        // event is either not about us or premature, so ignore it.
        void HandleUnhovered(PointableCanvasEventArgs args)
        {
            if (_state != FocusState.Hovered) return;
            ApplyState(FocusState.None);
        }

        void HandleSelected(PointableCanvasEventArgs args)
        {
            if (args.Hovered != _selectable.gameObject) return;
            ApplyState(FocusState.Selected);
        }

        // Unlike Unhovered, WhenUnselected's args.Hovered CAN be non-null:
        // read PointableCanvasModule.HandleSelectablePress directly -- on
        // release it's set to pointer.HoveredSelectable only when that still
        // equals the PointerEventData's selectedObject, i.e. "the pointer
        // let go without ever leaving the tile it pressed". Use that: a
        // plain click-and-release drops back to the aqua HOVER frame, since
        // the pointer is still sitting right there, not all the way to no
        // frame at all. Only a release after the pointer actually left the
        // tile (dragged off, or args.Hovered naming a different object
        // entirely) clears the frame completely.
        void HandleUnselected(PointableCanvasEventArgs args)
        {
            if (_state != FocusState.Selected) return; // not the tile that was pressed -- ignore
            ApplyState(args.Hovered == _selectable.gameObject ? FocusState.Hovered : FocusState.None);
        }

        void ApplyState(FocusState state)
        {
            bool wasSelected = _state == FocusState.Selected;
            bool wasNone = _state == FocusState.None;
            _state = state;
            switch (state)
            {
                case FocusState.None:
                    _transitioning = false;
                    _outline.enabled = false;
                    break;
                case FocusState.Hovered:
                    _outline.enabled = true;
                    if (wasNone)
                    {
                        // Fading in from fully hidden — start from the
                        // target color at zero alpha/width rather than
                        // trusting whatever stale value the disabled
                        // Outline was last left holding.
                        var from = _aquaCursor; from.a = 0f;
                        _transitionFromColor = from;
                        _transitionFromWidth = 0f;
                        _transitionToColor = _aquaCursor;
                        _transitionToWidth = _hoverOutlineWidth;
                        _transitionStartTime = Time.time;
                        _transitioning = true;
                    }
                    else StartTransition(_aquaCursor, _hoverOutlineWidth); // e.g. dropping back from Selected
                    break;
                case FocusState.Selected:
                    _outline.enabled = true;
                    StartTransition(_amberSelect, _selectOutlineWidth);
                    // startIdle(i) in the prototype resets its own phase
                    // clock (t0 = performance.now()) every time selection
                    // begins, not a continuously-running global phase —
                    // matched here with Time.time captured fresh.
                    _selectStartTime = Time.time;
                    break;
            }
            // Leaving Selected (by any path — unhover, unselect, or a
            // different tile taking focus) must snap back to rest, not
            // freeze mid-float — the prototype's cancelAnimationFrame(idleRAF)
            // at the top of startIdle() does the equivalent for the next
            // tile's own loop, but never explicitly resets THIS tile's
            // transform on the way out; leaving Unity's RectTransform
            // sitting at whatever Y the sine happened to be at when
            // deselected would be a real, visible bug the CSS original
            // doesn't have (CSS just stops re-writing the transform, but
            // the LAST written value there is also never mid-air because
            // .tile.selected's own box-shadow/lift baseline is separate
            // from the idle RAF's writes — safest direct port is an
            // explicit snap-to-rest here).
            if (wasSelected && state != FocusState.Selected && _rt != null)
            {
                _rt.anchoredPosition = _restAnchoredPosition;
                if (_shadow != null) _shadow.effectDistance = _restShadowOffset;
            }
        }

        static Transform FindChildByName(Transform root, string targetName)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == targetName) return t;
            return null;
        }
    }
}
