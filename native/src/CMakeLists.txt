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
else()
    target_compile_options(worklapse_native PRIVATE -Wall -Wextra -O2)
endif()
