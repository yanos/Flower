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
    OpenSLES
)

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
