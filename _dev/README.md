# HDMR dev tooling

## Rebuild the r0.6.0 native .aar (patched: instrumentation + ImageReader-recreate)
    cd _dev/uvcbuild
    export JAVA_HOME="C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/OpenJDK"
    LAUNCHER=$(ls "C:/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/Tools/gradle/lib/gradle-launcher-"*.jar)
    "$JAVA_HOME/bin/java.exe" -cp "$LAUNCHER" org.gradle.launcher.GradleMain --project-dir . --no-daemon --console=plain :uvcplugin:assembleRelease
    cp uvcplugin/build/outputs/aar/uvcplugin-release.aar ../../Assets/Plugins/Android/uvc-plugin-0.6.0.aar
Patched native sources: uvcbuild/uvcplugin/src/main/cpp/unity_uvc_holder.cpp (search "HDMR").
(arm64-v8a prebuilt .a only; other ABIs removed. abiFilters in build.gradle already arm64-only.)

## adb over Wi-Fi (test with capture card plugged)
1. phone hotspot; PC + Quest join it.
2. Quest still cabled: `bash rt.sh` ... actually: `bash wifi-adb-setup.sh` (edit the IP-detect if needed).
   OR manually: adb tcpip 5555 ; adb connect <quest-wlan0-ip>:5555
3. unplug cable, plug card. Edit D=<ip>:5555 at the top of rt.sh.

## rt.sh — drive the app remotely (app polls debug.hdmr.cmd sysprop)
    bash rt.sh install
    bash rt.sh launch
    bash rt.sh cmd start MJPEG 1920 1080     # relaunches app at that mode
    bash rt.sh cmd start MJPEG 3840 2160
    bash rt.sh cmd selftest                  # relaunch-loop, one mode per run
    bash rt.sh log 20                         # HDMRnative + [HDMR] logcat
    bash rt.sh hlog "SELFTEST" 30             # grep hdmr-log.txt
    bash rt.sh shot                           # screenshot -> _archive/

## First thing next session: stop the headset auto-sleeping
    adb shell settings put system screen_off_timeout 2147483647
    npx -y metavr device proximity_set --enable false     # (verify flag)
and keep the Switch awake.
