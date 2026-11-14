#!/usr/bin/env bash
# Builds libminiaudio.so and libflowerbridge.so for every ABI Flower.Android
# targets and drops each straight into Flower.Android/libs/<abi>/ - see
# native/miniaudio/README.md.
set -euo pipefail

: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to an installed NDK, e.g. ~/Library/Android/sdk/ndk/28.2.13676358}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
BUILD_ROOT="$SCRIPT_DIR/build"
API_LEVEL=21

ABIS=("arm64-v8a" "x86_64" "armeabi-v7a")

for ABI in "${ABIS[@]}"; do
    echo "=== Building libminiaudio.so and libflowerbridge.so for $ABI ==="
    ABI_BUILD_DIR="$BUILD_ROOT/$ABI"
    rm -rf "$ABI_BUILD_DIR"
    cmake -S "$SCRIPT_DIR" -B "$ABI_BUILD_DIR" \
        -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" \
        -DANDROID_ABI="$ABI" \
        -DANDROID_PLATFORM="android-$API_LEVEL" \
        -DCMAKE_BUILD_TYPE=Release \
        -G "Unix Makefiles"
    cmake --build "$ABI_BUILD_DIR" --config Release

    DEST="$REPO_ROOT/Flower.Android/libs/$ABI"
    mkdir -p "$DEST"
    for LIB in libminiaudio.so libflowerbridge.so; do
        "$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip" \
            --strip-unneeded -o "$DEST/$LIB" "$ABI_BUILD_DIR/$LIB"
        ELF_INFO="$("$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-readelf" \
            --wide --program-headers --dynamic "$DEST/$LIB")"
        if ! grep -q 'GNU_RELRO' <<< "$ELF_INFO" || ! grep -qE 'BIND_NOW|FLAGS.*NOW' <<< "$ELF_INFO"; then
            echo "error: $ABI/$LIB was built without full RELRO" >&2
            exit 1
        fi
        echo "-> $DEST/$LIB"
    done
done

# Which bridge source these were built from; CI fails a tree whose
# flower_audio_bridge.{c,h} no longer match (Tests/README.md, "CI").
(cd "$SCRIPT_DIR/.." && shasum -a 256 flower_audio_bridge.c flower_audio_bridge.h) \
    > "$REPO_ROOT/Flower.Android/libs/flowerbridge-source.sha256"

echo "Done. Built ABIs: ${ABIS[*]}"
