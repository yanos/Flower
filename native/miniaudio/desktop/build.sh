#!/usr/bin/env bash
# Builds libflowerbridge - flower_audio_bridge.c on its own - for the six
# desktop RIDs, and drops each into runtimes/<rid>/native/ beside this script,
# where FlowerBridge.props picks it up. Run on a Mac with Docker running
# (colima will do): the macOS pair builds natively, Linux and Windows in
# containers pinned by digest below.
#
# Desktop's miniaudio is the Miniaudio-CS NuGet's, which knows nothing of the
# bridge, so the bridge ships as a library of its own beside it. That works
# because the bridge includes no miniaudio: miniaudio is handed a
# dataCallback pointer into this library and has no opinion about where it
# lives (docs/AUDIO-QUALITY-PLAN.md, Phase 6 steps 5-7).
#
# The flags that matter, on every target:
#   -ffp-contract=off  The output stage is held to OutputStage.cs byte for
#                      byte, and RyuJIT never fuses a multiply-add. The
#                      source's FP_CONTRACT pragma covers clang; GCC ignores
#                      that pragma, and fuses by default on arm64.
#   -fvisibility=hidden  Exports exactly FLOWER_AUDIO_BRIDGE_API.
#   -lm                  rintf, on Linux.
#
# Checked after building: the export list, the dependencies, the glibc floor,
# and that no fused multiply-add survived. Then runtimes/flowerbridge-source.sha256
# records which source these binaries came from, which CI holds the tree to.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NATIVE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT="$SCRIPT_DIR/runtimes"
BUILD="$SCRIPT_DIR/build"

DEBIAN_IMAGE='debian@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc' # bookworm-slim
MINGW_IMAGE='mstorsjo/llvm-mingw@sha256:b2a697af8fc50c5cc56011143453d02168ccfcff635d12ca76a1c363f5f6b0c6' # clang 23.1.5

# The oldest glibc any of the Linux binaries may ask for. 2.17 is the arm64
# baseline and below anything .NET 10 itself runs on, so a binary inside it
# loads wherever the runtime does.
GLIBC_FLOOR=2.17

COMMON_FLAGS="-std=c11 -O2 -ffp-contract=off -fvisibility=hidden -Wall -Wextra -pedantic"
# GCC only: it does not implement the FP_CONTRACT pragma (-ffp-contract=off
# is what stands in for it), and -pedantic objects to returning the callback
# as a void*, which is the header's whole reason for not including miniaudio.
GCC_FLAGS="-Wno-unknown-pragmas -Wno-pedantic"
EXPORT_COUNT=21

rm -rf "$BUILD"
mkdir -p "$BUILD"

dest() {
    local rid="$1"
    mkdir -p "$OUT/$rid/native"
    echo "$OUT/$rid/native"
}

fail() {
    echo "error: $*" >&2
    exit 1
}

# --- macOS -------------------------------------------------------------------

build_macos() {
    local rid="$1" arch="$2"
    local out
    out="$(dest "$rid")/libflowerbridge.dylib"
    echo "=== $rid ==="
    # shellcheck disable=SC2086
    xcrun clang $COMMON_FLAGS -dynamiclib \
        -target "$arch-apple-macos12.0" \
        -install_name @rpath/libflowerbridge.dylib \
        -Wl,-headerpad_max_install_names \
        -o "$out" "$NATIVE_DIR/flower_audio_bridge.c"
    # The header padding is for the macOS head, whose bundling rewrites the
    # install name to its own and fails if the load commands have no room.
    xcrun strip -x "$out"
    # Stripping invalidates the signature arm64 requires to load at all.
    codesign --force --sign - "$out"

    local exports
    exports="$(xcrun nm -gU "$out" | grep -c ' _flower_audio_bridge_' || true)"
    [[ "$exports" == "$EXPORT_COUNT" ]] || fail "$rid exports $exports bridge symbols, expected $EXPORT_COUNT"
    local deps
    deps="$(xcrun otool -L "$out" | tail -n +2 | grep -v libflowerbridge | grep -v '/usr/lib/libSystem' || true)"
    [[ -z "$deps" ]] || fail "$rid links more than libSystem: $deps"
    if xcrun objdump -d "$out" | grep -qiE '\b(fmadd|fmsub|fnmadd|fnmsub|fmla|fmls|vfmadd|vfmsub|vfnmadd|vfnmsub)'; then
        fail "$rid contains a fused multiply-add"
    fi
    echo "-> $out"
}

