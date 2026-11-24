# Worklapse

Worklapse captures your screen every few seconds and logs the app and window that had focus, letting you replay your day and log time spent on different projects. It runs on Windows and Linux (KDE Plasma). 

Worklapse was inspired by [TimeSnapper](https://timesnapper.com). Like TimeSnapper, everything stays on your own computer; see [PRIVACY.md](PRIVACY.md).

## Download

Installers for Windows and a Linux AppImage are on the [releases page](https://github.com/colbyj/Worklapse/releases).

## Getting started

At its first start Worklapse opens a welcome window: what it records, a chance to change that, and how a day of reviewing and logging hours goes. Open it again from the question mark at the bottom of the main window's left edge.

## Building

Requirements:

- .NET 10 SDK
- Windows: Visual Studio 2026 or its Build Tools, with the C++ workload
- Linux: gcc, nasm, pkg-config and a vcpkg clone with `VCPKG_ROOT` set (see [native/README.md](native/README.md))

From the repository root:

```sh
dotnet build
dotnet run --project src/Worklapse.App
```

The first build takes several minutes while vcpkg builds the native libraries (libaom, libjpeg-turbo, libwebp).

## Tests

```sh
dotnet test tests/Worklapse.Tests
```

## Docs

- [docs/PROJECT.md](docs/PROJECT.md): decisions, how the code is organized, and where things stand
- [native/README.md](native/README.md): the native video and image library

## License

GPL-3.0; see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
