# Third-party notices

This repository's own code (the HDMR app, and the `nuvc` UVC/UAC plugin under
`_dev/nuvc/`) is MIT-licensed — see `LICENSE`. It bundles or builds against
the following third-party components:

## UVC4UnityAndroid (aandusb)
`Assets/Plugins/Android/common-9.16.0.aar`, `Assets/Plugins/Android/uvc-plugin-0.6.0.aar`,
and the C# wrapper under `Assets/UVC4UnityAndroidPlugin/`.

- Author: saki (t_saki@serenegiant.com) / serenegiant
- Source: https://github.com/saki4510t/UVCAndroid
- License: Apache License 2.0

Used by the `master` branch's fallback capture path (`com.natis.hdmr`,
"b21"). The `nuvc` plugin (`com.natis.hdmr.nuvc`) does not depend on it —
it's a from-scratch replacement built directly on libusb.

## libusb
Statically linked into `_dev/nuvc/nuvc/`'s native library. Not vendored in
this repository — `_dev/nuvc/nuvc/src/main/cpp/CMakeLists.txt` builds it from
source fetched separately (see `_dev/nuvc/README.md` if present, or the
`.gitignore` entry for `_dev/nuvc/**/third_party/libusb/`).

- Source: https://github.com/libusb/libusb
- License: GNU Lesser General Public License 2.1 (LGPL-2.1)

Because this project statically links libusb, LGPL-2.1 compliance is met by
this repository shipping the full source of everything linked against it
(the `nuvc` plugin's own code, build scripts, and CMake configuration), so the
libusb portion can be modified and relinked by rebuilding from source.