build_macos osx-arm64 arm64
build_macos osx-x64 x86_64

# --- Linux -------------------------------------------------------------------
# arm64 natively and x64 with Debian's cross GCC, both in one arm64 container,
# so nothing runs under emulation.

echo "=== linux-arm64, linux-x64 ==="
docker run --rm \
    -v "$NATIVE_DIR:/src:ro" -v "$BUILD:/build" \
    -e COMMON_FLAGS="$COMMON_FLAGS $GCC_FLAGS" \
    "$DEBIAN_IMAGE" bash -euo pipefail -c '
        apt-get update -qq >/dev/null
        apt-get install -y -qq --no-install-recommends \
            gcc libc6-dev gcc-x86-64-linux-gnu libc6-dev-amd64-cross >/dev/null
        build() {
            local cc="$1" strip="$2" dir="$3"
            mkdir -p "/build/$dir"
            # -Wl,--as-needed so libm is a dependency only if rintf is not
            # inlined; -z noexecstack because nothing here needs one.
            $cc $COMMON_FLAGS -shared -fPIC \
                -Wl,-soname,libflowerbridge.so -Wl,--as-needed -Wl,-z,noexecstack \
                -o "/build/$dir/libflowerbridge.so" /src/flower_audio_bridge.c -lm
            $strip --strip-unneeded "/build/$dir/libflowerbridge.so"
        }
        build gcc strip linux-arm64
        build x86_64-linux-gnu-gcc x86_64-linux-gnu-strip linux-x64
        gcc --version | head -1 > /build/linux-compiler.txt
    '

check_linux() {
    local rid="$1" objdump="$2"
    local built="$BUILD/$rid/libflowerbridge.so"
    docker run --rm -v "$BUILD:/build" "$DEBIAN_IMAGE" bash -euo pipefail -c "
        apt-get update -qq >/dev/null
        apt-get install -y -qq --no-install-recommends binutils binutils-x86-64-linux-gnu >/dev/null
        $objdump -T /build/$rid/libflowerbridge.so > /build/$rid/dynsym.txt
        $objdump -p /build/$rid/libflowerbridge.so | awk '/NEEDED/ {print \$2}' > /build/$rid/needed.txt
        $objdump -d /build/$rid/libflowerbridge.so > /build/$rid/disasm.txt
    "
    local exports
    exports="$(grep -c ' flower_audio_bridge_' "$BUILD/$rid/dynsym.txt" || true)"
    [[ "$exports" == "$EXPORT_COUNT" ]] || fail "$rid exports $exports bridge symbols, expected $EXPORT_COUNT"
    local needed
    needed="$(grep -vxE 'libc\.so\.6|libm\.so\.6' "$BUILD/$rid/needed.txt" || true)"
    [[ -z "$needed" ]] || fail "$rid needs more than libc and libm: $needed"
    local newest
    newest="$(grep -oE 'GLIBC_[0-9.]+' "$BUILD/$rid/dynsym.txt" | sed 's/GLIBC_//' | sort -t. -k1,1n -k2,2n | tail -1)"
    if [[ "$(printf '%s\n%s\n' "$GLIBC_FLOOR" "$newest" | sort -t. -k1,1n -k2,2n | tail -1)" != "$GLIBC_FLOOR" ]]; then
        fail "$rid asks for glibc $newest, past the $GLIBC_FLOOR floor"
    fi
    if grep -qiE '\b(fmadd|fmsub|fnmadd|fnmsub|fmla|fmls|vfmadd|vfmsub|vfnmadd|vfnmsub)' "$BUILD/$rid/disasm.txt"; then
        fail "$rid contains a fused multiply-add"
    fi
    cp "$built" "$(dest "$rid")/libflowerbridge.so"
    echo "-> $OUT/$rid/native/libflowerbridge.so (glibc >= $newest)"
}

