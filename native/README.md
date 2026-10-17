# Native code

`av1/worklapse_av1.c` is a small C wrapper around [libaom](https://aomedia.googlesource.com/aom/).
It keeps libaom's version-specific structs on the C side and exposes a few plain functions
(create encoder, encode an I420 frame, decode a frame to I420 or BGRA) that `Worklapse.Video` calls through
P/Invoke. libaom is linked in statically, so each platform ships one library.

## Windows

Requires Visual Studio 2026 or its Build Tools with the C++ workload (it provides vcpkg, CMake and
Ninja). Nothing to run by hand: building `Worklapse.Video` (so any `dotnet build` of the solution) runs
`worklapse_av1.targets`, which builds the library whenever `native/win-x64/worklapse_av1.dll` is missing or
older than its sources (the C files, `CMakeLists.txt`, the vcpkg files or the targets file itself).

It installs libaom with vcpkg (version pinned by `vcpkg-configuration.json`) into `vcpkg_installed/`,
builds the wrapper with CMake and Ninja in `av1/build/`, copies `worklapse_av1.dll` to `win-x64/`, and
from there next to every assembly that uses `Worklapse.Video`. The first build takes about five minutes
while vcpkg builds libaom (much less if vcpkg's binary cache already has it); a change to the C code
rebuilds in seconds. Visual Studio's design-time builds skip it.
