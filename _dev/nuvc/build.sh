#!/usr/bin/env bash
# nuvc build helper — Unity's bundled Gradle 9.1.0 / AGP 9.0.0 / NDK r27c.
# usage: ./build.sh [gradle tasks...]   (default: :nuvc:assembleRelease :testapp:assembleDebug)
set -e
UED="/c/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Data"
AP="$UED/PlaybackEngines/AndroidPlayer"
export JAVA_HOME="$(cygpath -w "$AP/OpenJDK")"
export ANDROID_HOME="$(cygpath -w "$AP/SDK")"
export ANDROID_SDK_ROOT="$ANDROID_HOME"
CLI="$(cygpath -w "$AP/Tools/gradle/lib/gradle-gradle-cli-main-9.1.0.jar")"
HERE="$(cygpath -w "$(cd "$(dirname "$0")" && pwd)")"
TASKS="${*:-:nuvc:assembleRelease :testapp:assembleDebug}"
MSYS_NO_PATHCONV=1 "$AP/OpenJDK/bin/java.exe" -jar "$CLI" \
    --project-dir "$HERE" --no-daemon --console=plain $TASKS
