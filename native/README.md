# Native code

`av1/worklapse_av1.c` is a small C wrapper around [libaom](https://aomedia.googlesource.com/aom/).
It keeps libaom's version-specific structs on the C side and exposes a few plain functions
(create encoder, encode a BGRA image, decode a frame to BGRA) that `Worklapse.Video` calls through
P/Invoke. libaom is linked in statically, so each platform ships one library.

## Windows

Requires Visual Studio 2026 or its Build Tools with the C++ workload (it provides vcpkg, CMake and
Ninja). From Git Bash:

```sh
bash native/build-windows.sh
```

This installs libaom with vcpkg (version pinned by `vcpkg-configuration.json`), builds the wrapper and
copies `worklapse_av1.dll` to `native/win-x64/`, where the .NET build picks it up.
