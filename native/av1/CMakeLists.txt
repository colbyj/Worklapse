cmake_minimum_required(VERSION 3.21)
project(worklapse_av1 C)

find_package(AOM CONFIG REQUIRED)

add_library(worklapse_av1 SHARED worklapse_av1.c)
target_link_libraries(worklapse_av1 PRIVATE AOM::aom)
set_target_properties(worklapse_av1 PROPERTIES
    C_STANDARD 11
    C_VISIBILITY_PRESET hidden
    PREFIX "")

if(MSVC)
    target_compile_options(worklapse_av1 PRIVATE /W4 /O2)
else()
    target_compile_options(worklapse_av1 PRIVATE -Wall -Wextra -O2)
endif()
