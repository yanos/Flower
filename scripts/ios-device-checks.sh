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
#   scripts/ios-device-checks.sh                           # newest iPhone, newest iOS
#   scripts/ios-device-checks.sh "iPhone 17 Pro"           # by name
#   IOS_SIMULATOR_RUNTIME=26 scripts/ios-device-checks.sh  # newest iPhone on iOS 26
#
# The simulator plumbing - boot, clean build, install, read the transcript -
# is scripts/lib/ios-simulator.sh, shared with scripts/ios-tests.sh.
#
# For a physical device, build with -r ios-arm64 and install it the way
# Flower.iOS/deploy.sh does; the app shows the same lines on screen, so a run
# with no cable attached is still readable.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/ios-simulator.sh

ios_simulator_boot "${1:-}"

ios_simulator_build Tests/Flower.DeviceChecks.iOS/Flower.DeviceChecks.iOS.csproj \
  Tests/Flower.DeviceChecks.iOS Tests/Flower.DeviceChecks Flower

# FFmpeg named rather than left to whatever loaded. iOS has a built façade, so
# it going missing here means the framework did not embed or would not load -
# not a fact about the platform - and the visible symptom would be a green run
# of no checks at all. That is not hypothetical: back when there were two
# decoders, the first run with the framework in the bundle reported 70 passed,
# 0 failed, having silently checked the other one only.
export SIMCTL_CHILD_FLOWER_REQUIRE_DECODERS=FFmpeg

ios_simulator_run \
  Tests/Flower.DeviceChecks.iOS/bin/Release/net10.0-ios26.5/iossimulator-arm64/Flower.DeviceChecks.iOS.app \
  com.yanos.flower.devicechecks flower-checks.log 'FLOWER-CHECKS ' 180

if ! echo "$IOS_TALLY" | grep -q ', 0 failed'; then
  exit 1
fi
