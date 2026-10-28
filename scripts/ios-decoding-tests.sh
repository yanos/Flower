#!/bin/bash
# Runs Flower.Tests.Decoding.Scenarios on an iOS Simulator and answers with an exit code.
#
# The same scenarios Flower.Tests.Decoding runs on this machine, run against the real iOS
# runtime instead. That difference is the entire point: every streaming bug so
# far - VLC's mp4 demuxer refusing an unseekable stream, then .NET's mobile
# HttpClientHandler having no synchronous path at all - was invisible to a
# green desktop suite and cost a person listening to a phone and reporting
# that an album played silence.
#
#   scripts/ios-decoding-tests.sh                           # newest iPhone, newest iOS
#   scripts/ios-decoding-tests.sh "iPhone 17 Pro"           # by name
#   IOS_SIMULATOR_RUNTIME=26 scripts/ios-decoding-tests.sh  # newest iPhone on iOS 26
#   scripts/ios-decoding-tests.sh --mono                    # on Mono rather than NativeAOT
#
# On NativeAOT by default, the runtime Flower.iOS ships on (see
# scripts/deploy-ios-device.sh): .NET's own runtime and garbage collector,
# with every assembly trimmed - which is exactly where a P/Invoke that will not
# resolve, or a type the trimmer took away, shows up, and Mono shows neither.
# The SDK compiles NativeAOT only on its publish path and refuses to publish
# for a simulator, so this is a build told it is publishing (_IsPublishing).
# --mono builds for Mono instead.
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

IOS_SIMULATOR_BUILD_ARGS="-p:FlowerNativeAot=true -p:_IsPublishing=true"
SIMULATOR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --mono)
      IOS_SIMULATOR_BUILD_ARGS=""
      ;;
    -*)
      echo "Unknown option: $1" >&2
      echo "Usage: $(basename "$0") [--mono] [simulator name]" >&2
      exit 1
      ;;
    *)
      SIMULATOR="$1"
      ;;
  esac
  shift
done

ios_simulator_boot "$SIMULATOR"

ios_simulator_build Tests/Flower.Tests.Decoding.iOS/Flower.Tests.Decoding.iOS.csproj \
  Tests/Flower.Tests.Decoding.iOS Tests/Flower.Tests.Decoding.Scenarios Flower

# FFmpeg named rather than left to whatever loaded. iOS has a built façade, so
# it going missing here means the framework did not embed or would not load -
# not a fact about the platform - and the visible symptom would be a green run
# of no checks at all. That is not hypothetical: back when there were two
# decoders, the first run with the framework in the bundle reported 70 passed,
# 0 failed, having silently checked the other one only.
export SIMCTL_CHILD_FLOWER_REQUIRE_DECODERS=FFmpeg

ios_simulator_run \
  Tests/Flower.Tests.Decoding.iOS/bin/Release/net10.0-ios27.0/iossimulator-arm64/Flower.Tests.Decoding.iOS.app \
  com.yanos.flower.decodingtests flower-decoding-tests.log 'FLOWER-DECODING ' 180

if ! echo "$IOS_TALLY" | grep -q ', 0 failed'; then
  exit 1
fi
