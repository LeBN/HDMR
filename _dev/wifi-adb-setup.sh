#!/usr/bin/env bash
# Run while the Quest is still cabled to the PC. Enables adb over Wi-Fi so the
# capture card can be plugged in while adb stays connected.
set -u
ADB="/c/Program Files/Unity/Hub/Editor/6000.5.10f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"

S=$("$ADB" get-serialno 2>/dev/null)
echo "cabled serial: $S"
IP=$("$ADB" -s "$S" shell ip -f inet addr show wlan0 2>/dev/null | grep -oE 'inet [0-9.]+' | awk '{print $2}')
echo "Quest wlan0 IP: $IP"
[ -z "$IP" ] && { echo "no wlan0 IP — is the Quest on Wi-Fi?"; exit 1; }

"$ADB" -s "$S" tcpip 5555
sleep 3
"$ADB" connect "$IP:5555"
sleep 1
echo "--- adb devices ---"
"$ADB" devices -l
echo
echo "If you see  $IP:5555  device  -> Wi-Fi adb is up."
echo "Now unplug the USB cable and plug the Guermok card. adb stays on $IP:5555."
echo "$IP:5555" > "$(dirname "$0")/wifi-target.txt"
