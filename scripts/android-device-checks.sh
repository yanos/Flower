#!/bin/bash
# Runs Flower.DeviceChecks on an Android emulator and answers with an exit code.
#
# The Android half of scripts/ios-device-checks.sh, and written to be its twin:
# the same checks Flower.Tests runs on this machine, run against the real
# Android runtime instead. Two platform runs are only worth comparing when the
# only difference between them is the platform, so when they disagree the
# disagreement is the finding.
#
#   scripts/android-device-checks.sh              # first available AVD
#   scripts/android-device-checks.sh flower_test  # by name
#
# The app reports by writing a transcript into its own files directory, which
# this reads back with `run-as`. logcat is a ring buffer shared with the whole
# system, so a chatty emulator can drop lines out of the middle of a long
# transcript, and a run that decoded everything but reported two thirds of its
# tally is indistinguishable from a failing one.
#
# For a physical phone, plug it in and set FLOWER_ANDROID_SERIAL to its serial;
# the app shows the same lines on screen, so a run with no cable attached is
# still readable.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT="Flower.DeviceChecks.Android/Flower.DeviceChecks.Android.csproj"
PACKAGE="com.yanos.flower.devicechecks"
ACTIVITY="$PACKAGE/.MainActivity"
TRANSCRIPT="flower-checks.log"
TIMEOUT_SECONDS=300
BOOT_TIMEOUT_SECONDS=180

SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
ADB="$SDK/platform-tools/adb"
EMULATOR="$SDK/emulator/emulator"

# A serial says an emulator (or a phone) is already up and this script should
# use it rather than start one of its own. That is how CI runs: the emulator
# action owns the lifecycle, and a second `emulator` process fighting it for
# the same AVD lock is a failure with no useful error.
SERIAL="${FLOWER_ANDROID_SERIAL:-}"
STARTED_EMULATOR=

if [ -z "$SERIAL" ]; then
  AVD="${1:-$("$EMULATOR" -list-avds | head -1)}"
  if [ -z "$AVD" ]; then
    echo "==> No AVD to run on. Create one with avdmanager, or set FLOWER_ANDROID_SERIAL."
    exit 1
  fi
  echo "==> Emulator: $AVD"

  # -no-snapshot-load so the run starts from the image rather than from
  # whatever the last one left behind: a saved snapshot can carry an older
  # install of this very package, and reinstalling over it is where a stale
  # native library survives a rebuild.
  "$EMULATOR" -avd "$AVD" -no-window -no-audio -no-boot-anim -no-snapshot-load >/dev/null 2>&1 &
  STARTED_EMULATOR=$!
  trap 'kill '"$STARTED_EMULATOR"' 2>/dev/null || true' EXIT

  echo "==> Waiting for boot"
  "$ADB" wait-for-device
  SERIAL=$("$ADB" devices | awk '/emulator/ {print $1; exit}')
fi

export ANDROID_SERIAL="$SERIAL"
echo "==> Device: $SERIAL"

# wait-for-device returns as soon as adb can talk to it, which is long before
# the framework can start an activity. sys.boot_completed is the one that means
# what this needs.
for _ in $(seq "$BOOT_TIMEOUT_SECONDS"); do
  if [ "$("$ADB" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; then
    break
  fi
  sleep 1
done
if [ "$("$ADB" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" != "1" ]; then
  echo "==> The device never finished booting after ${BOOT_TIMEOUT_SECONDS}s."
  exit 1
fi

echo "==> Building"
BUILD_LOG=$(mktemp)
if ! dotnet build "$PROJECT" -c Debug >"$BUILD_LOG" 2>&1; then
  cat "$BUILD_LOG"
  rm -f "$BUILD_LOG"
  exit 1
fi
# The signed one, specifically: the build drops both next to each other and
# the unsigned APK installs with INSTALL_PARSE_FAILED_NO_CERTIFICATES.
APK=$(find Flower.DeviceChecks.Android/bin/Debug -name "$PACKAGE-Signed.apk" | head -1)
if [ -z "$APK" ]; then
  APK=$(find Flower.DeviceChecks.Android/bin/Debug -name "$PACKAGE.apk" | head -1)
fi
rm -f "$BUILD_LOG"
if [ -z "$APK" ]; then
  echo "==> The build produced no APK."
  exit 1
fi

echo "==> Installing $APK"
# Uninstall first rather than install -r: the ABI slot a native library lands
# in is chosen at install time, and reinstalling over an existing package can
# keep the old lib directory.
"$ADB" uninstall "$PACKAGE" >/dev/null 2>&1 || true
"$ADB" install "$APK" >/dev/null

echo "==> Running"
# FFmpeg named rather than left to whatever loaded. Android has a built façade
# for all three ABIs, so it going missing here means the .so did not package or
# would not load - not a fact about the platform - and the visible symptom
# would otherwise be a green run of no checks at all.
"$ADB" shell am start -n "$ACTIVITY" --es FLOWER_REQUIRE_DECODERS FFmpeg >/dev/null

READ_TRANSCRIPT=("$ADB" shell run-as "$PACKAGE" cat "files/$TRANSCRIPT")
LOG=$(mktemp)
trap 'rm -f "$LOG"; [ -n "$STARTED_EMULATOR" ] && kill "$STARTED_EMULATOR" 2>/dev/null; true' EXIT

for _ in $(seq "$TIMEOUT_SECONDS"); do
  "${READ_TRANSCRIPT[@]}" 2>/dev/null | tr -d '\r' >"$LOG" || true
  if grep -q 'FLOWER-CHECKS ' "$LOG"; then
    break
  fi
  sleep 1
done

"$ADB" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true

if ! grep -q 'FLOWER-CHECKS ' "$LOG"; then
  echo "==> No tally after ${TIMEOUT_SECONDS}s - the run did not finish. What there was:"
  if [ -s "$LOG" ]; then
    cat "$LOG"
  else
    echo "(the app wrote nothing at all)"
    echo "==> logcat, in case it crashed before it could:"
    "$ADB" logcat -d -s FlowerChecks:V AndroidRuntime:E DOTNET:V 2>/dev/null | tail -40
  fi
  exit 1
fi

cat "$LOG"

TALLY=$(grep 'FLOWER-CHECKS ' "$LOG" | tail -1)
if ! echo "$TALLY" | grep -q ', 0 failed'; then
  exit 1
fi
