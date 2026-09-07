# HDMR XR — faithful stereo reproduction of the Switch-2 VR kit

## What the Switch actually produces

The Switch-2 "Lunette VR" mode renders a **side-by-side pair** and bakes the
Labo-VR lens pre-distortion into it:

```
   left eye render                 right eye render
   (rectilinear, FOV θ_S,          (rectilinear, FOV θ_S,
    camera at  -IPD_S/2)            camera at  +IPD_S/2)
        │                                │
        ▼  bake Labo lens pre-warp       ▼
   ┌──────────┐ ┌──────────┐   ← this is what the capture card sees (SBS frame)
   │  ╭────╮  │ │  ╭────╮   │      each half = a barrelled image on black
   │  │ L  │  │ │  │ R  │   │
   │  ╰────╯  │ │  ╰────╯   │
   └──────────┘ └──────────┘
```

- `θ_S` = the Switch VR camera's horizontal FOV per eye. Labo-VR is
  Fresnel-lens, "FOV comparable to Rift/Vive" → per-eye horizontal
  **≈ 60–75°** (no official number; treat as a tunable, start ~68°).
- `IPD_S` = the stereo baseline the Switch rendered with. It has a **software
  IPD slider (−20…+20)** in the VR settings — so the disparity is already in
  the pixels and is *user-set*.
- The **parallax / depth is fully baked into the two images.** We must NOT
  add any more.

## The faithful reconstruction (what HDMR should do)

```
capture SBS ──► split at _SplitX ──► per half:
                                     rectify (undo Labo pre-warp:  k1,k2)
                                          │
                                          ▼
                              a rectilinear image of eye E's view,
                              spanning FOV θ_S
                                          │
   for Quest eye E:  paste it into the eye buffer, centred on eye E's
                     OPTICAL AXIS, subtending exactly θ_S, nothing else.
                     (no world quad → no Quest-IPD parallax added)
                                          │
                     Quest compositor applies the Quest lens warp for us.
```

Key point: the image is a **per-eye screen-space overlay**, not a shared
world quad. A shared quad at distance d is seen from each eye's offset
position → adds Quest-IPD parallax on top of the baked parallax → double
vision / wrong depth, and the content drifts nasally so the **outer edge
goes black in each eye** (exactly the bug observed with build ≤4).

## Parameters

| param | meaning | start |
|---|---|---|
| `_SplitX` | where L\|R meet in the SBS source | 0.5 |
| `_K1,_K2` | radial rectification (undo Labo pre-warp) | −0.78 / 0.43 (measured) |
| `_CenX/Y L/R` | per-eye distortion centre in the half | 0.5 |
| `_FovFrac` | θ_S / θ_Quest — fraction of the Quest eye FOV the image fills | ~0.68 |
| `_EyeShiftX` | horizontal nudge of each eye's image on its own axis (residual convergence / IPD trim) — symmetric: L gets −s, R gets +s | 0 |
| `_OffY` | vertical centring | 0 |

`_FovFrac`: Quest 3 per-eye H-FOV ≈ 96°. If θ_S ≈ 65°, `_FovFrac ≈ 0.68`.
Tune by making a known-square object in-game look square and the right
angular size.

`_EyeShiftX` replaces the old `_Converge`: since parallax is baked, the only
legit per-eye X adjustment is a small symmetric shift to (a) trim if
`_SplitX` isn't exactly centred, (b) match your physical IPD to the Switch's
IPD setting. Push both eyes' images outward (away from nose) → more black on
the inner edge; inward → black on the outer edge.

## Resolution

The capture card passes the HDMI signal through. nuvc currently hard-picks
**1280×720 NV12** (`uvc.cpp scan_720p`). Bumping it:
- 1920×1080 NV12 @60 ≈ 187 MB/s — fits USB-3 (~625 MB/s). ✓ "2K-ish"
- 2560×1440 NV12 @60 ≈ 332 MB/s — fits. ✓ true 2K
- 3840×2160 NV12 @60 ≈ 800 MB/s — **exceeds USB-3**; needs MJPEG + a decoder
  (nuvc has no decoder yet) → separate project.

**But**: we only get what the Switch sends. If the Switch-2 VR mode outputs
720p or 1080p, that's the ceiling regardless. Need one run with the full
descriptor dump + `getprop`/EDID to see what modes the card advertises while
the Switch is connected.

Plan: `scan_720p` → `scan_best` (largest raw ≤ a bandwidth budget, else
720p), + log every advertised mode.
