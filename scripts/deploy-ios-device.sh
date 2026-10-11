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
# The app is published with NativeAOT: the whole app trimmed and compiled
# ahead of time by .NET's own compiler, running on .NET's own runtime and
# garbage collector rather than Mono's - the runtime that made scrolling smooth
# on a phone, and so the one worth looking at. It is a publish, not a build,
# because the SDK only switches runtimes on publish (see FlowerNativeAot in
# Flower.iOS.csproj), and it is always optimized. The .app lands where a build
# puts it.
#
# --mono builds for Mono instead, which is what a plain `dotnet build` of
# Flower.iOS still produces. Release (the app needs to run without a debugger
# attached), but with LLVM off - the .NET-for-iOS SDK turns MtouchUseLlvm on
# for every iOS Release build, which sends all ~107 trimmed assemblies through
# opt+llc and is where essentially the whole build goes: measured here, 4m25s
# total with it and 46s without, of which _AOTCompile was 244s and 24s.
# Nothing else in the build is close (ILLink, the next largest, is ~12s either
# way). --optimized is the Mono build with LLVM on, for measuring Mono itself.
#
# --no-build skips both the clean and the build and deploys whatever is
# already in bin/. That is for reinstalling or relaunching the app you just
# built - a crash to look at again, a device that was unplugged - not for
# picking up a source change, which is exactly the case the clean above
# exists for.
#
# The connected device is found automatically; the optional device-id is for
# when more than one is plugged in.
#
# Usage: scripts/deploy-ios-device.sh [--no-build] [--mono | --optimized] [device-id]
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/ios-device.sh

BUILD=1
LLVM=0
NATIVE_AOT=1
USAGE="Usage: $(basename "$0") [--no-build] [--mono | --optimized] [device-id]"
DEVICE_ID=""

while [ $# -gt 0 ]; do
  case "$1" in
    --no-build)
      BUILD=0
      ;;
    --mono)
      NATIVE_AOT=0
      ;;
    --optimized)
      NATIVE_AOT=0
      LLVM=1
      ;;
    -h|--help)
      echo "$USAGE"
      exit 0
      ;;
    -*)
      echo "Unknown option: $1" >&2
      echo "$USAGE" >&2
      exit 1
      ;;
    *)
      DEVICE_ID="$1"
      ;;
  esac
  shift
done

# No device id pinned here. A CoreDevice identifier belongs to one phone, so a
# hardcoded default outlives the phone it was written for, and what devicectl
# says when it resolves to an absent one names neither the phone nor the
# staleness - see scripts/lib/ios-device.sh, which has both failures and the
# reasoning. The device is whichever one is plugged in; the argument is only
# for choosing between several.
ios_resolve_device "$DEVICE_ID"
DEVICE_ID="$IOS_DEVICE_ID"

# Before the build, never after. The other thing a new phone needs is to be in
# the provisioning profile, and it is not: the install fails verification with
# 0xe8008012, which is the last step of the run rather than the first, so
# getting a new phone wrong costs the entire build first. This is a no-op when
# the device is already covered, which is every run but the first for a phone.
scripts/register-ios-device.sh "$IOS_DEVICE_UDID"

APP_PATH="Flower.iOS/bin/Release/net10.0-ios27.0/ios-arm64/Flower.iOS.app"

if [ "$BUILD" -eq 1 ]; then
  echo "==> Cleaning obj/bin (see this script's header comment for why)"
  rm -rf Flower.iOS/obj Flower.iOS/bin Flower/obj Flower/bin

  # No DEVELOPER_DIR pin: the iOS SDK pack this TFM selects checks the
  # selected Xcode's major.minor for *equality* and says which one it wanted,
  # so a wrong `xcode-select` fails here with a better message than a path
  # hardcoded to whichever Xcode happened to be installed the day this was
  # written.
  if [ "$NATIVE_AOT" -eq 1 ]; then
    echo "==> Publishing Release with NativeAOT (see the header)"
    dotnet publish Flower.iOS/Flower.iOS.csproj \
      -c Release -r ios-arm64 -p:FlowerNativeAot=true
  else
    if [ "$LLVM" -eq 1 ]; then
      echo "==> Building Release for Mono, LLVM on (several minutes - see the header)"
    else
      echo "==> Building Release for Mono, LLVM off (--optimized for LLVM on)"
    fi
    dotnet build Flower.iOS/Flower.iOS.csproj \
      -c Release -p:MtouchUseLlvm=$([ "$LLVM" -eq 1 ] && echo true || echo false)
  fi
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

echo "==> Launching $FLOWER_BUNDLE_ID"
xcrun devicectl device process launch \
  --device "$DEVICE_ID" --console "$FLOWER_BUNDLE_ID"
