# nuvc — true per-eye stereo (XR build) — implementation plan

*Written 2026-09-07 during the night session. Not executed: it needs package
resolution (network, Unity, flaky in batchmode) and on-device verification,
neither safe to do blind while the Quest is dead. This is the pickup.*

## Why the 2D panel isn't the end state

`node-d`'s nuvc is a **head-locked 2D panel** — one image, both eyes see the
same thing. The `Hdmr/NuvcStereo` stage can *preview* the SBS split
(anaglyph, ghost-overlay, single-eye) but it physically cannot put a
different image in front of each eye. The user's goal — "voir où se
séparent les écrans stéréoscopiques pour les régler et qu'ils tombent en
face des yeux" — needs real per-eye rendering.

The good news: **every slider in the 2D tool maps 1:1 to the XR shader.**
The calibration work already done is the hard part; XR is plumbing.

## Chosen approach: one quad, stereo-instanced shader (no OVROverlay)

Simplest reliable path on Quest + Unity 6:

- OVRCameraRig (Meta XR Core SDK) **or** plain `Camera` + OpenXR — a scene
  with head tracking.
- A single world-space **quad**, parented to the centre-eye anchor, ~2 m
  ahead, sized to a comfortable FOV (start ~1.6 m wide at 2 m ≈ 45°).
- Material `Hdmr/NuvcStereoXR` — a copy of `NuvcStereo` where the frag reads
  `unity_StereoEyeIndex` (0 = L, 1 = R) and calls the existing
  `sampleEye(uv, index == 0 ? -1 : +1)`. The capture `Output` is a plain
  2D RT (not a stereo array) so **no** `UNITY_DECLARE_SCREENSPACE_TEXTURE`
  needed — just:
  ```hlsl
  #pragma multi_compile_instancing
  ...
  UNITY_VERTEX_INPUT_INSTANCE_ID   // in appdata
  UNITY_VERTEX_OUTPUT_STEREO       // in v2f
  // vert: UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
  // frag: UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
  //       float eye = unity_StereoEyeIndex < 0.5 ? -1 : 1;
  ```
- `NuvcCapture` is reused **unchanged** — it already produces `Output`
  (RGB, graded). The XR component just needs the capture running; drop the
  `NuvcUi` OnGUI panel and drive the same `S*` fields from a world-space
  uGUI or a laser-pointer slider panel (or keep OnGUI on a second debug
  quad — Unity renders OnGUI to the overlay in VR only via a RenderTexture,
  so a small world-space canvas is cleaner).

Per-eye offsets, converge, K1/K2, scale, split — all already in the shader.

## Package decision

Two options, pick by risk tolerance:

| | Unity OpenXR only | Meta XR Core SDK |
|---|---|---|
| packages | `com.unity.xr.openxr` + `com.unity.xr.management` + `com.unity.xr.core-utils` | + `com.meta.xr.sdk.core` (pulls the above) |
| rig | plain Camera + `TrackedPoseDriver` | OVRCameraRig / OVRManager |
| 240 Hz / display rate control | limited | full (`OVRManager.displayFrequency`) — matters for the *Régie* work, not strictly here |
| user's stated preference | — | **"utilise leur plugin comme GOFUN"** |
| resolution risk in batchmode | lower (fewer, first-party) | higher (registry + version pins) |

The `6da85d8` commit already added OpenXR **settings assets** (`Assets/XR/*`,
`OpenXR Package Settings.asset` with 3312 lines) but **never added the
packages to `Packages/manifest.json`** — that's why it broke. So step 1 is
literally editing manifest.json.

### Recommended: Meta XR Core SDK, pinned

```jsonc
// Packages/manifest.json  "dependencies"
"com.unity.xr.management": "4.5.0",
"com.unity.xr.openxr": "1.14.3",        // verify latest on Quest at build time
"com.unity.xr.core-utils": "2.5.1",
"com.meta.xr.sdk.core": "74.0.0",       // verify — SDK v74 was current in the
                                        //   OVROverlay ref docs seen this session
```
Then let Unity resolve **once in the Editor GUI** (not batchmode) so you can
watch it. Commit the resulting `manifest.json` + `packages-lock.json`.

## Step-by-step

1. `git checkout -b node-e-xr-stereo` (keep `node-d-xr-stereo` — the working
   2D tool — pristine).
2. Add the packages above to `manifest.json`. Open the Editor, let it
   resolve, fix any prompts. Commit lockfile.
3. Meta XR: run **Project Setup Tool** (`Meta > Tools > Project Setup Tool`),
   `FixAllAsync(Android)`. Then **`GenerateOrUpdateAndroidManifest()`** — the
   `hz-unity-meta-core-sdk` skill flags this as mandatory after any
   OVRProjectConfig change.
4. New scene `HDMRNuvcXR.unity`: OVRCameraRig + a `NuvcRig` GO with
   `NuvcCapture` + a new `NuvcXrPanel` (quad mesh + `Hdmr/NuvcStereoXR`
   material, `cap.Output` → `_MainTex`, `S*` → shader each frame).
5. New shader `Assets/Shaders/NuvcStereoXR.shader` — `NuvcStereo` + the
   instancing macros above; frag picks `eye` from `unity_StereoEyeIndex`.
6. New build script `HDMRBuildNuvcXR.cs` (copy `HDMRBuildNuvc.cs`, swap
   scene + add `NuvcStereoXR` to always-included shaders; **do not** set
   `resizableWindow` — this is an immersive app; set the OpenXR loader +
   Meta feature group; keep `com.natis.hdmr.nuvc.xr` as the package id so
   it installs alongside the 2D one).
7. Build, install, **launch worn** (or fake-worn). Expect: floating panel,
   each eye sees its SBS half.
8. Calibrate with the sliders (see `nuvc-stereo-calibration.md` step list).
   Save. The `nuvc.st.*` prefs are shared with the 2D build's keys, so a
   calibration done in either transfers.

## Known gotchas

- **Passthrough vs black bg**: decide if the panel floats over passthrough
  (nice) or black. Passthrough = `OVRPassthroughLayer` + `OVRManager`
  insight passthrough enabled + manifest regen.
- **Single-pass instanced** is the Quest default; the shader MUST have the
  instancing macros or the right eye renders the left eye's image.
- **`unity_StereoEyeIndex` in URP vs BiRP**: nuvc is Built-in RP
  (`GraphicsDeviceType.OpenGLES3`, no URP asset). BiRP + OpenGLES3 +
  single-pass instanced is fine on Quest; the macros above are the BiRP
  ones (`UnityCG.cginc`).
- **Battery**: an immersive app costs more than the 2D panel. Test on the
  charger.
- Meta XR SDK **v74** was the version referenced in this session's docs
  fetch — verify the current one via `metavr docs search` /
  `meta_docs_search` (MCP server was timing out tonight) before pinning.

## Fallback if XR resolution fights back

The 2D tool (`node-d-xr-stereo`, built & committed this session) is a
complete, shippable calibration aid. If the XR packages won't resolve
cleanly, ship the 2D tool head-locked and revisit XR later — the slider
values are not wasted.
