#!/usr/bin/env bash
# remote-test helper for wifi adb (reconnects on drop)
ADB="/c/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
D=192.168.104.245:5555
PKG=com.natis.hdmr

a() {  # adb with auto-reconnect
  for i in 1 2 3 4 5; do
    out=$("$ADB" -s $D "$@" 2>&1)
    if echo "$out" | grep -q "device.*not found\|no such device\|closed\|offline"; then
      "$ADB" disconnect $D >/dev/null 2>&1; "$ADB" connect $D >/dev/null 2>&1; sleep 2
    else
      echo "$out"; return 0
    fi
  done
  echo "$out"; return 1
}

cmd() { a shell "setprop debug.hdmr.cmd '$RANDOM:$*'"; }

case "${1:-}" in
  install)
    a install -r -g "/c/Users/user/OneDrive/Documents/Natis/HDMR/Build/HDMR-spike.apk"
    a shell pm grant $PKG android.permission.CAMERA
    a shell pm grant $PKG horizonos.permission.USB_CAMERA
    a shell svc power stayon true
    ;;
  launch)
    a shell "setprop debug.hdmr.cmd '$RANDOM:config'"
    a shell input keyevent KEYCODE_WAKEUP
    a logcat -c >/dev/null
    a shell am start -n $PKG/com.unity3d.player.UnityPlayerGameActivity
    ;;
  cmd) shift; cmd "$@" ;;
  log)   a logcat -d 2>&1 | grep -E "HDMRnative|internal_stop|\[HDMR\]|\[RENDER\]|CRASH|SIGABRT|reap_bulk|switch (Resize|Start|begin|FAILED)" | tail -${2:-15} ;;
  hlog)  a shell "cat /sdcard/Android/data/$PKG/files/hdmr-log.txt" | grep -E "$2" | tail -${3:-15} ;;
  shot)  a exec-out screencap -p > "/c/Users/user/OneDrive/Documents/Natis/HDMR/_archive/shot-$(date +%H%M%S).png" && echo "shot saved" ;;
  alive) a shell "dumpsys activity activities | grep topResumedActivity | head -1" ;;
  *) echo "usage: rt.sh {install|launch|cmd <..>|log [n]|hlog <re> [n]|shot|alive}" ;;
esac
