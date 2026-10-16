#!/usr/bin/env bash
# Builds native/win-x64/worklapse_av1.dll: the small C wrapper in native/av1 with libaom linked in
# statically. Uses the vcpkg, CMake and Ninja that ship with Visual Studio (Build Tools) 2026.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe"
vs_root="$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | tr -d '\r')"
vs_unix="$(cygpath -u "$vs_root")"
vcpkg="$vs_unix/VC/vcpkg/vcpkg.exe"
cmake_bin="$vs_unix/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin"
ninja_dir="$vs_unix/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja"
triplet="x64-windows-static-md"

cd "$here"
"$vcpkg" install --triplet "$triplet"

# Configure and build inside a Developer environment so the MSVC compiler is on PATH.
cat > build-av1.cmd <<EOF
@echo off
call "$(cygpath -w "$vs_unix/VC/Auxiliary/Build/vcvars64.bat")" >nul || exit /b 1
set PATH=$(cygpath -w "$cmake_bin");$(cygpath -w "$ninja_dir");%PATH%
cmake -S av1 -B av1/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH="$(cygpath -w "$here/vcpkg_installed/$triplet")" || exit /b 1
cmake --build av1/build || exit /b 1
EOF
status=0
cmd.exe //c "$(cygpath -w "$here/build-av1.cmd")" || status=$?
rm -f build-av1.cmd
if [[ $status -ne 0 || ! -f av1/build/worklapse_av1.dll ]]; then
  echo "Building the AV1 wrapper failed (exit $status)." >&2
  exit 1
fi

mkdir -p win-x64
cp av1/build/worklapse_av1.dll win-x64/
echo "Built $here/win-x64/worklapse_av1.dll"
