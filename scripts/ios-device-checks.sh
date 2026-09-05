#!/bin/bash
# Runs Flower.DeviceChecks on an iOS Simulator and answers with an exit code.
#
# The same checks Flower.Tests runs on this machine, run against the real iOS
# runtime instead. That difference is the entire point: every streaming bug so
# far - VLC's mp4 demuxer refusing an unseekable stream, then .NET's mobile
# HttpClientHandler having no synchronous path at all - was invisible to a
# green desktop suite and cost a person listening to a phone and reporting
# that an album played silence.
#
#   scripts/ios-device-checks.sh                 # first available simulator
#   scripts/ios-device-checks.sh "iPhone 17 Pro" # by name
#
# The app reports by writing a transcript into its own Documents directory,
# which this reads out of the simulator's data container. Console.WriteLine
# from a .NET iOS app does not reliably reach `simctl launch --console-pty`,
# and a run that decodes perfectly but prints nothing is indistinguishable
# from a hang - see AppDelegate's own remarks.
#
# For a physical device, build with -r ios-arm64 and install it the way
# Flower.iOS/deploy.sh does; the app shows the same lines on screen, so a run
# with no cable attached is still readable.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT="Flower.DeviceChecks.iOS/Flower.DeviceChecks.iOS.csproj"
BUNDLE_ID="com.yanos.flower.devicechecks"
APP="Flower.DeviceChecks.iOS/bin/Debug/net10.0-ios26.0/iossimulator-arm64/Flower.DeviceChecks.iOS.app"
TRANSCRIPT="flower-checks.log"
TIMEOUT_SECONDS=180
DEVICE="${1:-}"

if [ -z "$DEVICE" ]; then
  # Newest runtime last in simctl's output, so the last match is the most
  # current iOS available rather than the oldest still installed.
  DEVICE=$(xcrun simctl list devices available | grep -oE '^\s+iPhone [^(]+' | tail -1 | xargs)
fi

echo "==> Simulator: $DEVICE"

# Booting an already-booted simulator is an error, not a no-op.
xcrun simctl boot "$DEVICE" 2>/dev/null || true
xcrun simctl bootstatus "$DEVICE" -b >/dev/null

# Always from clean, for the reason Flower.iOS/deploy.sh gives at length: an
# incremental iOS build here reliably launches into a Mono AOT crash
# ("Managed Stacktrace: at <unknown> <0xffffffff>") that a clean rebuild
# always fixes. Costs a few minutes; the alternative is a crash that reads
# exactly like a failing check.
echo "==> Cleaning"
rm -rf Flower.DeviceChecks.iOS/obj Flower.DeviceChecks.iOS/bin \
       Flower.DeviceChecks/obj Flower.DeviceChecks/bin \
       Flower/obj Flower/bin

echo "==> Building"
BUILD_LOG=$(mktemp)
trap 'rm -f "$BUILD_LOG"' EXIT
if ! dotnet build "$PROJECT" -c Debug -r iossimulator-arm64 >"$BUILD_LOG" 2>&1; then
  cat "$BUILD_LOG"
  exit 1
fi

echo "==> Installing"
xcrun simctl install "$DEVICE" "$APP"

# The container only exists once the app has been installed, and its path
# changes with every reinstall - so ask for it now rather than remembering one.
CONTAINER=$(xcrun simctl get_app_container "$DEVICE" "$BUNDLE_ID" data)
LOG="$CONTAINER/Documents/$TRANSCRIPT"
rm -f "$LOG"

echo "==> Running"
# FFmpeg named rather than left to whatever loaded. iOS has a built façade, so
# it going missing here means the framework did not embed or would not load -
# not a fact about the platform - and the visible symptom would be a green run
# of no checks at all. That is not hypothetical: back when there were two
# decoders, the first run with the framework in the bundle reported 70 passed,
# 0 failed, having silently checked the other one only.
SIMCTL_CHILD_FLOWER_REQUIRE_DECODERS=FFmpeg \
  xcrun simctl launch "$DEVICE" "$BUNDLE_ID" >/dev/null

for _ in $(seq "$TIMEOUT_SECONDS"); do
  if [ -f "$LOG" ] && grep -q 'FLOWER-CHECKS ' "$LOG"; then
    break
  fi
  sleep 1
done

xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" 2>/dev/null || true

if [ ! -f "$LOG" ] || ! grep -q 'FLOWER-CHECKS ' "$LOG"; then
  echo "==> No tally after ${TIMEOUT_SECONDS}s - the run did not finish. What there was:"
  cat "$LOG" 2>/dev/null || echo "(the app wrote nothing at all)"
  exit 1
fi

cat "$LOG"

TALLY=$(grep 'FLOWER-CHECKS ' "$LOG" | tail -1)
if ! echo "$TALLY" | grep -q ', 0 failed'; then
  exit 1
fi
