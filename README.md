# HDMR

Play your Switch 2 inside a Meta Quest 3.

HDMR takes the HDMI output of the console through a USB capture card plugged into the Quest 3, and shows it live in the headset. With the Switch 2 VR mode, the game is shown in stereoscopic 3D.

Personal project, made with Unity 6 and Claude Code (CLI).

## How it works

- **Capture**: a USB capture card (UVC, the same standard as webcams), read directly by the Quest, up to 4K at 60 fps
- **nuvc**: my own UVC/UAC plugin, written from scratch in C++ and Java on top of libusb (`_dev/nuvc/`)
  - up to 4K at 60 fps
  - about 7 ms between the end of a frame and the moment Unity gets it
- **Stereo mode**: the Switch 2 VR image (side by side) is split per eye, with a live inverse of the Nintendo lens distortion to get back a clean 16:9 picture, then shown per eye with OpenXR
- **Fallback path**: the UVC4UnityAndroid plugin
- **Display**: Unity 6, NV12 to RGB shader, video with sound

## What you need

- Meta Quest 3
- A USB HDMI capture card (UVC). Tested with a MacroSilicon "USB3 Video" card
- A Nintendo Switch 2 (or any HDMI source), in VR mode for the stereo
- Unity 6000.5.10f1 with Android build support (arm64)

## Build

1. Open the project in Unity 6000.5.10f1
2. Build for Android (arm64), the build scripts are in `Assets/Editor/` (`HDMRBuildNuvc`, `HDMRBuildNuvcXR` for the stereo mode, `HDMRBuildHome` for the interface)
3. Install the APK on the Quest, plug the card into the Quest and the console into the card, then launch HDMR

To rebuild the `nuvc` plugin, see `_dev/nuvc/build.sh`. The dev tools (adb over Wi-Fi, remote commands, logs) are in `_dev/`.

## Status

- Video and sound: working
- Stereo mode: working
- In progress: a graphical interface

## License

MIT for my code (the HDMR app and `nuvc`), see `LICENSE`.
Third-party parts keep their own license (UVC4UnityAndroid: Apache 2.0, libusb: LGPL-2.1), see `THIRD_PARTY_NOTICES.md`.

Made by Natis Beaurepere - [LinkedIn](https://www.linkedin.com/in/natis-beaurepere)