check_linux linux-arm64 objdump
check_linux linux-x64 x86_64-linux-gnu-objdump

# --- Windows -----------------------------------------------------------------
# clang, not MSVC, which supports C11 <stdatomic.h> only behind
# /experimental:c11atomics. llvm-mingw links the UCRT, which every supported
# Windows has; -static-libgcc and friends are moot for plain C with no
# exceptions or threads, and the dependency check below holds it to that.

echo "=== win-x64, win-arm64 ==="
docker run --rm \
    -v "$NATIVE_DIR:/src:ro" -v "$BUILD:/build" \
    -e COMMON_FLAGS="$COMMON_FLAGS" \
    "$MINGW_IMAGE" bash -euo pipefail -c '
        build() {
            local triple="$1" dir="$2"
            mkdir -p "/build/$dir"
            $triple-clang $COMMON_FLAGS -shared \
                -Wl,--no-insert-timestamp \
                -o "/build/$dir/flowerbridge.dll" /src/flower_audio_bridge.c
            $triple-strip --strip-unneeded "/build/$dir/flowerbridge.dll"
            llvm-objdump -p "/build/$dir/flowerbridge.dll" > "/build/$dir/pe.txt"
            llvm-objdump -d "/build/$dir/flowerbridge.dll" > "/build/$dir/disasm.txt"
        }
        build x86_64-w64-mingw32 win-x64
        build aarch64-w64-mingw32 win-arm64
        x86_64-w64-mingw32-clang --version | head -1 > /build/windows-compiler.txt
    '

check_windows() {
    local rid="$1"
    local pe="$BUILD/$rid/pe.txt"
    local exports
    exports="$(grep -cE '^\s+[0-9]+\s+0x[0-9a-f]+\s+flower_audio_bridge_' "$pe" || true)"
    [[ "$exports" == "$EXPORT_COUNT" ]] || fail "$rid exports $exports bridge symbols, expected $EXPORT_COUNT"
    local imports
    imports="$(grep 'DLL Name:' "$pe" | awk '{print $3}' | grep -viE '^(api-ms-win-crt-[a-z]+-l1-1-0\.dll|kernel32\.dll)$' || true)"
    [[ -z "$imports" ]] || fail "$rid imports more than the UCRT and kernel32: $imports"
    if grep -qiE '\b(fmadd|fmsub|fnmadd|fnmsub|fmla|fmls|vfmadd|vfmsub|vfnmadd|vfnmsub)' "$BUILD/$rid/disasm.txt"; then
        fail "$rid contains a fused multiply-add"
    fi
    cp "$BUILD/$rid/flowerbridge.dll" "$(dest "$rid")/flowerbridge.dll"
    echo "-> $OUT/$rid/native/flowerbridge.dll"
}

check_windows win-x64
check_windows win-arm64

# --- Provenance --------------------------------------------------------------

(cd "$NATIVE_DIR" && shasum -a 256 flower_audio_bridge.c flower_audio_bridge.h) > "$OUT/flowerbridge-source.sha256"
echo "Linux:   $(cat "$BUILD/linux-compiler.txt")"
echo "Windows: $(cat "$BUILD/windows-compiler.txt")"
echo "macOS:   $(xcrun clang --version | head -1)"
rm -rf "$BUILD"
echo "Done. Source recorded in $OUT/flowerbridge-source.sha256"
