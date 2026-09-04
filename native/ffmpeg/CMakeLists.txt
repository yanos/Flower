cmake_minimum_required(VERSION 3.20)
project(flower_ffmpeg C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)

# The façade is one translation unit on purpose - see flower_ffmpeg.h for why
# the surface is this small.
add_library(flower_ffmpeg SHARED flower_ffmpeg.c)
target_include_directories(flower_ffmpeg PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

# pkg-config is how every FFmpeg build - MacPorts, a distro, or one configured
# and installed by native/ffmpeg/<platform>/build.sh - describes itself. The
# cross builds point PKG_CONFIG_LIBDIR at their own prefix rather than teaching
# CMake a second way to find FFmpeg.
find_package(PkgConfig REQUIRED)
# The floor is FFmpeg 5.1, which is where AVChannelLayout and
# swr_alloc_set_opts2 arrived - the newest APIs this façade uses, and the ones
# that make its channel handling a single code path. Higher numbers were an
# accident of the machine it was first built on rather than a requirement, and
# an accident that costs something real: Ubuntu 24.04 ships FFmpeg 6, so a
# floor of 61 meant the Linux build could not find a distro FFmpeg at all.
pkg_check_modules(FFMPEG REQUIRED IMPORTED_TARGET
    libavformat>=59.27
    libavcodec>=59.37
    libavutil>=57.28
    libswresample>=4.7)

target_link_libraries(flower_ffmpeg PRIVATE PkgConfig::FFMPEG)

if(FLOWER_FFMPEG_STATIC)
    # Mobile links FFmpeg in rather than shipping four more libraries per ABI,
    # so the façade is the only binary Flower has to package there.
    target_link_libraries(flower_ffmpeg PRIVATE ${FFMPEG_STATIC_LDFLAGS})
endif()

if(APPLE)
    set_target_properties(flower_ffmpeg PROPERTIES
        INSTALL_NAME_DIR "@rpath"
        BUILD_WITH_INSTALL_NAME_DIR TRUE)
endif()

if(FLOWER_FFMPEG_ASAN)
    target_compile_options(flower_ffmpeg PRIVATE -fsanitize=address -fno-omit-frame-pointer -g)
    target_link_options(flower_ffmpeg PRIVATE -fsanitize=address)
endif()
