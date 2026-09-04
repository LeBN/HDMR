#!/usr/bin/env bash
# After a standalone test: wait for the Quest on USB, pull all persistent logs
# for both apps (UVC spike + Camera2 probe) + screenshots.
set -u
ADB="/c/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
OUT="/c/Users/user/OneDrive/Documents/Natis/HDMR/_archive/logs-$(date +%m%d-%H%M)"
mkdir -p "$OUT"

echo "waiting for Quest on USB…"
for i in $(seq 1 900); do
  [ "$("$ADB" get-state 2>/dev/null)" = "device" ] && { echo connected; break; }
  sleep 2
done
[ "$("$ADB" get-state 2>/dev/null)" = "device" ] || { echo "no device"; exit 1; }
sleep 1

for PKG in com.natis.hdmr com.natis.hdmr.cam2; do
  echo "=== $PKG ==="
  for f in hdmr-log.txt hdmr-log.txt.1 hdmr-logcat.txt hdmr-logcat.txt.1; do
    "$ADB" shell "cat /sdcard/Android/data/$PKG/files/$f" > "$OUT/${PKG##*.}-$f" 2>/dev/null
    [ -s "$OUT/${PKG##*.}-$f" ] && echo "  pulled $f ($(wc -c < "$OUT/${PKG##*.}-$f") bytes)" || rm -f "$OUT/${PKG##*.}-$f"
  done
  "$ADB" shell dumpsys package $PKG 2>&1 | grep -iE "USB_CAMERA|CAMERA:|granted=" | head > "$OUT/${PKG##*.}-perms.txt"
done

echo
echo "=== dumpsys media.camera (external camera HAL state, best effort) ==="
"$ADB" shell dumpsys media.camera 2>&1 | head -120 | tee "$OUT/dumpsys-media-camera.txt"

echo
echo "=== screenshot ==="
"$ADB" exec-out screencap -p > "$OUT/screen.png" 2>/dev/null && echo "saved screen.png ($(wc -c < "$OUT/screen.png") bytes)"

echo
echo "=== key lines: Resize codes + Camera2 verdict + USB speed ==="
grep -hE "Resize\(|Start\(id|VERDICT|LENS_FACING|getCameraIdList|speed=|EXTERNAL|BEATS|SELFTEST.*(PASS|FAIL|modes delivered)" "$OUT"/*.txt 2>/dev/null | sort -u
echo "=== DONE -> $OUT ==="
