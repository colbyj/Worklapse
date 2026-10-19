# Handoff: finishing the Linux port on Kubuntu

Written 2026-10-19 for a new Claude session on the developer's Kubuntu 26.04 install (KDE Plasma 6, Wayland). The
port was built on Windows while the developer was away: the code is there, it builds and its tests pass on Linux,
and most of it ran against a real KWin in a container. What's left needs a real Plasma desktop: a first real
screenshot, multiple and scaled monitors, and the developer looking at the result.

The other open task, designing Day review's side panel, is in `docs/HANDOFF.md`. It's separate from this one.

## Before you start

1. Read `docs/PROJECT.md`, especially *How capture works on KDE Plasma (Linux)* and *Next steps → 2*.
2. Working rules from the developer: ask before adding anything they haven't asked for; when a choice changes what
   they see or keep, ask; for internal choices, pick the sensible option and say so. Commit only when asked. Never
   tell them they're "absolutely right"; push back when something's wrong.
3. **Getting the code onto Kubuntu.** The repository has **no git remote**. The Linux work is committed on master
   (`21debfa` to the commit that added this file). Ask the developer how they brought it over, and check `git log`
   against what's described here.

## Setting up the machine

```sh
sudo apt install build-essential git curl zip unzip tar pkg-config nasm
# .NET 10 SDK: from Ubuntu's archive if 26.04 has it (apt search dotnet-sdk-10), otherwise
# https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual
git clone https://github.com/microsoft/vcpkg ~/vcpkg && ~/vcpkg/bootstrap-vcpkg.sh -disableMetrics
echo 'export VCPKG_ROOT=~/vcpkg' >> ~/.bashrc     # new terminal afterwards
```

Then from the repository root:

```sh
dotnet build                                      # first time: several minutes (vcpkg builds libaom, libjpeg-turbo, libwebp)
tests/Worklapse.Tests/bin/Debug/net10.0/Worklapse.Tests      # 193 tests; `dotnet test` reports "Zero tests ran"
src/Worklapse.App/bin/Debug/net10.0/Worklapse                # run the executable itself, see below
```

Run the `Worklapse` executable, not `dotnet Worklapse.dll`. KWin only allows screenshots by the executable that a
desktop file names, and Worklapse writes that file for whichever executable is running.

## What was verified, and how

| What | How | Result |
|---|---|---|
| C rewrite (readability) | old and new built side by side on Linux; every export compared over JPEG/WebP at several qualities, AV1 streams with a quantizer change, decoding, odd sizes down to 1×1, error paths | 1,614 checks, 0 differences; clean under gcc `-Wall -Wextra` and MSVC `/W4` |
| Native library on Linux | `dotnet build` in WSL (Ubuntu 24.04) through the new target | `.so` exports only the 20 `wl_*` functions, needs only libc and libm |
| Whole solution on Linux | build and tests in WSL | 193/193 pass (and on Windows) |
| KWin script, screens, active window | real KWin 6.7.5 (Fedora 43 container, headless virtual outputs) | screens reported in ~55 ms; xterm (XWayland) and foot (Wayland) identified with path and title |
| Screenshot permission | same | no desktop file → KWin refuses and Worklapse explains; with the file Worklapse writes → allowed |
| Real screenshot | same | **not possible there**: no GPU, so KWin composites with QPainter, and its screenshots need OpenGL; Worklapse reports that clearly |
| Screenshot pixels path | a fake KWin on a private bus that answers like KWin's source does (checked against KWin 6.7's code) | ARGB32 and RGBA8888, padded rows, odd width: frames byte-identical to expected; no fd leaks |
| Idle | real KWin | version 2 (input-only) offered; 7.7 s idle after 8 s without input |
| Lock | real KWin (it provides `org.freedesktop.ScreenSaver`) | answers; never saw it locked (no lock screen there) |
| The app | published app under the container's KWin | starts, main window shows (XWayland), desktop file with icon written, recorder reports each failed screenshot and recovers, quits on SIGTERM |

