#!/usr/bin/env bash
# Builds a dynamic miniaudio.xcframework (device arm64 + Apple Silicon
# simulator arm64) and drops it into Flower.iOS/Frameworks/ - see
# native/miniaudio/README.md. Dynamic, not static: see
# Flower.iOS.csproj's NativeReference comment for why (P/Invoke-only
# symbol references get dead-stripped out of a static .a on iOS unless
# ForceLoad is set - a dynamic framework exports its full symbol table by
# default and avoids that whole failure class, matching how
# VideoLAN.LibVLC.iOS itself already ships).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NATIVE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
DEPLOYMENT_TARGET=12.2

# Must be named "miniaudio" - Miniaudio-CS's generated bindings use the
# literal DllImport("miniaudio") string, and dotnet/macios's framework
# resolution matches a NativeReference's binary name to that string with no
# custom DllImportResolver needed.
FRAMEWORK_NAME=miniaudio

rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"

build_slice() {
    local sdk="$1"          # iphoneos | iphonesimulator
    local target_triple="$2" # e.g. arm64-apple-ios12.2 / arm64-apple-ios12.2-simulator
    local slice_dir="$3"     # output dir name under $BUILD_DIR

    local sdk_path
    sdk_path="$(xcrun --sdk "$sdk" --show-sdk-path)"

    local out_dir="$BUILD_DIR/$slice_dir/$FRAMEWORK_NAME.framework"
    mkdir -p "$out_dir"

    echo "=== Building $FRAMEWORK_NAME for $target_triple ($sdk) ==="
    # -x objective-c, not plain C: miniaudio.h's Apple/mobile Core Audio
    # backend #includes <AVFoundation/AVFoundation.h> and uses
    # __has_feature(objc_arc) - it requires an Objective-C compiler
    # front-end on iOS specifically (AVAudioSession's category/route APIs
    # are Objective-C), even though impl.c is a plain .c file.
    xcrun clang \
        -x objective-c \
        -fobjc-arc \
        -dynamiclib \
        -target "$target_triple" \
        -isysroot "$sdk_path" \
        -I "$NATIVE_DIR/vendor" \
        -O2 \
        -install_name "@rpath/$FRAMEWORK_NAME.framework/$FRAMEWORK_NAME" \
        -compatibility_version 1.0 -current_version 1.0 \
        -framework Foundation \
        -framework AVFAudio \
        -framework AudioToolbox \
        -framework CoreAudio \
        -o "$out_dir/$FRAMEWORK_NAME" \
        "$NATIVE_DIR/impl.c"

    cat > "$out_dir/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>$FRAMEWORK_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>com.yanos.flower.native.miniaudio</string>
    <key>CFBundleName</key>
    <string>$FRAMEWORK_NAME</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleShortVersionString</key>
    <string>0.11.22</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>MinimumOSVersion</key>
    <string>$DEPLOYMENT_TARGET</string>
    <key>CFBundleSupportedPlatforms</key>
    <array><string>$([ "$sdk" = "iphoneos" ] && echo iPhoneOS || echo iPhoneSimulator)</string></array>
</dict>
</plist>
PLIST

    # Ad-hoc sign so the simulator (and a device build re-signed at app
    # packaging time) accepts it - simulator doesn't enforce a real
    # identity, but an entirely unsigned framework can still fail to load.
    codesign --force --sign - "$out_dir"

    echo "-> $out_dir"
}

build_slice iphoneos "arm64-apple-ios${DEPLOYMENT_TARGET}" ios-device
# Apple Silicon simulator only for v1 - matches Flower.iOS.csproj's own
# commented-out RuntimeIdentifier toggle (iossimulator-arm64), not an
# Intel-simulator build.
build_slice iphonesimulator "arm64-apple-ios${DEPLOYMENT_TARGET}-simulator" ios-simulator

# Plain per-platform framework folders (ios-device/, ios-simulator/), not a
# combined .xcframework: confirmed via a real build that dotnet-ios's
# <NativeReference> here silently doesn't embed a plain .xcframework
# Include (the item resolves in the item list but nothing gets copied into
# the app bundle - no error, no warning). VideoLAN.LibVLC.iOS - proven
# working in this exact app - ships the same ios-device/ + ios-simulator/
# folder-pair shape instead, conditioned in Flower.iOS.csproj on
# $(Platform)/$(RuntimeIdentifier), which is what we mirror here.
FRAMEWORKS_OUT="$REPO_ROOT/Flower.iOS/Frameworks"
# Only this framework, not the slice directories that hold it. They used to be
# removed wholesale, which was harmless while miniaudio was the only thing in
# them and silently deleted flower_ffmpeg.framework the first time it was not.
for slice in ios-device ios-simulator; do
    mkdir -p "$FRAMEWORKS_OUT/$slice"
    rm -rf "$FRAMEWORKS_OUT/$slice/$FRAMEWORK_NAME.framework"
    cp -R "$BUILD_DIR/$slice/$FRAMEWORK_NAME.framework" "$FRAMEWORKS_OUT/$slice/"
done

echo "Done. -> $FRAMEWORKS_OUT/ios-device/$FRAMEWORK_NAME.framework"
echo "     -> $FRAMEWORKS_OUT/ios-simulator/$FRAMEWORK_NAME.framework"
