#!/bin/bash
# Runs Flower.Tests on an iOS Simulator and answers with an exit code.
#
# The same suite `dotnet test` runs on this machine, unchanged, run on the iOS
# runtime instead: Mono rather than CoreCLR, no JIT, a sandboxed filesystem,
# and the iOS builds of ffaudio, miniaudio and Skia. The decode checks are part
# of that suite (DeviceChecksTests), so this covers everything
# ios-device-checks.sh does and the rest of the suite besides.
#
#   scripts/ios-tests.sh                   # newest iPhone simulator
#   scripts/ios-tests.sh "iPhone 17 Pro"   # by name
#   FLOWER_TEST_ARGS="-class Flower.Tests.PlaylistTests -verbose" scripts/ios-tests.sh
#
# FLOWER_TEST_ARGS is passed to xunit as extra command-line arguments. -verbose
# names every test as it starts and finishes, which is how a hang gets found:
# the last STARTING line in the transcript is the test that never came back.
#
# MusicListViewGestureTests are left out, in the app rather than here - see
# Tests/Flower.Tests.iOS/AppDelegate.cs for why.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/ios-simulator.sh

ios_simulator_boot "${1:-}"

ios_simulator_build Tests/Flower.Tests.iOS/Flower.Tests.iOS.csproj \
  Tests/Flower.Tests.iOS Tests/Flower.Tests Tests/Flower.DeviceChecks Flower Flower.Core

# Required, as everywhere the decode checks run: without it a façade that will
# not load makes DeviceChecksTests check nothing and pass.
export SIMCTL_CHILD_FLOWER_REQUIRE_DECODERS=FFmpeg
export SIMCTL_CHILD_FLOWER_TEST_ARGS="${FLOWER_TEST_ARGS:-}"

# About two minutes on an Apple Silicon Mac. The ceiling is for a CI runner,
# which is slower, and is there to turn a hang into a failure rather than to
# be reached.
ios_simulator_run \
  Tests/Flower.Tests.iOS/bin/Debug/net10.0-ios26.5/iossimulator-arm64/Flower.Tests.iOS.app \
  com.yanos.flower.tests flower-tests.log 'FLOWER-TESTS ' 1200

if [ "$IOS_TALLY" != "FLOWER-TESTS exit 0" ]; then
  exit 1
fi
