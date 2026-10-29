#!/bin/bash
# AFIF Phone smoke test on an Android emulator: start, first-run choice, secure network sign-in screen.
# Writes screenshots, logcat and crash dumps to ./shots
PKG=com.afiftech.phone
mkdir -p shots
shot() { sleep 2; adb exec-out screencap -p > "shots/$1.png" 2>/dev/null || true; }
tap_text() { # tap the first element whose text or description matches $1
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1
  adb shell cat /sdcard/ui.xml > shots/ui-last.xml 2>/dev/null
  b=$(python3 - "$1" <<'PY'
import re, sys
x = open("shots/ui-last.xml", encoding="utf-8", errors="ignore").read()
for m in re.finditer(r'<node [^>]*>', x):
    n = m.group(0)
    if re.search(r'(text|content-desc)="[^"]*' + re.escape(sys.argv[1]) + '[^"]*"', n, re.I):
        b = re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', n)
        if b:
            x1, y1, x2, y2 = map(int, b.groups()); print((x1 + x2) // 2, (y1 + y2) // 2); break
PY
)
  if [ -n "$b" ]; then echo "tap '$1' at $b"; adb shell input tap $b; return 0; fi
  echo "no '$1' on screen"; return 1
}

adb install -r AFIF-Phone-x86_64-test.apk
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS || true
adb shell pm grant $PKG android.permission.RECORD_AUDIO || true
adb logcat -c || true
adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1
sleep 20; shot 1-start
# the first-run question may be on any of the first screens
for i in 1 2 3; do tap_text "Not now" && break; adb shell input keyevent KEYCODE_BACK; sleep 3; done
sleep 2; tap_text "Not now" || true   # the "Run in background" question
shot 2-after-choice
tap_text "Messages" && sleep 3 && shot 2b-messages
adb shell am start -n $PKG/com.tailscale.ipn.MainActivity || true
sleep 10; shot 3-secure-network
tap_text "Get Started"; sleep 8; shot 4-get-started
tap_text "Log in" || tap_text "Sign in" || true; sleep 8; shot 5-login
tap_text "OK" || tap_text "Allow" || true; sleep 5; shot 6-after
adb shell pidof $PKG > shots/pid.txt || echo "NOT RUNNING" > shots/pid.txt
adb logcat -d -b crash > shots/crash.txt || true
adb logcat -d -b main -b system | grep -vE "NamespaceManager|Phenotype" > shots/logcat.txt || true
grep -E "ANR in $PKG" -A20 shots/logcat.txt >> shots/crash.txt || true
grep -iE "tailscale|libtailscale|ipn|AFIF|Secure network limited" shots/logcat.txt | grep -E " E | W " | head -200 > shots/tailscale-errors.txt || true
exit 0
