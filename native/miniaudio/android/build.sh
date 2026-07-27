#!/usr/bin/env bash
# Builds libminiaudio.so for every ABI Flower.Android targets and drops each
# straight into Flower.Android/libs/<abi>/ - see native/miniaudio/README.md.
set -euo pipefail

: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to an installed NDK, e.g. ~/Library/Android/sdk/ndk/28.2.13676358}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
BUILD_ROOT="$SCRIPT_DIR/build"
API_LEVEL=21

ABIS=("arm64-v8a" "x86_64" "armeabi-v7a")

for ABI in "${ABIS[@]}"; do
    echo "=== Building libminiaudio.so for $ABI ==="
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
    "$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip" \
        --strip-unneeded -o "$DEST/libminiaudio.so" "$ABI_BUILD_DIR/libminiaudio.so"
    echo "-> $DEST/libminiaudio.so"
done

echo "Done. Built ABIs: ${ABIS[*]}"
