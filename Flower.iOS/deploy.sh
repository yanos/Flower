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
set -euo pipefail
cd "$(dirname "$0")/.."

DEVICE_ID="${1:-C015F3A7-5133-5D6B-9DBF-F6E85FC2A230}"
BUNDLE_ID="com.yanos.flower"
APP_PATH="Flower.iOS/bin/Debug/net10.0-ios26.0/ios-arm64/Flower.iOS.app"

echo "==> Cleaning obj/bin (see this script's header comment for why)"
rm -rf Flower.iOS/obj Flower.iOS/bin Flower/obj Flower/bin

echo "==> Building"
DEVELOPER_DIR=/Applications/Xcode_26.0.app dotnet build Flower.iOS/Flower.iOS.csproj -c Debug

echo "==> Installing to device $DEVICE_ID"
DEVELOPER_DIR=/Applications/Xcode_26.0.app xcrun devicectl device install app \
  --device "$DEVICE_ID" "$APP_PATH"

echo "==> Launching $BUNDLE_ID"
DEVELOPER_DIR=/Applications/Xcode_26.0.app xcrun devicectl device process launch \
  --device "$DEVICE_ID" --console "$BUNDLE_ID"
