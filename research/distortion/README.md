# nuvc stereo distortion — notes

## The model (`Hdmr/NuvcStereo` sampleEye)

Output pixel `o` (eye-local, centred, in a metric where the source half is
`_Aspect` wide × 1 tall) samples the SBS source at:

    s = c + (o - c)/scale · (1 + k1·r² + k2·r⁴)          r² = |(o-c)/scale|²

`c` = per-eye distortion centre (`_CenX/Y L/R`, default 0.5 = half centre).
`_FillX/_FillY` pre-zoom `o` so the 16:9 game frame (a letterboxed band in
the portrait half) fills the eye. This is the standard Brown-Conrady /
Google-Cardboard polynomial — the same `1 + k1 r² + k2 r⁴` Cardboard uses.

## Reference — the Switch VR headset is Cardboard / Labo-VR class

Cardboard's `DISTORTION_COEFFS` (forward, i.e. the pincushion prewarp baked
into what the device renders):

    Cardboard v1:  k1 ≈ 0.34–0.44,  k2 ≈ 0.55
    Cardboard v2:  k1 ≈ 0.4331,     k2 ≈ -0.0856

Our shader applies the **inverse** (to flatten), so our k1 is negative.
Cardboard's radius is in **tan-angle units** (r = tan of the field angle),
not screen-normalized — so the magnitudes don't transfer directly, only the
model + sign. FOV of the Labo-VR-class lens ≈ 60–70°.

## Measured / tuned (Zelda BOTW Switch 2 Edition, 2026-09-07)

Offline fit of the raw SBS boundary → forward k1 ≈ -0.23 (poor fit, bad
edge data on the seam side). Applying the inverse + scoring straightness →
k1 ≈ -1.0. **In-headset, the user converged on:**

    k1 -0.82   k2 0.6   scale 0.8   fillY 1.75   conv -0.07   centre 0.5

(see `tuned-vals-2026-09-07.txt` for the exact `--es` line). These are now
the in-app defaults.

## Tools

- `distfit2.js <dump.png> L|R` — trace the content↔black boundary of one
  eye's half, fit (centre, k1, k2, extents), draw an overlay.
- `undist2.js <dump.png> L|R <k1> <k2> <fillY> [fillX] [cx] [cy]` — apply the
  correction offline, score edge straightness, write the corrected image.
- get a clean raw frame: `adb shell am start -S -W -n <act> --es nuvc.dump 1`
  → `dump-raw-sbs.png` / `dump-graded-sbs.png` in the app's files dir.
- snapshot the current params: `--es nuvc.dumpvals 1` → `dump-vals.txt`.
