#!/usr/bin/env bash
# Builds dynamic miniaudio and flowerbridge frameworks (device arm64 + Apple
# Silicon simulator arm64) and drops them into Flower.iOS/Frameworks/ - see
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
# custom DllImportResolver needed. The bridge is its own framework,
# flowerbridge, for NativeAudioBridge's DllImport("flowerbridge"): it includes
# no miniaudio, and is a library of its own on every platform.
FRAMEWORKS=(miniaudio flowerbridge)

rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"

# Info.plist and an ad hoc signature around a binary already built into
# $BUILD_DIR/<slice>/<name>.framework/<name>.
package_framework() {
    local name="$1" sdk="$2" slice_dir="$3" version="$4"
    local out_dir="$BUILD_DIR/$slice_dir/$name.framework"

    cat > "$out_dir/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>$name</string>
    <key>CFBundleIdentifier</key>
    <string>com.yanos.flower.native.$name</string>
    <key>CFBundleName</key>
    <string>$name</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
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

build_slice() {
    local sdk="$1"          # iphoneos | iphonesimulator
    local target_triple="$2" # e.g. arm64-apple-ios12.2 / arm64-apple-ios12.2-simulator
    local slice_dir="$3"     # output dir name under $BUILD_DIR

    local sdk_path
    sdk_path="$(xcrun --sdk "$sdk" --show-sdk-path)"

    mkdir -p "$BUILD_DIR/$slice_dir/miniaudio.framework" "$BUILD_DIR/$slice_dir/flowerbridge.framework"

    echo "=== Building miniaudio for $target_triple ($sdk) ==="
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
        -install_name "@rpath/miniaudio.framework/miniaudio" \
        -compatibility_version 1.0 -current_version 1.0 \
        -framework Foundation \
        -framework AVFAudio \
        -framework AudioToolbox \
        -framework CoreAudio \
        -o "$BUILD_DIR/$slice_dir/miniaudio.framework/miniaudio" \
        "$NATIVE_DIR/impl.c"
    package_framework miniaudio "$sdk" "$slice_dir" 0.11.22

    echo "=== Building flowerbridge for $target_triple ($sdk) ==="
    # Plain C with no miniaudio. -ffp-contract=off and hidden visibility for
    # the reasons native/miniaudio/desktop/build.sh gives.
    xcrun clang \
        -std=c11 -O2 -ffp-contract=off -fvisibility=hidden -Wall -Wextra -pedantic \
        -dynamiclib \
        -target "$target_triple" \
        -isysroot "$sdk_path" \
        -install_name "@rpath/flowerbridge.framework/flowerbridge" \
        -compatibility_version 1.0 -current_version 1.0 \
        -o "$BUILD_DIR/$slice_dir/flowerbridge.framework/flowerbridge" \
        "$NATIVE_DIR/flower_audio_bridge.c"
    package_framework flowerbridge "$sdk" "$slice_dir" 1.0
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
# Only these frameworks, not the slice directories that hold them. They used to
# be removed wholesale, which was harmless while miniaudio was the only thing in
# them and silently deleted ffaudio.framework the first time it was not.
for slice in ios-device ios-simulator; do
    mkdir -p "$FRAMEWORKS_OUT/$slice"
    for name in "${FRAMEWORKS[@]}"; do
        rm -rf "$FRAMEWORKS_OUT/$slice/$name.framework"
        cp -R "$BUILD_DIR/$slice/$name.framework" "$FRAMEWORKS_OUT/$slice/"
        echo "Done. -> $FRAMEWORKS_OUT/$slice/$name.framework"
    done
done

# Which bridge source these were built from; CI fails a tree whose
# flower_audio_bridge.{c,h} no longer match (Tests/README.md, "CI").
(cd "$NATIVE_DIR" && shasum -a 256 flower_audio_bridge.c flower_audio_bridge.h) \
    > "$FRAMEWORKS_OUT/flowerbridge-source.sha256"
