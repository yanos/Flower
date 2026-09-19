#!/bin/bash
# Builds, installs, and launches Flower.iOS on a connected physical device.
#
# Always cleans Flower.iOS/obj+bin and Flower/obj+bin before building. This
# project's iOS builds have repeatedly hit a stale-incremental-build crash on
# launch (Mono SIGABRT during its own AOT module load, "Managed Stacktrace:
# at <unknown> <0xffffffff>") that a clean rebuild always resolves and a
# plain incremental `dotnet build` does not reliably avoid - the .NET-for-iOS
# AOT pipeline doesn't always correctly invalidate previously-compiled native
# code when the IL it was built from changes. There is no known MSBuild flag
# that fixes this without a clean; doing it here, always, removes the class
# of bug entirely instead of relying on remembering to do it by hand.
#
# The build is Release (the app needs to run without a debugger attached), but
# with LLVM off - the .NET-for-iOS SDK turns MtouchUseLlvm on for every iOS
# Release build, which sends all ~107 trimmed assemblies through opt+llc and is
# where essentially the whole build goes: measured here, 4m25s total with it
# and 46s without, of which _AOTCompile was 244s and 24s. Nothing else in the
# build is close (ILLink, the next largest, is ~12s either way). The trimming,
# the AOT and every code path are the same; the native code is just optimized
# less well, which is a trade worth making for a build whose whole purpose is
# to be looked at on a device in a minute rather than five. Pass --optimized
# for the slow one when what you are measuring IS the performance, or when you
# want the artifact Apple would actually receive.
#
# --no-build skips both the clean and the build and deploys whatever is
# already in bin/. That is for reinstalling or relaunching the app you just
# built - a crash to look at again, a device that was unplugged - not for
# picking up a source change, which is exactly the case the clean above
# exists for.
#
# Usage: deploy.sh [--no-build] [--optimized] [device-id]
set -euo pipefail
cd "$(dirname "$0")/.."

BUILD=1
LLVM=0
DEVICE_ID=""

while [ $# -gt 0 ]; do
  case "$1" in
    --no-build)
      BUILD=0
      ;;
    --optimized)
      LLVM=1
      ;;
    -h|--help)
      echo "Usage: $(basename "$0") [--no-build] [--optimized] [device-id]"
      exit 0
      ;;
    -*)
      echo "Unknown option: $1" >&2
      echo "Usage: $(basename "$0") [--no-build] [--optimized] [device-id]" >&2
      exit 1
      ;;
    *)
      DEVICE_ID="$1"
      ;;
  esac
  shift
done

DEVICE_ID="${DEVICE_ID:-C015F3A7-5133-5D6B-9DBF-F6E85FC2A230}"
BUNDLE_ID="com.yanos.flower"
APP_PATH="Flower.iOS/bin/Release/net10.0-ios26.5/ios-arm64/Flower.iOS.app"

if [ "$BUILD" -eq 1 ]; then
  echo "==> Cleaning obj/bin (see this script's header comment for why)"
  rm -rf Flower.iOS/obj Flower.iOS/bin Flower/obj Flower/bin

  if [ "$LLVM" -eq 1 ]; then
    echo "==> Building Release, LLVM on (several minutes - see the header)"
  else
    echo "==> Building Release, LLVM off (--optimized for the shipping one)"
  fi
  # No DEVELOPER_DIR pin: the iOS SDK pack this TFM selects checks the
  # selected Xcode's major.minor for *equality* and says which one it wanted,
  # so a wrong `xcode-select` fails here with a better message than a path
  # hardcoded to whichever Xcode happened to be installed the day this was
  # written.
  dotnet build Flower.iOS/Flower.iOS.csproj \
    -c Release -p:MtouchUseLlvm=$([ "$LLVM" -eq 1 ] && echo true || echo false)
else
  echo "==> Skipping clean and build (--no-build)"
  if [ ! -d "$APP_PATH" ]; then
    echo "No app at $APP_PATH - run without --no-build first." >&2
    exit 1
  fi
fi

echo "==> Installing to device $DEVICE_ID"
xcrun devicectl device install app \
  --device "$DEVICE_ID" "$APP_PATH"

echo "==> Launching $BUNDLE_ID"
xcrun devicectl device process launch \
  --device "$DEVICE_ID" --console "$BUNDLE_ID"
