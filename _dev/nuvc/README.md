# nuvc — from-scratch native UVC/UAC plugin for Quest

Node D. Replaces the closed `libaandusb` (whose ~350 ms hidden buffer was
the project's latency blocker) with a hand-rolled libusb path:
`UsbManager.getFileDescriptor()` → `libusb_wrap_sys_device` → hand-rolled
UVC (descriptor scan, PROBE/COMMIT, bulk IN) → async transfer ring
(drop-to-newest) → AHardwareBuffer/texture.

**Quest 3 has no HW MJPEG decoder** (`/vendor/etc/media_codecs.xml`: H.264 /
HEVC / VP9 / AV1 only). So the strategy is raw NV12 for ≤1080p (zero
decode, the gaming path), the card's onboard H.264 for 4K, libjpeg-turbo as
a universal MJPEG fallback.

## Status

| phase | what | state |
|---|---|---|
| P1  | libusb wrap + UVC negotiation + bulk ring + frame assembly | **done, validated on the card** — 1280×720 NV12, 0 bad frames |
| P1.5| transfer-ring throughput tuning | **done** — solid 60 fps 720p NV12 inside a rendering app |
| P3  | frame delivery API + first picture in the headset | **done** — live game on the panel, `com.natis.hdmr.nuvc` |
| P5  | latency instrumentation | **done** — capture→pull 8–9 ms, USB span ~16 ms; ~24 ms our added latency vs aandusb's 24 ms **+ 350 ms that's now gone** |
| P4  | audio (from-scratch UAC iso, own context/fd/thread) | **iso pipe opens clean, reads 0 bytes** — see `../uac-zero-bytes-analysis.md`. Leading theory: the card gates audio on an active video stream; fix in NuvcCapture (audio starts only after video frames flow) + uac.cpp diagnostics — untested, needs the card |
| —   | MJPEG path (libjpeg-turbo), 1080p/4K mode selection | not started |
| —   | fold nuvc into the real 3-page HDMR UX (or swap it into the b-line) | not started — `com.natis.hdmr.nuvc` is a bare spike scene |

**b21 (aandusb) stays the working fallback** — `HDMRBuildNuvc` is a
parallel build, the b-line is 100 % untouched.

## Build

```sh
cd _dev/nuvc && ./build.sh :nuvc:assembleRelease        # the AAR
cp nuvc/build/outputs/aar/nuvc-release.aar ../../Assets/Plugins/Android/
# then the Unity APK:
Unity.exe -batchmode -quit -nographics -projectPath <HDMR> \
  -buildTarget Android -executeMethod HDMRBuildNuvc.BuildNuvc -logFile <log>
```

`./build.sh` (no args) also builds `:testapp:assembleDebug` — a standalone
`com.natis.nuvctest` that exercises the native lib without Unity.

Toolchain quirks: Unity's bundled Gradle 9.1.0 / AGP 9.0.0 / NDK r27c,
`compileSdk 34` (35 not licensed), `local.properties` needs forward-slash
`sdk.dir`/`cmake.dir`, `ANDROID_STL=c++_shared` + explicit
`target_link_libraries(... c++_shared)`. libusb 1.0.27 under
`nuvc/src/main/cpp/third_party/libusb/` is gitignored — re-fetch from the
GitHub release if missing.

## Files

```
nuvc/src/main/cpp/
  uvc.{h,cpp}      hand-rolled UVC: descriptor scan, PROBE/COMMIT, bulk ring, payload-header frame assembly
  uac.{h,cpp}      from-scratch UAC1.0 iso capture — separate libusb context/fd/thread from video
  nuvc.cpp         video JNI + stats/latency
  uac_jni.cpp      audio JNI
nuvc/src/main/java/com/natis/nuvc/
  Nuvc.java        ensureOpen (permission dance) + latestIntoAddr (newest frame → pinned ptr)
  Uac.java         second UsbDeviceConnection + AudioTrack drain thread
```

Unity side: `Assets/Scripts/Nuvc/NuvcCapture.cs` (bridge, NV12→RGB via
`Assets/Shaders/NV12.shader`), `NuvcUi.cs`, scene `Assets/Scenes/HDMRNuvc.unity`.

## APK history (`_archive/`)

`HDMR-nuvc-p3` first light · `-p5` latency · `-p4b-uac` zero-bytes state ·
`-p4c-uac-diag` descriptor-dump + AC-detach + GET_INTERFACE build.
