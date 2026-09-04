#!/usr/bin/env bash
# Builds libflower_ffmpeg.dylib for macOS against whatever FFmpeg pkg-config
# finds. On a development machine that is MacPorts (/opt/local); a shipping
# build must point PKG_CONFIG_LIBDIR at an LGPL-only FFmpeg instead - MacPorts'
# default is GPL-enabled, which Flower cannot distribute. See ../README.md.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
build="$here/build"

: "${PKG_CONFIG_PATH:=/opt/local/lib/pkgconfig:/usr/local/lib/pkgconfig:/opt/homebrew/lib/pkgconfig}"
export PKG_CONFIG_PATH

cmake -S "$here/.." -B "$build" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="${FLOWER_ARCHS:-arm64}" \
    "$@"
cmake --build "$build" --config Release -j

out="$root/native/ffmpeg/artifacts/macos"
mkdir -p "$out"
cp "$build/libflower_ffmpeg.dylib" "$out/"
echo "built $out/libflower_ffmpeg.dylib"
nm -gU "$out/libflower_ffmpeg.dylib" | grep flower_ || true