Not tested anywhere: a real screenshot, scaled screens (KWin's virtual outputs came up at 100 %), several real
monitors and their EDID ids, the tray icon (Avalonia uses StatusNotifierItem, which Plasma shows), the Settings
preview, Day review on Linux-recorded video, memory use.

## Smoke test on Kubuntu (do this with the developer)

Use a test configuration first, so nothing lands in the real data: `WORKLAPSE_CONFIG_DIR=~/wl-test` with a
`settings.json` whose `DataFolder` is disposable and `"IdleMinutes": 120` (see PROJECT.md *Building, running,
testing*). There's no real Linux history yet, so the developer may prefer to go straight to the real folders.

1. Start it from a terminal (errors print there). Expect the main window and a tray icon.
   `~/.local/share/applications/worklapse.desktop` should exist and name the executable; Worklapse appears in the
   application menu.
2. Let it record a minute in each capture mode (Settings): all monitors, selected, active application.
   Check in Day review: the right monitors, right colours, nothing shifted or cut. Active application should black
   out everything but the focused window. **This is where scaling shows**: the laptop panel is probably at 125–200 %.
   If a window's area is off by the scale factor, the logical-to-pixel conversion (`ScreenLayout`) is the place.
3. The status window's app and title should follow focus, for Wayland apps and XWayland apps (Worklapse itself is
   XWayland), Firefox (probably a Snap on Kubuntu), VS Code, Konsole.
4. Idle: no input for the idle limit → idle, and the captures since the last input re-marked idle. Lock the screen
   (Meta+L) → Locked.
5. Unplug/replug a monitor while recording (the Windows replug fix depends on stable ids: here
   `make|model|serial|connector`).
6. Settings → the recording preview (it uses its own screen source on the same KWin connection).
7. Memory (System Monitor) while recording and while reviewing four monitors; the Windows figures are in
   PROJECT.md. Linux uses software rendering like Windows unless `UseGpuRendering` is set (`Program.cs`); that
   choice was copied from Windows, not measured.

If screenshots fail with "KWin refused", the desktop file doesn't match the running executable (check the `Exec`
line against `readlink /proc/<pid>/exe`) or KDE hasn't read it yet (`kbuildsycoca6`). With "only while compositing
with OpenGL", check System Settings → Display and Monitor → Compositor. The KWin script's errors go to the journal
(`journalctl --user -e`, look for "kwin" and "worklapse").

## Decided by the developer (2026-10-19, built)

- **Can't record → say so, stay usable.** When capture can't start (not Plasma Wayland, KWin missing, the script
  didn't report…), the app runs on `PlatformServices.Unavailable`: the status bar says "Not recording" and the red
  message bar gives the reason; Day review, the timesheet and Settings work. On Windows too. Verified in the
  container: with `XDG_CURRENT_DESKTOP=GNOME` the app stays open (before, it exited).
- **No primary monitor on KDE.** KWin doesn't tell scripts which screen is primary, so none is marked primary:
  monitors are numbered 1, 2, 3… and Settings doesn't offer "Primary monitor" (`KnowsPrimaryMonitor`). A settings file
  that already has that mode makes the recorder say why it isn't recording.

## Decisions for the developer (not taken; ask)

1. **Browser sites** aren't read on Linux (Windows uses UI Automation; Linux would need AT-SPI, and Firefox only
   exposes it with accessibility on). Until then, site-based Productive and Excluded rules don't apply on Linux.
2. **Program names differ by OS**: Linux records `firefox`, `code`; Windows `firefox.exe`, `Code.exe`. Programs that
   only host others (python3, java, node, electron, wine, dotnet, mono…) are named by their window's class instead
   (`ProcessIdentity`; that list was my choice). This matters once history is shared between the two OSes.
3. **The desktop file** is written, and the application menu updated, at every start where it differs. A packaged
   install would ship its own instead.
