# Native code

`src/` is a small C library that `Worklapse.Video` calls through P/Invoke:

- `av1.c` wraps [libaom](https://aomedia.googlesource.com/aom/) (create an encoder, encode an I420 frame,
  decode a frame to I420 or BGRA). It keeps libaom's version-specific structs on the C side.
- `image.c` encodes I420 frames as JPEG ([libjpeg-turbo](https://libjpeg-turbo.org/)) or WebP
  ([libwebp](https://chromium.googlesource.com/webm/libwebp)) and decodes those back to I420.
- `color.c` has the colour conversions both use (I420 is BT.709 full range; JPEG and WebP files are BT.601).

The libraries are linked in statically, so each platform ships one library, `worklapse_native`.

## Windows

Requires Visual Studio 2026 or its Build Tools with the C++ workload (it provides vcpkg, CMake and
Ninja). Nothing to run by hand: building `Worklapse.Video` (so any `dotnet build` of the solution) runs
`worklapse_native.targets`, which builds the library whenever `native/win-x64/worklapse_native.dll` is missing
or older than its sources (the C files, `CMakeLists.txt`, the vcpkg files or the targets file itself).

It installs the libraries with vcpkg (versions pinned by `vcpkg-configuration.json`) into `vcpkg_installed/`,
builds the wrapper with CMake and Ninja in `src/build/`, copies `worklapse_native.dll` to `win-x64/`, and
from there next to every assembly that uses `Worklapse.Video`. The first build takes about seven minutes
while vcpkg builds the libraries (much less if vcpkg's binary cache already has them); a change to the C code
rebuilds in seconds. Visual Studio's design-time builds skip it.

## Linux

The same libraries at the same versions, linked in statically, into `native/linux-x64/worklapse_native.so`. It exports
only the `wl_*` functions (the libraries' own symbols are hidden, so they can't clash with Skia's libjpeg and libwebp)
and needs nothing beyond libc and libm. Install once (Kubuntu / Ubuntu):

```sh
sudo apt install build-essential git curl zip unzip tar pkg-config nasm
git clone https://github.com/microsoft/vcpkg ~/vcpkg
~/vcpkg/bootstrap-vcpkg.sh -disableMetrics
echo 'export VCPKG_ROOT=~/vcpkg' >> ~/.bashrc   # then open a new terminal
```

Then `dotnet build` works as on Windows: `worklapse_native.targets` runs `vcpkg install --triplet x64-linux` into
`vcpkg_installed-linux/` (its own folder: vcpkg removes whatever an install folder holds that the current build
doesn't need, which would include the other OS's libraries), builds with vcpkg's own CMake and Ninja in
`src/build-linux/`, and copies the library to `linux-x64/`. The first build takes several minutes.
