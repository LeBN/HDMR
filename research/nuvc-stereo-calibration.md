# nuvc — dark source + stereo calibration (night session, 2026-09-07)

## What was on the card tonight

Switch 2 → USB HDMI capture card → Quest 3, streamed by the from-scratch
`nuvc` plugin (`com.natis.hdmr.nuvc`). Video path healthy the whole session:

```
nuvc : fps=60.0  last=1382400 bytes  bad=0          (1280x720 NV12)
Unity: cap→pull 8.3ms (max 15) · usbSpan 16.1ms · seq climbing
nuvc-uac: okPkt climbing, zeroPkt=3, errPkt=0, 48000Hz 2ch     (audio OK)
```

So: capture, transport, NV12→RGB, audio — all working. The problem the
user reported ("je viens de la voir s'afficher, par contre il est très
sombre") is purely a **levels / layout** problem, not a capture problem.

## The darkness — measured, not guessed

Added a Y-plane luma probe (`SampleLuma`, ~4k samples/frame):

```
[nuvc] ... luma 0/18/79
```

min 0, **mean 18, max 79** (out of 255). The source is a *real image* (it
has structure — not `SourceLikelyBlack`, which needs max-min < 8) but it
genuinely never gets brighter than ~31 %. Causes, in likely order:

1. **Switch-2 VR/stereo render mode** puts two barrel-distorted circular
   views on a large black field → most of the frame is black, dragging the
   mean right down.
2. Limited-range (16–235) NV12 decode of what may be a full-range signal
   costs a little more headroom.
3. Dark game content / a menu at the moment of capture.

Even a hard 2/98 per-channel histogram stretch of the metacam screenshot
barely lifts the content — consistent with "small bright region on black",
i.e. cause 1 dominates.

## Fix 1 — picture grade (shipped this session)

`Hdmr/NV12` shader gained `_Gain` / `_Gamma` / `_Lift`, applied after the
BT.709 matrix:

```
rgb = rgb*(1-_Lift) + _Lift;      // raise the floor off 0
rgb = saturate(rgb * _Gain);      // exposure
rgb = pow(rgb, 1/_Gamma);         // midtone lift
```

- `NuvcCapture.Gain/Gamma/Lift`, persisted in PlayerPrefs `nuvc.grade.*`.
- `AutoGrade()` picks values from the live luma stats
  (`Gain ≈ 0.95/max`, small `Lift`, `Gamma 1.4`).
- **One-shot auto-grade**: on first run, after 4 luma samples, if the user
  has never saved a grade and the source never reaches luma 150,
  `AutoGrade()` fires once automatically (logged). A manual Save sets
  `nuvc.grade.userset=1` and suppresses it thereafter.
- UI: "Picture ▼" panel — Exposure / Gamma / Black-lift sliders + Auto /
  Reset / Save. Also a live "source luma a/b/c — live image / LIKELY BLACK"
  status line so the Switch-asleep case is unambiguous on the panel.

Screenshot-side tools live in the scratchpad: `enhance.js` (histogram
stretch / bright / gamma presets) and `cropstretch.js` (crop to the panel
+ hard stretch + 2×).

## Fix 2 — stereo re-layout stage (shipped, untested on device)

New shader `Hdmr/NuvcStereo`, run as a second `Graphics.Blit` after the
NV12 conversion (`NuvcCapture.StereoOutput`, gated by `StereoOn`). All
params persisted in `nuvc.st.*`, all exposed as sliders in the "Stereo ▼"
panel:

| param | slider range | meaning |
|---|---|---|
| `SMode` | button cycle | Full / Left eye / Right eye / SBS fit / Anaglyph / Ghost overlay |
| `SSplitX` | 0.30–0.70 | where L and R halves meet in the source (magenta guide line in Full mode) |
| `SScale` | 0.4–2.5 | zoom per eye |
| `SOffXL/YL`, `SOffXR/YR` | ±0.5 | pan each eye-half so it lands in front of the eye |
| `SConverge` | ±0.3 | symmetric horizontal toe-in |
| `SK1`, `SK2` | ±0.6 / ±0.4 | radial distortion applied to the *sampling* coord — k<0 barrel, k>0 pincushion; inverts (or adds to) the source's baked lens warp |
| `SGrid` | toggle | green 20-cell grid + centre crosshair; split/edge guides |
| `TestPattern` | toggle | synthetic SBS calibration frame (`Hdmr/NuvcTestSBS`) — rings + crosshair + grid per half, right half biased 3 % inward. Calibrate split/converge/offsets with no live feed. |

**Detect / Apply buttons**: `AnalyzeSource()` scans the Y plane —
- central dark seam → `SSplitX`
- per-half content bounding box → `SScale` + per-eye `SOffX`
- a 24×16 point grid, left-half vs right-half → `HalvesDiff`; `< 0.18` ⇒
  "SBS stereo", higher ⇒ "NOT stereo?" and an inline warning if Stereo is
  on (the Switch probably isn't in VR/stereo mode).
`ApplyDetection()` pushes the geometry into the sliders.

**On a 2D panel this can only *preview*** (one eye, SBS-fit, anaglyph,
ghost-overlay for convergence tuning). True per-eye display needs an XR
build — but every slider value found here transfers straight over, so the
calibration work is not wasted.

## Device session 2026-09-07 (45W bank on the card — power solved)

- **Power**: a 45W USB-PD power bank on the capture card's power-in → the
  Quest reads `sink_power=true` **even while hosting the card and streaming**,
  and holds 100% under full load. The earlier adapter was too weak
  (`source_power=true`, drained ~1.5%/min). The battery race is over — with
  a ≥45W bank on the card, testing is continuous.
- **Switch-2 VR-glasses mode confirmed**: clean SBS stereo. Each eye's view
  is **barrel pre-distorted** (for the Switch's own lens) and sits on a
  **black field** — there's a black gutter between the two halves at the
  seam and black around the outer edges. Split ≈ 0.50 (detector wobbles
  0.43–0.50 depending on on-screen content; 0.50 is right for the Zelda
  frames).
- **Measured starting point** (Zelda title screen, via metacam):
  `split 0.50 · scale 1.22 · k1 0.20 · k2 0.05 · conv 0 · offsets 0`
  → fills the eye, flattens most of the baked barrel. Now the
  `LoadStereo`/`ResetStereo` defaults.
- **Auto-grade** fires on the real feed: `luma ~10/28/72` → `gain ~3.3`.
- Screenshots: `scratchpad/c2-*.jpg` (full SBS, per-eye raw vs corrected,
  ghost).

### Still needs the user, worn

A metacam photo is *through the Quest lens at an angle* — it can't show
true per-eye perception. The user must, wearing the headset:
- **Convergence** (`SConverge`): on a **static** screen (menu / paused) —
  ghost-overlay mode, dial until the doubled text merges. Zelda's animated
  title screen is useless for this.
- **Fine `k1`/`k2`**: per-eye view — the Quest lens *adds* some barrel back,
  so the worn-correct k1 is likely a bit *less* than the 0.20 that looks
  flat on the 2D panel.
- **Per-eye offset / IPD**: nudge `SOffXL/XR` so each eye's centre sits on
  the pupil.

### Launch hook — must be a COLD start

`am start -S -W -n com.natis.hdmr.nuvc/com.unity3d.player.UnityPlayerGameActivity --es nuvc.<k> <v>`
A warm `am start` delivers `onNewIntent`, which Unity's `Awake` never reads,
so the `--es` extras are silently ignored — always pass `-S`.
Keys: `stereo testpat grid detect autograde` (0/1), `smode` (0-5),
`split scale oxl oyl oxr oyr conv k1 k2` (floats).

## Next (needs the headset + battery)

1. Charge the Quest. Battery went 46 %→1 % in ~55 min streaming
   video+audio+fake-worn — that drain rate is expected, not a bug, but it
   means device testing needs the charger connected (the card can share the
   port via a powered hub, or test in short bursts).
2. Launch nuvc, open "Stereo ▼", turn Stereo ON, Grid ON, View = "Full".
   Read off where the magenta split line sits vs. the actual seam in the
   Switch's SBS output — set `SSplitX` to match.
3. View = "Ghost overlay": adjust `SConverge` / per-eye offsets until a
   near-field feature and a far-field feature both line up sanely.
4. View = "Left eye" / "Right eye": dial `SK1`/`SK2` until straight lines
   in the game render straight (cancelling the baked barrel distortion),
   or deliberately leave some in to match the Quest's own lens profile.
5. Then decide: is the 2D-panel result good enough head-locked, or do we
   commit to the XR per-eye build (Meta XR SDK, `6da85d8` has the infra).

## XR build status

XR infra (`Assets/XR`, `HDMRBuildXR.cs`, OVR settings) is **removed from
the working tree** — it referenced `HdmrXrRig` which broke compilation.
Everything is retrievable from commit `6da85d8`. `Packages/manifest.json`
was never modified (still no Meta XR packages installed).
