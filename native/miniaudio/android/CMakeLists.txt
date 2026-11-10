cmake_minimum_required(VERSION 3.22)
project(flower_miniaudio C)

# Must be named "miniaudio" -> produces libminiaudio.so, matching the literal
# DllImport("miniaudio") string in Miniaudio-CS's generated bindings, so
# .NET's Android P/Invoke resolution finds it with no custom
# DllImportResolver needed.
add_library(miniaudio SHARED ../impl.c)

target_include_directories(miniaudio PRIVATE ../vendor)

# Android's AAudio/OpenSL|ES backends need these; miniaudio auto-detects and
# compiles the right backend in via its own preprocessor checks - no MA_*
# feature defines needed beyond a standard NDK build.
target_link_libraries(miniaudio
    android
    log
    m
    OpenSLES
)

# Flower's render-thread bridge, a library of its own on every platform
# (libflowerbridge.so here, DllImport("flowerbridge") in NativeAudioBridge).
# It includes no miniaudio - see the top of flower_audio_bridge.c - so
# miniaudio is handed a callback that lives in this library instead.
#
# libm for rintf, which bionic keeps out of libc. -ffp-contract=off and the
# hidden default visibility for the reasons native/miniaudio/desktop/build.sh
# gives: the output stage is held to OutputStage.cs bit for bit, and the
# header's FLOWER_AUDIO_BRIDGE_API is the whole export list.
add_library(flowerbridge SHARED ../flower_audio_bridge.c)
target_link_libraries(flowerbridge m)
target_compile_options(flowerbridge PRIVATE
    -std=c11 -O2 -ffp-contract=off -fvisibility=hidden -Wall -Wextra -pedantic)

# AAudio itself is dlopen()'d at runtime by miniaudio (not link-time linked)
# so it works across the full API 21+ range with a graceful OpenSL|ES
# fallback below API 26 - see MiniaudioSink.cs's own remarks on backend
# selection being handled entirely native-side.

# No -fvisibility=hidden: Miniaudio-CS's bindings resolve every ma_*
# function via P/Invoke (effectively dlsym), so the whole C API needs
# default visibility - miniaudio's own functions carry no explicit
# visibility/export attribute to survive a hidden default.
target_compile_options(miniaudio PRIVATE -O2)
# Debug-only ASan variant: configure with -DFLOWER_MINIAUDIO_ASAN=ON to
# catch native heap corruption early (see MiniaudioSink.cs's ma_device
# struct-sizing padding remarks - AAudio/OpenSL|ES are untested backends).
option(FLOWER_MINIAUDIO_ASAN "Build with AddressSanitizer" OFF)
if(FLOWER_MINIAUDIO_ASAN)
    target_compile_options(miniaudio PRIVATE -fsanitize=address -g -O0)
    target_link_options(miniaudio PRIVATE -fsanitize=address)
endif()