4. **HDR**: KWin may send 10-bit screenshots with HDR on; Worklapse then reports an unsupported format.
5. **Mixed scales** leave gaps between monitors in Worklapse's coordinates (see `ScreenLayout`), which Day review
   shows as space between them, if it shows the layout.

## What changed outside the port

- `native/src/*.c, *.h`: rewritten for readability (named constants for the fixed-point colour maths, a `wl_i420`
  struct for plane layout, one statement per line, helper functions); output identical (see the table).
  `CMakeLists.txt`: on Linux, hides the static libraries' symbols and fails the link on undefined symbols.
- `worklapse_native.targets`: a Linux target next to the Windows one; separate install and build folders per OS.
- `PlatformServices.CreateScreenSource` (Core): how the App makes the Settings preview's own screen source (was
  `new GdiScreenSource()` in App). `PlatformServices.Dispose` also disposes the idle detector.
- **Bug fix (both OSes, mattered on Linux):** the default data and config folders used
  `Environment.GetFolderPath` without `DoNotVerify`. On Linux that gives an empty path when `~/.local/share` doesn't
  exist yet, so the data went to a relative `Worklapse` folder wherever the app was started. Found in the container.
- `ReclaimTests.WhatKeepsDeletedVideoOnDiskIsReportedUntilItIsGone`: blocked deletion with an open file, which only
  works on Windows; on Linux it now uses a read-only folder. That also checks that the reclaimer handles the Linux
  error (`UnauthorizedAccessException`), which it does.
- `Program.cs`: X11 rendering options mirroring the Windows ones. App: references `Worklapse.Capture.Linux`, copies
  `worklapse.png` next to the executable on Linux (for the menu icon).

## New code (src/Worklapse.Capture.Linux)

`KdePlatform` (entry), `KWinSession` (bus connection, script loading, latest reports), `KWinScript.js` (runs in
KWin), `KWinReports` (JSON), `ScreenLayout` (logical → pixel), `KWinScreenSource` (ScreenShot2 + pipe),
`KWinForegroundWindowProbe`, `ProcessIdentity`, `WaylandConnection` + `WaylandIdleDetector` (wire protocol, no
libwayland), `ScreenSaverSessionMonitor`, `DesktopEntry`, `DBusCalls`, `Libc` (poll). D-Bus is Tmds.DBus.Protocol
0.94.1, the version Avalonia already ships. Tests: `KdeCaptureTests`, `WaylandIdleTests`.

Useful facts from KWin 6.7's source: ScreenShot2 is version 5; the reply (type `raw`, format = QImage format, width,
height, stride as uint32, scale as double) is sent **before** the pixels are written, from KWin's own copy of the fd;
screenshots come as `ARGB32_Premultiplied`; "Cancelled" means no OpenGL context. Scripts see `workspace.screens`
(LogicalOutput: name, geometry, devicePixelRatio, manufacturer, model, serialNumber), `windowActivated`,
`screensChanged`, `virtualScreenGeometryChanged`; no output order or mode size.

## Tooling left on the Windows machine (in case it's useful again)

- WSL Ubuntu-24.04, in the user's home: .NET 10 SDK added to `~/.dotnet` (next to the 9 that was there), `~/vcpkg`,
  nasm and pkgconf in `~/.local`, scratch copies `~/wl-repo`, `~/wl-native`, `~/wl-harness`, `~/wl-app`,
  `~/wl-compare`, `~/wl-tools`. Nothing installed system-wide.
- Docker image `worklapse-kwin-fedora` (Fedora 43 + KWin 6.7 + xterm, foot). The harness that drove it lived in
  that session's scratchpad and may be gone. The approach: `kwin_wayland --virtual --xwayland` inside
  `dbus-run-session`, with `--cap-add=SYS_NICE` (kwin_wayland has that file capability), a `/etc/machine-id`, and
  `/tmp/.X11-unix` created. Arch's KWin 6.8 crashes on start without a GPU.
