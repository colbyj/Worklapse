cmake_minimum_required(VERSION 3.21)
project(worklapse_native C)

find_package(AOM CONFIG REQUIRED)
# CMake's own module: libjpeg-turbo's package config also wants libspng and zlib (for TurboJPEG's image loaders,
# unused here), which can't be found without vcpkg's toolchain file.
find_package(JPEG REQUIRED)
find_package(WebP CONFIG REQUIRED)

add_library(worklapse_native SHARED av1.c color.c image.c)
target_link_libraries(worklapse_native PRIVATE
    AOM::aom
    JPEG::JPEG
    WebP::webp)
set_target_properties(worklapse_native PROPERTIES
    C_STANDARD 11
    C_VISIBILITY_PRESET hidden
    PREFIX "")

if(MSVC)
    target_compile_options(worklapse_native PRIVATE /W4 /O2)
    # The C runtime linked in (/MT), matching vcpkg's x64-windows-static libraries: no VCRUNTIME140.dll needed.
    set_target_properties(worklapse_native PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
else()
    target_compile_options(worklapse_native PRIVATE -Wall -Wextra -O2)
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_link_options(worklapse_native PRIVATE
        # Only the wl_* functions are exported, not libaom's, libjpeg's and libwebp's: other libraries in the process
        # (Skia has its own libjpeg and libwebp) can't pick up these copies, nor these theirs.
        "LINKER:--exclude-libs,ALL"
        # A missing symbol fails the build here rather than when Worklapse loads the library.
        "LINKER:--no-undefined")
endif()
