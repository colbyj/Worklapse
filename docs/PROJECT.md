# Worklapse — project notes

Worklapse is a successor to [TimeSnapper](https://timesnapper.com) for Windows and Linux. It takes a
capture of the screen every few seconds, logs which app and window had focus, and lets you play a day
back to work out how long you spent on what. The main workflow: drag across part of the day's timeline,
read how much of it was active, productive and idle, and delete noise (a game after hours, a video at
lunch) before totalling project hours.

This document records what has been decided, how the code is put together, where things stand, and
what to do next. It's meant to be enough to pick the work up again in a new session.

---

## Decisions so far

| Topic | Decision | Why |
|---|---|---|
| Platforms | Windows first, then Kubuntu 26.04 (KDE Plasma, Wayland). GNOME, XFCE, Mint/X11 later. | The developer dual-boots; Linux is the long-term home. |
| UI toolkit | .NET 10 + Avalonia 12 | Familiar stack, runs on both. |
| Look | **KDE Breeze-style with a little Windows flavour**: Breeze control styling, system font (9 pt Segoe UI / 10 pt Noto Sans), 4 px corners, Fluent System Icons, desktop control sizes (Fluent compact density plus own templates for scroll bars, tabs, check boxes, spinners). Colours follow the OS, live: Windows 11 light/dark with the system accent; on KDE the active colour scheme from kdeglobals; on GNOME and the other GTK desktops (XFCE, Cinnamon, MATE, Budgie, …) Adwaita light/dark (colours only, untested); elsewhere Breeze light/dark with the portal accent. On Windows the tool bars and timeline strip share the window colour, like Windows 11's own apps. Settings → Application can pick another desktop's look (Windows / KDE / GTK) and force light or dark; the desktop's accent is kept. Timeline meaning colours stay fixed (dark variants). A dense desktop-app layout (tool bars, status bars; no menu bar since 2026-10-19: it repeated the rail and tool bars), not a web-app look. | Asked for explicitly; an earlier pass with cards and big padding looked like a web app, and a later one still looked touch-sized (2026-10-18). |
| What to capture | Choice of active application, primary monitor, selected monitors, or all monitors. The developer uses all monitors. | Matches TimeSnapper's options; all monitors became practical once video compression was in. |
| Idle | Stop capturing after N minutes without input (default 5) and while the screen is locked. When the limit is hit, the captures since the last input are re-marked as idle. | Idle time should be time without input, not time after the threshold. |
| Video format | AV1 (libaom), one stream per monitor, 90-frame segments (15 min at 10 s), constant quantizer, default 40, adjustable 20–55 in Settings (warning from 48). | Measured: roughly 2.5× smaller than per-frame WebP or JPEG at the same readability. |
| Recording as images | **Optional, video stays the default.** Settings → Recording: Video (AV1), JPEG images or WebP images, each format with its own quality slider over its full range (JPEG 1–100, default 15, TimeSnapper's; WebP 0–100, default 20, agreed 2026-10-18). A file per changed capture and monitor, `images/YYYY-MM-DD/HHmmss-m<monitor>.jpg/.webp`, which other programs can open. Each choice has a one-line trade-off under it, and a preview (as in TimeSnapper) shows the primary monitor's most detailed part as stored, at 1:1, with a running estimate of the space: while Settings is open the primary monitor is captured every interval and stored as the recorder would (`StorageEstimate`), for the chosen format only, and the average per capture is shown, so video's falls from its key frame as captures come in. Agreed 2026-10-17/18. AVIF was measured (2026-10-18: clearest text at WebP 20's size, ~0.3 s per 3440×1440 image, ~+110 MB while encoding) and left out as not different enough; AV2 has only its slow reference encoder so far; JPEG XL was worse at that size. | Asked for, like TimeSnapper. WebP was checked by eye: text clearer at 10 than JPEG at 15 (developer). Images take far more disk (see Benchmarks) and WebP more CPU than JPEG, but each is exact for its moment (no stale small text), deleting is deleting files, and playback needs no decoder. |
| Encoding | **Batch encoding.** Captures are converted to I420 (YUV 4:2:0, 1.5 bytes per pixel) at capture time and go into a staging file per segment (XOR against the previous capture, Brotli-compressed); full segments are encoded one at a time on a low-priority thread. Staged segments left over by a crash or quit are encoded on the next start. | Four live libaom encoders used about 850 MB (their frame-buffer pool is fixed; options barely help). |
| Encoding modes | One mode only (no battery mode, no quick encode). | The developer's laptop is effectively always plugged in; simplest to build. |
| Small-text accuracy | Accepted: at the default quantizer, small changes (a clock digit, a status label) can show the previous value for a while. The app/title log is exact regardless. A two-level fix exists (key frames q45, other frames q10) but costs 30–125 MB/hour during video playback, so it's not used. | Tracking relies on the log; the video is for context. |
| Screen capture (Windows) | GDI BitBlt in 64-row strips into one small shared bitmap; each strip is hashed (XxHash3, for change detection) and converted straight into one shared I420 frame. No `CAPTUREBLT` (it makes the cursor flicker and isn't needed with DWM). | DirectX Desktop Duplication needed Direct3D devices that cost ~90 MB+ of driver memory on the hybrid-GPU laptop; strips and I420 cut pixel buffers from ~105 MB to ~28 MB. |
| Playback decoding | One libaom decoder per monitor, single-threaded, closed after 15 s unused; a 64 MB cache of recent frames as I420 in pooled buffers, converted straight into the on-screen bitmap. | Each open decoder holds ~60 MB (1920×1080) to ~145 MB (3440×1440) of reference frames and buffers; every extra thread adds 30–40 MB for little speed. The old setup (8 decoders × 8 threads, 400 MB BGRA cache, a new 20 MB array per frame) reached ~3 GB. |
| Rendering (Windows) | Avalonia software rendering by default; `"UseGpuRendering": true` in `settings.json` switches to the GPU (ANGLE/Direct3D 11) at the next start. Agreed 2026-10-16; in Settings (General, "Draw with the GPU") since 2026-10-21. | Measured: GPU rendering cost ~80 MB more while recording, ~450 MB more while reviewing four monitors, and kept ~200 MB after the review closed, with no CPU saving in a normal-sized window. It may be smoother with Day review maximized on a very large or high-DPI screen (not measured). |
| Mica (Windows 11) | With the Windows look on Windows 11 22H2+, the main window's title bar, rail and the views' tool bars show Mica; nothing else does (developer's choice 2026-10-21, after trying more: Mica as a frame around Settings' solid tab page looked out of place). Those surfaces are transparent (`UiChromeBrush`, `UiChromeHeaderBrush`), each view's content below its tool bar sits on a solid panel, and DWM draws Mica behind (`Theming/WindowBackdrop`, attached by `MainWindow`: Avalonia `Transparent`, frame extended over the client area, `DWMWA_SYSTEMBACKDROP_TYPE`). Settings and the dialogs are solid, like Windows' own tabbed dialogs. Text in the main window is antialiased in grey while Mica is on. | Avalonia's own Mica (`TransparencyLevelHint = Mica`) needs WinUI composition, so GPU rendering; DWM's works with software rendering too (checked both, light and dark, maximize/restore). ClearType text smears over a transparent background. |
| Deleting | Mark ranges in Day review → they're hidden from totals at once → confirm later to delete permanently. The batch encoder then removes the deleted frames from disk: a segment with no frame left in use is deleted; one with some is rebuilt from those frames (decoded and encoded again at its quantizer), however few frames were deleted; a segment still staged leaves the deleted frames out when it's encoded. | Matches the review workflow: decide at the end of the day. **The purpose is that deleted content is gone** (nothing a client could be shown by accident); disk space is a bonus (developer, 2026-10-17). So there is no "only when it saves enough" rule, despite the small quality cost of a rebuild. |
| Productive time | Two classes, Productive and Other. Apps (by exe name) and websites are marked on Settings → Programs or by right-clicking in Day review. **The more specific one decides** (developer, 2026-10-17): a capture with a site follows the site; otherwise its app. So a browser marked productive covers only its pages without a known site. Classification is looked up when a day is read, so a change applies to all history. Kept in the database, not `settings.json` (it belongs with the history). | Decided 2026-10-16/17. |
| Excluded apps and sites | Marked on Settings → Programs (Excluded column, next to Productive). While an excluded app (by exe name) or site has the focus, **nothing is recorded** (no capture at all), so that time shows as *Not recorded*; once the idle limit passes it is recorded as idle as usual (an idle capture has no app or frames). Excluded if the app or the site is (a browser excluded covers all its pages). A browser tick whose site can't be read (private window, too slow) is recorded as the browser. Recorded history is left as it is. Kept in the database like the productive marks; an excluded app or site keeps its row when its captures are deleted. | Developer, 2026-10-18: nothing kept rather than time-and-name; the Programs list only (something must be recorded once before it can be excluded); no browser-specific handling for now. |
| Projects and the main window | **Designed and built 2026-10-18** (phases 1–6) — see Next steps, *1a*. One main window (Overview, Day review, Timesheet) replaces the status window; time is labelled with projects by linking apps/sites to them, overridden by hand in Day review; a timesheet holds one line per project per day with the hours to bill. | The developer logs contract hours; the status window was "somewhat useless". |
| Websites | Read from the browser's address bar through UI Automation at capture time (Chrome, Edge, Firefox), storing the **host name only, minus a leading `www.`** (mail.google.com and docs.google.com are separate; www.github.com is github.com). New captures only; older ones fall back to their browser's classification. Private windows record no site. | Agreed 2026-10-16/17. Measured: ~1 ms per capture, no measurable cost to the browsers' memory (see Benchmarks). |
| Plugging monitors in and out | Each capture records which monitors were attached (its *layout*), captured or not; Day review shows only those, and its Monitor list only those. Captures from before (schema < 6) get a layout worked out from their frames. *Selected monitors* are kept by the monitor's stable identity (Windows: device interface path, EDID model + connector), not `\\.\DISPLAYn`. Agreed 2026-10-17. | A replug on 2026-10-17 made Windows move and rename the monitors three times in 20 s; each arrangement became new `monitors` rows, and Day review drew all seven at once, on top of each other ("impossible to use"). |
| When a day starts | Settings → General → Days: *Day starts at*, any minute 00:00–23:59 typed as a 24-hour time (`DayStart` in `settings.json`, default midnight). A day is named by the date it starts on, so with 04:00 the hours after midnight belong to the day before, and with 22:00 Monday 21:00 is Sunday's. Applies to every view at once (Day review, Overview, Timesheet, "today"); nothing stored changes, and notes and logged time stay with their date. Timelines measure from the date's midnight, so a late night runs past 24 h (12 AM, 1 AM …). `Core/History/LocalDay`; `HistoryStore` takes the day start. | Developer, 2026-10-20: works into the early hours and wants them in the day before; asked for any time, not whole hours. |
| TimeSnapper history | Import it and play the old JPEGs back in place (referenced, not converted). | The goal is reviewing old days. |
| Shared history across OSes | Wanted eventually (likely on D:, BitLocker-unlocked under Linux, with Fast Startup off). Not started. | — |

### Benchmarks behind these choices (4 monitors: 3440×1440, 2560×1600, 2×1920×1080)

- Per-capture size, quiet desktop work: whole-screen JPEG 15% ≈ 431 KB, WebP q25 ≈ 410 KB,
  changed-tiles-only WebP ≈ 40–55 KB, **AV1 low-delay q40 ≈ 22 KB** (all monitors together).
- AV1 above about q48 starts showing wrong/stale small text; SSIM does not catch this, only visual checks do.
- libaom keyframes cost about 12 CPU-seconds for all four monitors; ordinary frames 0.2–0.3 s.
- Memory: four live encoders ≈ 850 MB. After batch encoding and GDI capture, recording ran at
  ≈ 225–250 MB; with strip capture and I420 staging it runs at ≈ 160–170 MB (status window open;
  ≈ 88 MB since switching Avalonia to software rendering),
  with a short spike while a segment is encoded. Most of what is left is Avalonia/.NET.
- An 8-hour day (from the developer's first recordings, all four monitors): ≈ 75–100 MB, against
  ≈ 240 MB for TimeSnapper's active-window JPEGs (its archive averages 123 MB per calendar day).
- For comparison, TimeSnapper itself (a .NET Framework WinForms app) runs at ≈ 43 MB private memory.
- Playback decoding (libaom 3.15, screen content at q40): key frame 50–100 ms, inter frames ~12 ms single-threaded
  (~8 ms with 4 threads) for 3440×1440. Decoder memory grows by one ~9 MB frame buffer per frame until all 8
  reference slots are filled (the encoder uses them all); one decoder's memory is ~62 MB at 1920×1080 and
  ~144 MB at 3440×1440 with one thread, ~190 / ~225 MB with 2–8 threads.
- Day review, all four monitors (2026-10-16, software rendering, private memory): ~88 MB recording only →
  ~640 MB peak while stepping or playing → ~260 MB once decoders close after 15 s idle → ~100 MB after the window
  closes. Before the fix: up to ~2.8–3.1 GB, and memory stayed high after closing.
- The developer's previous TimeSnapper archive: 415,736 captures (Dec 2025 – Oct 2026), 34.7 GB of JPEGs
  at ~85 KB each.
- Reclaiming space (2026-10-16, copy of real data): deleting 00:12–00:18 from a 90-frame 3440×1440 segment
  (2.06 MB) rebuilt it as 55 frames, 1.30 MB (37% freed), in 11–18 s. Peak while
  rebuilding: +320 MB private, the same as an ordinary staged encode (+302 MB measured the same way); back to
  baseline afterwards. Twice-encoded vs once-encoded frames: PSNR (luma) mean 48.3 dB, min 38.1 dB; the frames
  before the gap 47–63 dB, after it 38–48 dB (predicted across six minutes of change). Small text (taskbar clock,
  terminal, IDE file list, Notepad++ lists and status bar) reads the same at 1:1 in the worst frames, with some
  extra ringing at edges; no stale or wrong text seen. Recording memory unchanged (three alternating runs each:
  90/81/81 MB vs 92/82/82 MB before).
- Images (2026-10-17, one real capture of each monitor, single-threaded, libjpeg-turbo 3.2 / libwebp 1.6 method 4):

  | Monitor | JPEG 15 | JPEG 30 | WebP 10 | WebP 25 | WebP 50 | AV1 key frame q40 |
  |---|---|---|---|---|---|---|
  | 3440×1440 (IDE, browser) | 204 KB, 22 ms | 316 KB | 165 KB, 393 ms | 215 KB, 380 ms | 296 KB | 457 KB, 3.7 s |
  | 1920×1080 (busy) | 83 KB, 9 ms | 118 KB | 61 KB, 166 ms | 76 KB, 145 ms | 101 KB | 128 KB, 0.4 s |
  | 2048×1280 (wallpaper) | 36 KB, 11 ms | 62 KB | 23 KB, 182 ms | 33 KB, 192 ms | 51 KB | 97 KB, 0.9 s |

  Decoding: JPEG 4–16 ms, WebP 6–36 ms. AV1 inter frames are ~10–30 KB for all monitors together, so images are
  roughly 10–20× the disk of video for the same screens; WebP costs about as much CPU per changed frame as AV1 (~0.2–0.4 s
  for 3440×1440), JPEG about a twentieth of that.
- Recording memory by format (2026-10-17, recorder without the UI, all four monitors, 3 minutes each, run twice
  alternating, ~20 changed monitor captures per run): video 50 MB private (peak commit 58–60 MB), JPEG 15 25 MB
  (38–41), WebP 25 25–26 MB (38–39). Images need no staging (video keeps each monitor's previous frame for its
  deltas, ~17 MB here) and no encoder later (+300 MB for a while every 15 minutes). CPU over the 3 minutes: video
  5.1–5.4 s (before its AV1 encode), JPEG 5.3–5.4 s, WebP 9.0–9.7 s.
- Reading the site (2026-10-17, test windows): after the first read of a window, 0.2–1 ms (the address bar's
  element is remembered per window); the first read 10–30 ms, and up to ~250 ms for a Firefox whose
  accessibility tree isn't built yet (over the 300 ms limit that capture simply gets no site). Average over all
  test windows, popups included: 1.1 ms. UI Automation adds ~7 MB to the reading process; Worklapse recording
  all monitors stayed at 79–81 MB while reading sites. Browsers: a Firefox and a Chrome read every 2 s for 90 s
  against an identical untouched one: same memory growth (Firefox +66 vs +58 MB, Chrome flat), same CPU for
  Firefox, ~1 s more CPU for Chrome over the 90 s (with a full search every read; Worklapse reads a remembered
  element every 10 s). A browser turns its accessibility on when first read and keeps it on until it restarts.

---

## Code layout

```
native/
  src/av1.c                   C wrapper around libaom (encode I420, decode to I420 or BGRA, set options)
  src/image.c                 JPEG (libjpeg-turbo) and WebP (libwebp): encode I420, decode to I420 or BGRA
  src/color.c                 colour conversions (I420 is BT.709 full range; JPEG/WebP files BT.601)
  worklapse_native.targets    MSBuild: builds the native library when needed, libraries static via vcpkg:
                              native/win-x64/worklapse_native.dll (Windows), native/linux-x64/worklapse_native.so (Linux)
src/
  Worklapse.Core/             settings, SQLite storage, recorder loop, platform interfaces, history reading,
                              TimeSnapper import (Import/), what a timelapse holds (Export/)
  Worklapse.TimeSnapperReader/ 32-bit .NET Framework 4.8 helper: reads TimeSnapper's Firebird database with TimeSnapper's
                              own engine and streams it to the import; copied next to Worklapse.exe
  Worklapse.Video/            native interop, IVF files, staging files, frame sink (video or images), batch encoder,
                              frame provider for playback, MP4 writer
  Worklapse.Capture.Windows/  GDI screen capture, foreground window, browser site (UI Automation), idle time, lock detection
  Worklapse.Capture.Linux/    KDE Plasma (Wayland): KWin screenshots over D-Bus, a KWin script for the screens and the
                              active window, idle from the Wayland compositor, lock from the screen saver service
  Worklapse.App/              Avalonia app: tray icon, main window (rail; Overview, Day review, Timesheet), settings dialog
tests/Worklapse.Tests/        xUnit v3 tests (storage, recorder, staging, AV1 round trip, playback frames, history, reclaiming space, sites, monitor layouts, images)
docs/PROJECT.md               this file
docs/HANDOFF.md               briefs for the next tasks, for a new session
docs/HANDOFF-LINUX.md         the Linux port's brief: smoke testing and finishing it on Kubuntu
```

### How recording works

1. `Recorder` (Core) ticks every interval on a background task. It skips the tick when paused or locked,
   records an idle capture when there's been no input for the idle limit, and otherwise:
2. asks `IForegroundWindowProbe` for the focused window (app, title, bounds, and for a browser the site) and
   `IScreenSource` for the monitors attached; if the app or site is excluded (Settings → Programs) it records
   nothing for this tick; otherwise it records the monitors as the capture's layout (idle captures too), and
   captures each monitor chosen by the capture mode;
3. reuses a monitor's previous frame when the new capture's pixel hash equals that frame's (the recorder keeps the
   hash with the frame; comparing per device name went wrong after a replug), otherwise appends the
   capture to `FrameSink` (in active-application mode, everything outside the window is blacked out first), which
   stages it for video or writes it as an image (see How images are stored);
4. writes the capture, its layout and frame references in one SQLite transaction.
5. When a segment has 90 frames (or recording pauses), `BatchEncoder` encodes it to AV1 on its own thread,
   rewrites the frame offsets to point into the `.ivf` file, marks the segment encoded and deletes the staging file.
   Frames whose captures were deleted while it was staged are left out (a frame no `frames` row refers to by then
   counts as deleted, so every appended frame must get its `frames` row in the same transaction).

### How plugging monitors in and out is handled

A `monitors` row is a monitor *as arranged*: device name, position, size and primary flag. Windows hands the
`\\.\DISPLAYn` names out again and moves monitors when displays come and go (on 2026-10-17: the laptop panel
alone as primary, then the ultrawide left of it, then the final arrangement, within 20 s), so a replug adds rows,
several of which can occupy the same place. Hence:

- every capture stores its layout (`layouts`: the ids of the monitors attached, captured or not);
- when a monitor leaves the layout, the recorder closes its open segment (`IFrameSink.Close`), so it's encoded
  then rather than at the next pause, and forgets its last frame (frames are only reused within an open segment);
- Day review shows the monitors of the current capture's layout. Its Monitor list is rebuilt when the layout
  changes; a monitor the user picked stays picked while attached, else the one in its place, else all monitors,
  and comes back when it's attached again;
- captures recorded before schema 6 get a layout from `MonitorLayouts.Guess`: a capture with frames of several
  monitors had exactly those; one with a single frame (active application, primary monitor) had that monitor plus
  the day's other monitors with frames, nearest in time first, skipping any that can't have been attached at the
  same time as one already in (overlapping it, sharing its device name, or a second primary); an idle capture takes
  the nearest earlier layout. On the 2026-10-17 recording this gives the real arrangement except during the 20 s of
  the replug and, until 13:39, without the lower-left monitor (its old device name was then the panel's);
- *Selected monitors* match `MonitorInfo.StableId` (Windows: `EnumDisplayDevices` with
  `EDD_GET_DEVICE_INTERFACE_NAME`, e.g. `\\?\DISPLAY#GSM77BE#5&3a2dcb36&0&UID4624#{…}`: the EDID model and the
  connector; ~0.3 ms per monitor per tick). The Settings dialog keeps selected monitors that aren't attached.

### How sites are read (Windows)

`Win32ForegroundWindowProbe` hands browser windows (chrome.exe, msedge.exe, firefox.exe) to `BrowserSiteReader`,
which talks to the browser through the UI Automation COM API (declared in `UiAutomationInterop.cs`; no WPF) on
an MTA thread of its own. A capture waits at most 300 ms; if the answer isn't there, or an earlier read is still
stuck in a hung browser, the capture has no site. Per window it finds the address bar once (Chromium: class
`OmniboxViewViews`; Firefox: id `urlbar-input`) and remembers it, so later reads are one or two property reads.
If the address bar gives no site (it has the focus, so it may hold typing; or Firefox shows search terms on a
search results page), it reads the URL of the page on screen instead (the visible Document element's value).
`SiteDomain.FromAddressBar` (Core, platform-neutral) turns the text into a host name: lower case, readable
internationalized names, no port, no leading `www.`; browser pages, files, extensions and searches give none.
Private windows give none: Firefox and Edge say so in the window title ("Private Browsing", "[InPrivate]");
Chrome only in its top view's UI Automation name ("(Incognito)"), read once per window. These are English
labels; a browser in another language needs its words added.

### How capture works on KDE Plasma (Linux; built 2026-10-19, not yet run on a real desktop)

Plasma Wayland only so far (`KdePlatform.Create`). Anywhere else, or when it can't start, the app runs on
`PlatformServices.Unavailable`: nothing is recorded, the status bar and message bar say why, and Day review, the
timesheet and Settings work as usual (decided 2026-10-19; this applies on Windows too). Avalonia 12 has no Wayland
backend, so the UI itself runs under XWayland; capture goes through KWin instead.

- **Permission.** KWin only answers `org.kde.KWin.ScreenShot2` for an executable named by the `Exec` of an
  installed desktop file that lists the interface under `X-KDE-DBUS-Restricted-Interfaces` (it compares canonical
  paths). `DesktopEntry` writes `~/.local/share/applications/worklapse.desktop` for the running executable at every
  start (only when it changed) and runs `kbuildsycoca6`. So run the `Worklapse` executable, not `dotnet Worklapse.dll`.
  The file also puts Worklapse in the application menu.
- **Screens and the active window.** `KWinSession` connects to the session bus and loads `KWinScript.js` into KWin
  (`org.kde.kwin.Scripting`), which reports the screens and the active window (pid, caption, class, frame) as JSON to
  Worklapse whenever they change; reports are only accepted from KWin's bus name. The program's name is the file name
  of `/proc/<pid>/exe` (`firefox`, `code`), or the window's class for programs that host others (python3, java,
  electron…).
- **Coordinates.** KWin works in logical units (a 150 % screen is smaller than its pixels). `ScreenLayout` places
  each screen at its logical position times the largest scale and gives it its own pixel size, so monitors never
  overlap and a window converts exactly within the screen showing most of it. The first capture of a screen fixes
  its exact pixel size. KWin doesn't tell scripts which screen is primary, so none is marked primary (monitors are
  just numbered) and Settings doesn't offer "Primary monitor" (`PlatformServices.KnowsPrimaryMonitor`; the
  developer's choice 2026-10-19: don't pretend to know).
- **Pixels.** `KWinScreenSource` calls `CaptureScreen(name, {native-resolution, no cursor}, pipe)`; KWin replies
  with the size, stride and format, then writes the image into the pipe, which is read a strip at a time (with a
  deadline), hashed and converted into the shared I420 frame like the GDI capture. KWin only takes screenshots
  while compositing with OpenGL.
- **Idle** from the compositor (`ext-idle-notify-v1` over a minimal managed Wayland client, version 2's input-only
  notifications when offered, as KWin 6.7 does); **lock** from `org.freedesktop.ScreenSaver` (KWin provides it).
- Not done: browser sites (Windows uses UI Automation; Linux would need AT-SPI), X11 sessions, GNOME and others.

### How images are stored

With JPEG or WebP chosen, `FrameSink` encodes each changed capture straight away (`ImageCodec`, native `image.c`) and
writes one file per monitor. Each image is a segment of its own (`segments.format` 1 = JPEG, 2 = WebP; one
`segment_frames` row), so frame references, deleting and playback work as for video:

- **Open while it may be shown again.** The recorder reuses a monitor's last frame when the screen hasn't changed, and
  only ever adds references to segments it is still writing. So an image stays *staged* until that monitor's next
  changed capture (or a pause, lock or unplug) closes it; the batch encoder then "encodes" it, which only marks it
  finished, or deletes it if its captures were deleted meanwhile. Deletes therefore treat open images like the video
  segment being recorded (`CloseSegmentsSoon`).
- **Named once committed.** An image is written as `….webp.tmp` and renamed when the capture showing it is committed.
  If the tick fails, the sink is disposed and deletes it; after a crash, the next sink renames the leftovers whose
  capture was committed and deletes the rest (it looks in the folder of the newest image, the day before, and later).
- **Colours.** Captures are BT.709 full range; JPEGs are written as BT.601 full range through BGRX rows (16 at a
  time), WebP as BT.601 limited range converted plane to plane, so other programs show the right colours (tested
  against the libraries' own decoding) without a full-size BGRA copy.
- **A change of format** applies at each monitor's next changed capture: its open video segment or image is closed
  then, so a day can mix video and images; Day review plays both.
- `FrameProvider` reads and decodes an image file per request (no decoder stays open); its cache works the same.

### How deleting removes video

The purpose is that deleted content is gone from the data folder, not disk space (see Decisions). An image is a
segment with one frame: once nothing refers to it, the same path deletes its file (never a rebuild).

`HistoryStore.DeleteMarked` deletes the captures (their `frames` rows cascade) and the apps left without
captures (unless marked productive), and, in the same transaction, queues the segments the captures used in
`reclaim_queue`. It reports whether any of those segments is still staged; if so the app calls
`Recorder.CloseSegmentsSoon`, and the recorder closes its open segments at the next tick (≤ 10 s), before
capturing, so they're encoded without the deleted frames instead of keeping them in staging for up to 15 minutes.
`BatchEncoder` works through the queue one segment per job, between encodes, when Day review asks
(`RequestReclaim`, through the app's current `FrameSink`) and when it starts (deletes made before a crash
or quit). The recorder opens its storage, and so the encoder, on every tick even while paused or locked, so
this doesn't wait for recording to resume. `SpaceReclaimer` then:

- leaves staged segments alone (their encode drops the unreferenced frames);
- deletes a segment no frame of which is referenced any more, with its file;
- rebuilds a segment with any frames unreferenced: decodes the
  frames still referenced (and the ones before them, which they depend on) into a new *staged* segment next to it
  (`<name>-r1.ivf`), then in one transaction inserts it, moves every `frames` row over and deletes the old rows; then
  deletes the old file and encodes the new segment like any other. Decoding to staging first keeps the decoder
  and encoder from being open together. Frame indexes keep their values, so segments can have gaps in them
  (`FrameProvider` maps index → position).

Segments are never changed in place. Files are only deleted through `dead_segment_files`: a file's path is
written there in the same transaction that lets go of it (or before a new one is written), and removed once the
file is gone, so a crash at any point leaves nothing orphaned and no referenced frame lost; the next pass
finishes the job. The recorder only adds references to the segment it is writing (staged), which is why frames
found unreferenced stay that way. Day review may hold references to a segment that was just rebuilt:
`FrameProvider` still serves them while it has the old file open, and otherwise throws
`FrameUnavailableException`, on which the window reloads the day (once per missing frame) and asks again.

The database: every connection sets `PRAGMA secure_delete`, so deleted rows (window titles, app names) are
zeroed rather than left in free pages; when a pass has nothing left to do, the encoder truncates the write-ahead
log (`wal_checkpoint(TRUNCATE)`), which otherwise keeps the pages as they were. The recorder looks apps up on
every capture instead of caching ids, since a delete can remove an app row. Whatever keeps deleted video on
disk after a pass (a segment that couldn't be rebuilt, retried at the next start; a file another program has
open, retried every minute) is in `BatchEncoder.DeletedContentLeft` and shown in a message bar under each view's tool bar until it's gone.

### How playback works

`HistoryStore` loads a day (captures with their layouts, frame references, monitors). `FrameProvider` decodes any frame,
whether its segment is staged or encoded; it keeps one single-threaded decoder per monitor positioned in
that monitor's current segment, so stepping forward decodes one frame. Stepping back restarts from the
segment's key frame; on the way it keeps the two frames before the target, so the next steps back are free.
Frames are `DecodedFrame`s: packed I420 in pooled buffers, reference-counted between the provider's 64 MB
cache and the window (each holder calls `Release`). `FrameRenderer` runs the provider on a background
thread, newest request wins, and closes decoders unused for 15 s. `DayReviewWindow` converts each frame
straight into its `WriteableBitmap`, and closing the window runs an aggressive GC so the memory goes back to
the OS. `TimelineControl` and `FrameView` are custom-drawn controls; `DayReviewViewModel` holds the playhead,
selection, summaries and the mark/restore/confirm deletion logic.

### How timelapse export works (built 2026-10-21)

Day review's tool bar has **Export Timelapse…** (icon only when the window is too narrow for its label). The dialog
(`TimelapseExportWindow`) picks the time (whole day, one project's blocks that day, or the selection, which is the
default when there is one), speed (10/15/30/60 frames per second, one capture per frame), video size (720p to 4K: the
largest size, the picture keeps its shape), the time of day in the bottom right corner, and whether idle time is kept;
the choices are remembered in settings.json (`Timelapse*`). The picture is Day review's: its monitor choice (found
again by place after replugging; captures where it wasn't attached are left out) and Focus on Active App. Captures
marked for deletion are never exported. The developer chose all of this, and AV1 in MP4, on 2026-10-21.

- `CapturePictures` (Core/History) decides what a capture shows, for Day review and the export alike; `TimelapsePlan`
  (Core/Export) lists the frames and the video size (the largest picture of the export fitted in the chosen size, even
  sides; frames of other shapes get black bars).
- `TimelapseExporter` (App/Export) decodes and draws on one thread and encodes on another (two frames ahead). Drawing
  is SkiaSharp: each picture is first shrunk with `I420.Reduce` (box average) to at most twice its drawn size, then
  bilinear; Skia's mipmaps cost ~45 ms a frame at 3440×1440, rebuilt every frame. The frame then goes to limited
  range (`I420.ToLimitedRange`), what players assume.
- Encoder: libaom good quality, speed 6, quantizer 32, low delay, a key frame every 2 s for seeking, 4×2 tiles, up to
  12 threads; tagged BT.709 limited range (`wl_enc_create`'s `limited_range`, ABI 5). Realtime mode was 3× faster
  but blurred light grey text at the same quantizer and made ~1.6× larger frames.
- `Mp4Writer` (Video) writes the MP4 itself: ftyp, the frames in one mdat as they come (temporal delimiters left out),
  then moov with the index (co64, stss for key frames) and av1C/colr from the first sequence header (`Av1SequenceInfo`).
  The file is written as `<name>.tmp` and moved over the target at the end, so cancelling or failing leaves an
  earlier file as it was. Frames that moved meanwhile (space reclaimed, today's segment encoded) make it plan again
  from the reloaded day, once per missing frame.
- Measured 2026-10-21 on the developer's real 2026-10-20 (i7-14700HX): Focus on Active App, 3,829 frames at 1920×860:
  5 min (12.7 frames/s), 88 MB; encoding is the limit (~60 ms a frame), drawing ~28 ms. Checked: plays and seeks in
  Chromium; Windows reads its properties and Media Foundation decodes a frame with the right colours. Not tried: VLC,
  macOS/iOS (older Apple devices can't play AV1).
- Possible speed-up, not built: encode alternate 2-second chunks with two or three encoders at once (each chunk starts
  with a key frame anyway); needs its own decoders per encoder, or a chunk of frames held in memory.

### Storage

Default data folder `%LOCALAPPDATA%\Worklapse` (Linux: `~/.local/share/Worklapse`); settings in
`%APPDATA%\Worklapse\settings.json` (Linux: `~/.config/Worklapse`). The data folder holds
`worklapse.db`, `video/YYYY-MM-DD/HHmmss-m<monitor>.ivf` (plus `.ivf.staging` while a segment is open) and, when
recording images, `images/YYYY-MM-DD/HHmmss-m<monitor>.jpg` or `.webp`.

SQLite schema (version 12, migrations in `WorklapseDatabase`; the project tables of 9–11 are described under 1a):

- `apps` (exe name, path, `productive` and `excluded` flags; classified by exe name; deleted with their last capture unless
  productive or excluded)
- `sites` (domain, `productive` and `excluded` flags; deleted with their last capture unless productive or excluded)
- `monitors` (device name and geometry; a monitor as arranged, so replugging adds rows, see above)
- `layouts` (the monitor ids attached at the same time, ascending, comma-separated; never deleted)
- `captures` (UTC ms + local offset, active/idle, app, site, layout (null before schema 6), window title and bounds, `pending_delete`)
- `segments` (monitor, relative path (indexed), size, quality, `state` 0 = staged / 1 = encoded, `format` 0 = AV1 video /
  1 = JPEG / 2 = WebP; an image is a segment with one frame)
- `segment_frames` (segment, frame index, file offset and length, key flag; indexes can have gaps after deletes)
- `frames` (capture × monitor → segment frame; unchanged monitors point at an earlier frame; indexed by segment frame)
- `reclaim_queue` (segments that may hold unreferenced frames, with a generation counter)
- `dead_segment_files` (segment paths whose files are to be deleted; see How deleting removes video)
- `timesnapper_imports` (TimeSnapper `ACTIVITY.ID` → the capture imported from it, NULL once deleted; schema 12).
  Imported pictures are image segments whose `path` is **absolute** (TimeSnapper's Snapshots folder), on a monitor
  named `TimeSnapper`; everything that resolves a segment path (`Path.Combine(dataFolder, path)`) handles that.

---

### Errors and the log

`Core/Diagnostics/Log`: `Logs/worklapse.log` beside `settings.json` (so `WORKLAPSE_CONFIG_DIR` moves it too), started
in `Program.Main`; nothing is written before that, so tests and tools write nowhere. Past 1 MB it starts over and
keeps `worklapse.old.log`. Entries: start (version, OS, .NET), quit, errors with their stack traces.

- **Another thread's exception** ends the process (.NET can't prevent that): `AppDomain.UnhandledException` logs it as
  FATAL and leaves `crash.txt`; the next start shows *Worklapse closed unexpectedly* with its message and time
  (`ErrorWindow`, with Open Log Folder). Unobserved task exceptions are logged.
- **A UI-thread exception** (`Dispatcher.UIThread.UnhandledException`) is logged and shown in `ErrorWindow`, and
  Worklapse carries on: recording doesn't depend on the windows. One that repeats while the window is open is logged
  once. During startup (before the tray exists) it isn't caught, since a half-started app would run with no window
  or tray icon; it ends the process and is reported at the next start.
- **The recorder** logs each error once per distinct message (not every tick it lasts) and when it recovers.
- **The batch encoder**'s thread can't die from an error any more: its database is opened in the constructor (a
  failure there reaches the recorder, which retries), an encode failure sets `EncodingProblem`, and an error outside
  a segment's rebuild (the database busy, say) is reported like deleted content left on disk and retried every minute.

### Tray icon and notifications

`App/Tray`: `IAppTray` with two implementations.

- **Windows, `Win32Tray`**: Shell_NotifyIcon directly (NOTIFYICON_VERSION_4) on a hidden top-level window (top-level
  so it hears `TaskbarCreated` and adds the icon again when Explorer restarts or wasn't up yet). Notifications are
  balloons (`NIF_INFO`, the app icon large, respecting quiet hours), which Windows 10/11 show as toasts; clicking one
  or the icon opens the main window. Avalonia's tray icon can't show notifications, hence its own. The menu is
  `TrayMenuWindow`, Avalonia's menu look in a borderless window, as Avalonia's own tray draws it.
- **Linux, `AvaloniaTray`**: Avalonia's tray icon (StatusNotifierItem), notifications through
  org.freedesktop.Notifications (`Capture.Linux/DesktopNotifications`, its own session-bus connection, clicking opens
  the main window).
- **Badge** (`TrayIconImages`): a dot cut out of the .ico's picture for the size: red recording, amber paused, red with
  a cross on error, none when idle, locked, excluded or starting. Fixed colours (the task bar needn't match the
  windows' theme).
- **Notifications**: *recording started* once per run, the first time recording is under way after starting by
  itself (not after the developer's own pause or resume); *still running in the tray* once per run, when the main
  window is closed (not when quitting).

### Starting at login

`App/LoginStartup`. The OS holds the setting, not `settings.json`: read when Settings opens, written on Apply (only when
changed). Windows: the `Worklapse` value under `HKCU\…\CurrentVersion\Run`, `"<exe>" --autostart`; Task Manager's
Startup apps switch (`Explorer\StartupApproved\Run`, first byte odd = off) counts as off, and turning it on in Worklapse
clears that switch. Linux: `~/.config/autostart/worklapse.desktop` (`Capture.Linux/AutostartEntry`, the menu entry's
contents plus `--autostart`); `Hidden=true` or `X-GNOME-Autostart-enabled=false` counts as off. Not offered when
running through the dotnet host (no executable to start). The entry names the executable that applied it, so applying
from a debug build points login at that build. With `--autostart` the main window isn't opened; the *recording*
notification says Worklapse is running (with *Record when Worklapse starts* off it starts silently, paused).

## Building, running, testing

Requirements: .NET 10 SDK, plus what the native library needs, which `dotnet build` builds when needed (several
minutes the first time; see `native/README.md`): on Windows, Visual Studio 2026 or its Build Tools with the C++
workload; on Linux, gcc, nasm, pkg-config and a vcpkg clone (`VCPKG_ROOT`). From the repository root:

```sh
dotnet build
dotnet test tests/Worklapse.Tests
dotnet run --project src/Worklapse.App
```

Worklapse starts with its main window on the Overview and a tray icon; closing the window keeps it recording.
Quit from the tray menu. Only one instance runs at a time.

Testing without touching real data: set `WORKLAPSE_CONFIG_DIR` to a folder containing a `settings.json`
whose `DataFolder` points somewhere disposable (copy the real data folder there to test Day review on real
recordings). Captures are idle (no frames) while nobody uses the keyboard or mouse, so for unattended
test runs set `"IdleMinutes": 120` in that settings file.

The UI can be driven for screenshots with Windows UI Automation from PowerShell (find a button by name,
invoke it, render the window with `PrintWindow`); icon-only buttons carry `AutomationProperties.Name`
for this and for screen readers. Lessons from 2026-10-17: the developer may be using the machine, so never
take the focus or send keys; only the selected tab's controls are in the tree, so check which control you type
into (a script once typed into the History folder box and pointed a test run at the real data); and check the
test `settings.json` still points at the copy after each step.

---

## Status (2026-10-17, updated after image recording)

Working on Windows:

- Recording of all four capture modes, idle and lock detection, unchanged-frame reuse, batch AV1 encoding,
  crash recovery of staged segments.
- Status window and tray menu (pause/resume, Day review, settings, quit); Settings dialog (capture mode,
  monitors, interval, idle, lock, recording format and quality, history folder; an empty folder is refused, Default
  fills in the standard one, the folder button opens it in the file manager).
- Productive apps and websites (2026-10-17): sites read from Chrome, Edge
  and Firefox; Settings → Programs lists every app and site with its recorded time (filter, sort, checkbox,
  applied with Apply/OK); right-click in Day review on the frame (its site and app) or the timeline (each app and
  site in the selection, up to 12, with its time there); the site shows in Day review's status bar.
- Day review: timeline of the recorded hours, all or one monitor, zoom/pan, ← / → frame stepping,
  Shift+arrows and drag to select, playback at four speeds, selection/whole-day summary (length, active,
  productive, other, idle, not recorded, marked), mark for deletion / restore / delete permanently, F5 refresh.
- Deleted captures' video is removed from disk in the background (2026-10-16/17): whole segments deleted, partly deleted ones rebuilt, staged ones (including the one being
  recorded, closed at the next tick) encoded without the deleted frames, also while paused; titles and app
  names zeroed in the database; failures shown in the status window; crash-safe, and Day review reloads when a
  frame it shows has moved.
- Breeze-style theme across all windows, at desktop density, coloured to match the OS (Windows 11 light/dark + accent, KDE colour scheme, Adwaita on GTK desktops, Breeze elsewhere), switching live (2026-10-18; Windows high contrast is not handled yet).
- An unreadable `settings.json` (bad JSON, unknown value) no longer crashes at startup: a window shows the
  file and the error and offers Quit (to fix it by hand) or starting with defaults, which renames the file
  to `settings.json.broken-<date>`.

- Recording as JPEG or WebP images (2026-10-17): Settings → Recording, each choice
  with a one-line trade-off, a preview of the primary monitor as stored and a running average per capture (2026-10-18;
  measured on a 3440×1440 monitor: while Settings is open, ~+260 MB with Video chosen, the AV1 encoder, ~+45 MB with
  WebP; given back when it closes); a file per changed capture; deletes remove the files; crash-safe naming; Day
  review plays them. Checked with tests (codec
  round trip and colours as other viewers show them, recording, format switching, deletes, failed ticks, crash
  leftovers) and benchmarks on real captures (see Benchmarks); the Settings dialog checked as rendered off-screen
  (Avalonia headless), not clicked. Not yet done by hand: an image recording looked at in Day review.
- Plugging monitors in and out (2026-10-17): layouts recorded per capture,
  Day review and its Monitor list follow them, older captures' layouts worked out; selected monitors by stable
  identity. Checked with tests (recorder with a fake desktop, the guess) and in Day review on a copy of the
  2026-10-17 recording (screenshots at 10:51, 11:30, 14:00 and a capture recorded with the new build; Monitor
  list; picking a monitor across the replug).
- Day review panning and Focus on App (2026-10-18): scroll bars while the
  picture is larger than the view, drag with the left or middle button; a zoom keeps its magnification as frames
  change size (Fit stays fitted). Focus on App (tool bar monitor menu; remembered as `ReviewFocusOnApplication` in
  `settings.json`) shows only the focused window's rectangle (`captures.window_*`) on the monitors recorded, centred:
  for active-application recordings that is just what was recorded; idle captures use the last active one's window;
  a capture without a window, or whose window misses every recorded monitor, shows all monitors. Checked off-screen
  (Avalonia headless, synthetic two-monitor day: all monitors, app-only, idle; zoom, middle drag, scroll bars), not
  by hand.
- Excluded apps and sites (2026-10-18): Excluded column on Settings → Programs
  (schema 8); the recorder skips the tick while one has the focus and the status window shows *Excluded*. Tested
  with the recorder on a fake desktop (`ExclusionTests`) and the Programs page rendered off-screen; not tried in a
  real recording. Also fixed: Settings stayed dirty after Apply (Apply remained enabled).
- Errors, tray and notifications (2026-10-20; the developer chose each option offered): see *Errors and the log* and
  *Tray icon and notifications*. Settings → General → Startup and notifications: *Start with Windows* (*Start when I log in* on
  Linux; see *Starting at login*), *Record when Worklapse starts* (`RecordAtStartup`; off starts paused) and two
  notifications, *When recording starts* and *When closing the window leaves Worklapse in
  the tray*. Checked with tests (`LogTests`, the encoder failure in `ReclaimTests`), the badges, Settings, error
  window and menu rendered off-screen, and the Win32 structures' sizes and offsets against the native ones plus a real
  add and update of the icon; the notifications, the menu and clicks not tried on the desktop yet.
- Day start (2026-10-20): see *When a day starts*. Checked with tests (`DayStartTests`: days split at 04:00 and at
  22:00, summaries, settings to the minute) and Overview, Day review and Settings rendered off-screen on a synthetic
  late night, including Day review reopening on the same date when the setting changes. Also fixed: Day review's
  typed selection times crashed on text no format matched, such as "4" or "25:00" (`ParseClock`'s "H" format).

Tested less or not at all: active-application mode over a longer session, selected monitors (only the matching,
by tests; not the Settings dialog), an actual replug with the new build, rotated displays (GDI should handle them, untested), Store-app window detection,
drag selection and keyboard stepping by a person (only toolbar buttons were exercised automatically), the
Day review right-click menu (built and compiled, not clicked: it needs a real right-click), sites in Chrome and
Edge as the foreground app of a real recording (read correctly from test windows, which couldn't be brought to
the front while the developer was using the machine).

Known gaps and caveats:

- Sites: private windows are recognised by English labels only; Chrome and Edge don't expose the page while
  minimized, so a search-terms or typing fallback can't apply there (their address bar shows the URL anyway);
  other browsers (Brave, Opera, Vivaldi) aren't read; the first read of a Firefox can exceed the 300 ms limit.
  Window titles are recorded for private windows as before (only the site is left out).
- Images: WebP costs ~0.2–0.4 s of CPU per changed large monitor, on the recorder's thread inside the capture's
  transaction (all monitors of a tick are captured one after another, so up to ~1 s apart). A frame read in Day review
  in the instant between its capture being committed and its file being renamed shows "Couldn't read this frame".
- A video segment's staging file created in a tick that a crash interrupted (not an error: those are cleaned up) is
  left on disk with no segment (pre-existing; images have the cleanup described above).
- A segment that can't be encoded stays staged (with any deleted frames still in its staging file) and is retried
  at the next start; the main window's message bar says so (`BatchEncoder.EncodingProblem`) and the log has the
  error. Failures to remove deleted video are shown the same way.
- Deleted content that can still be found: deleted files aren't overwritten (recoverable with forensic tools
  from the unlocked drive; overwriting isn't reliable on SSDs, and BitLocker covers the drive at rest); deletes
  made before schema 4 (2026-10-16) may have left titles in the database's free pages (a `VACUUM` would clear
  them); a frame kept right after a deleted range can carry small stale fragments of the deleted screens (the
  small-text effect in Decisions); and a monitor that still shows the same picture in a kept capture keeps
  its frame, since that capture shows it.
- Monitors: for captures recorded before schema 6, the worked-out layout can keep a monitor that was unplugged
  without another taking its place (shown as "Not recorded", or its last picture while idle), and is rough during
  a replug itself. Device names in a `settings.json` written before stable ids (`\\.\DISPLAY3`) no longer match
  any monitor; choose the monitors again. Stable ids change if a monitor moves to another port or GPU.
- Small text can lag behind in the video (see Decisions).
- Avalonia and .NET are the bulk of the remaining footprint while recording (~88 MB in all with software
  rendering, measured shortly after start); see the recorder/UI split below.

---

## Next steps

The priority is a complete, usable Windows app. Alternatives and ports come after.

### 0. Memory while reviewing — done 2026-10-16

The Day review reached ~2.8 GB. Measured causes (no leak: everything was freed on dispose): ~1.4 GB of
native libaom decoders (8 open, 8 threads each), the 400 MB BGRA cache, ~1–2 GB of committed but unused
large-object heap from a new 20 MB array per decoded frame, and ~450 MB for GPU rendering. Fixed as listed
in Decisions (Playback decoding, Rendering). Now: ~640 MB peak with four monitors, ~260 MB idle, back to
~100 MB after closing.

Still open, if the peak matters:

- Most of the peak is the four open decoders (~380 MB), which hold the 8 reference frames the encoder fills.
  Encoding new segments with fewer reference slots would cut that for future recordings (a format/quality
  trade-off to measure first).
- After 15 s idle the first step costs ~0.7 s while decoders reopen and decode from the key frame; stepping
  back costs ~0.3–1 s every third step for the same reason.

### 1a. Projects, timesheet and a main window — designed and built 2026-10-18

Mockups (four rounds; "Round 4" is the approved one): https://claude.ai/artifact/2HyWXXPGWRR6bJR232Nomx
(private to the developer). Agreed with the developer over the design rounds:

**Main window.** Replaces the status window and the separate Day review window. A left rail of icons only
switches screens: at the top the three views — **Overview**, **Day review**, **Timesheet** — at the bottom
**recording** (state + start/stop) and **Settings** (stays a dialog). The app opens on Overview; closing the
window hides it to the tray and keeps recording. Recording state must be obvious on every screen: status bar
segment (red dot "Recording · capture mode"), rail button; when paused, an info bar under the tool bar
("Recording is paused… [Resume Recording]") and a paused rail icon. The status window's other messages
(recording errors, deleted content not yet removed, Excluded) move to the status bar and the tray tooltip.

**Projects.** Projects belong to categories; a category has a colour and a project uses it unless given its
own. Projects can be archived (hidden from lists, kept in history). Apps and sites get a project link (Settings →
Applications, or Day review); a link marks that app's/site's **active** time with the project (site beats
browser). Idle and unrecorded time stays unassigned unless set by hand. Ranges set by hand (a project, or
"no project") override the links. Projects never overlap. There is no suggestion system.

**Notes.** A note belongs to one project block, never spans projects. Stored with its project and a moment in
time; shown on whichever block of that project contains that moment (blocks are computed and change when links
or ranges change); two notes in one block are joined; a note whose block disappears is kept and listed in the
Projects tab. Plus one note per day.

**Times.** *Active* = time with input, productive or not. *Productive* = active time in productive apps/sites
(the existing per-app flag). *Assigned* = all time marked as the project, idle or not. *Logged* = what the
developer types (hh:mm) to bill, per project per day. Screens show h:mm; CSV export uses decimal hours.

**Screens.**
- *Overview* (home): week mode — one timeline per day (projects above activity), step a week at a time, click a
  day to open it in Day review; below, a tree of totals by category → project (day columns, Assigned, Logged)
  with a "No project" branch (productive / other), and day notes that expand to their block notes. Month mode
  swaps the timelines for a month grid; the tree's columns become weeks.
- *Day review*: as today, plus a Projects lane above the activity lane. Tool bar: day/frame/playback controls
  on the left; monitor menu (with Focus on Active App, which leaves the tool bar) and zoom on the right. Right
  side panel. With no selection, three tabs from raw to curated: **Applications** (the day's apps/sites:
  active time, Productive checkbox, project link), **Projects** (project blocks only, how each was assigned,
  its note; between two blocks of the same project less than 10 minutes apart, a spot offering to join them),
  **Time Log** (Project, Assigned, Logged — editable), and **Marked** (added 2026-10-19, replacing the tool bar's
  Marked button: ranges marked for removal with Go To / Restore, Delete Permanently below). Day note underneath. With a selection (a
  single frame is just a short range): **Recorded** (app runs in order, repeated when switching back, idle and
  not-recorded rows; per-app Productive and Project), **Projects and notes** (each project block in the
  selection with its note box; "No project" time), and an action bar: **Set Project ▾**, **Clear Project**,
  **Mark for Deletion**. The same commands on the timeline's right-click menu.
- *Timesheet*: one line per project per day — Project, Notes (block notes joined), Active, Productive,
  Assigned, Logged (editable cell). Week/month, totals by category, Export CSV.
- *Settings* tabs: General, **Applications** (was Programs; adds a Project column), **Projects** (categories,
  projects, colour, archived, linked apps), **Appearance** (was Application). The Defaults button only on
  General and Appearance. Renamed 2026-10-20 (developer): **Recording** (was General), **General** second (was
  Appearance; startup, notifications and the theme), Applications, Projects.

Desktop idiom (developer, 2026-10-18): no segmented pills (toggle tool buttons or combo boxes), framed tabs,
grids with editable cells rather than a text box per row, no accent buttons in tool bars, no cards or badges.

**Build order** (each phase reviewed before the next; commit only when asked):
1. Data and time calculations, no UI: schema 9 (`categories`, `projects`, `apps.project_id`, `sites.project_id`,
   hand-set `project_spans` with no overlaps, `notes`, `day_notes`, `timesheet`); a Core calculator for a day's
   project blocks and per-project Active/Productive/Assigned; CSV export. Most of the tests go here.
   **Built 2026-10-18** (153 tests pass). Code: `Worklapse.Core/Projects/`
   (`ProjectStore` — categories, projects, ranges set by hand, notes, day notes, logged time; `ProjectTimeline` —
   blocks and totals, `NotePlacement`; `Timesheet`, `TimesheetCsv`), `HistoryStore.SetProject`/`LoadSamples` (was `LoadActivity`, replaced in phase 6)/
   `DayRange`, `CaptureRecord.LinkedProjectId`. Choices made while building, to confirm: a site without a link
   leaves it to its browser's link (unlike Productive, where the site always decides); a note whose block no
   longer contains its moment goes on the nearest block of its project that day before becoming an orphan; deleting
   a project deletes its ranges, notes and logged time (archive keeps them). Agreed after building (developer,
   2026-10-18): **nothing is ever joined automatically** — `ProjectTimeline.JoinableGaps` lists gaps shorter than
   10 minutes between two blocks of the same project (no other project in between), the Projects tab offers to join
   each one, and joining sets the gap to that project by hand; **Clear Project** does what it says: the selection
   ends up with no project (a range set to "no project", overriding links); there is no "back to links" command.
   Schema 9 is one-way: once a build with it has opened the real data folder, older builds can't.
2. Settings tabs: Applications (Project column), Projects, Appearance; Defaults moved into its tabs.
   **Built 2026-10-18.** Tabs General / Applications / Projects / Appearance, since 2026-10-20 Recording / General /
   Applications / Projects (dialog 760 px wide). Defaults stays at the left of the button bar but only shows on
   Recording and General and resets only that page (as KDE's System Settings does). Projects tab (`ProjectsViewModel`): categories with their projects,
   then "No category"; New Project / New Category / Delete (asks first, `ConfirmWindow`, when it's in the history);
   Show archived; editors for name, category, colour (the category's or its own, from a 12-colour palette), linked
   apps and sites (read-only), Archived. Projects made in the dialog can be linked on the Applications tab before
   Apply; Apply saves projects first, then links. Renamed items are re-sorted the next time the dialog opens (re-sorting
   while typing would take the focus). Checked with headless screenshots (light and dark) and an Apply round trip on a
   scratch data folder; 153 tests pass.
3. Main window shell: rail, Day review moved in as a view, tool bar reordered, recording state everywhere,
   close-to-tray; the status window removed.
   **Built 2026-10-18.** `MainWindow` (rail with Overview / Day review / Timesheet, then
   recording and Settings at the bottom; no menu bar since 2026-10-19, its shortcuts are window key bindings:
   Ctrl+1/2/3, Ctrl+, Settings, Ctrl+W, Ctrl+Q, F5 in Day review), `MainViewModel`, `DayReviewView` (was `DayReviewWindow`; tool
   bar: day (with Refresh while it shows today), frames and playback on the left, monitor menu with Focus on Active App and zoom on the right),
   `RecordingViewModel` (was `StatusViewModel`) shown by `RecordingIndicator` (start of each view's status bar) and
   `RecordingMessages` (bars under the tool bar: paused with Resume Recording, not recording, deleted content still on
   disk); the tray menu is Show Worklapse / Pause–Resume / Settings / Quit and its tooltip says the state. Closing the
   window really closes it (its views and the Day review's memory go; recording carries on in the tray); Show
   Worklapse makes a new one. Overview and Timesheet are placeholders until phases 5–6, so the window opens on Day
   review for now. A change of data folder reopens Day review on the new folder. Checked with headless screenshots.
4. Day review labelling: Projects lane, side panel tabs, selection panel and actions, menus.
   **Built 2026-10-18.** The timeline has a Projects lane above the activity (lane names on the left). A selection
   is now a range of time, not whole captures: dragging snaps each end to the capture it falls in, or to the minute
   where nothing was recorded, and the selection panel's start and end can be typed — so time with no recording (a
   call while away, an evening call) can be selected and given a project; the timeline widens to show ranges set by
   hand. Deleting still acts on the captures inside the range. Side panel (toolbar toggle, 400 px, now 440): with no selection,
   tabs Applications (the day's apps and sites: active time, Productive, Project link — saved at once, all history),
   Projects (blocks with "via Code.exe" / "set by hand", a note box each, and a Join spot between blocks of the same
   project under 10 minutes apart) and Time Log (Assigned, Logged typed as 4:45 / 4.75 / 4h45 / 45m, saved to the
   timesheet), plus the day note. With a selection: start/end boxes and length, active/idle/not-recorded bar, the
   Recorded runs (an app listed again each time you switch back; Productive and Project per app or site), Projects
   and notes (one note box per block in the selection) and Set Project ▾ / Clear Project / Mark for Deletion. The same
   commands are on the timeline's right-click menu. Code: `DayReviewViewModel.Projects.cs`,
   `DayReviewView.axaml`, `TimelineControl` (`ProjectLaneBlock`). Checked with a headless harness on a scratch day.
   **Changed 2026-10-19 after the developer used it:** a recorded segment's Project (in a selection's Recorded list)
   sets that stretch of time only, as a range set by hand ("None": no project there), not the app's link for all
   history — linking stays on the Applications tab; the combo shows the stretch's project, or "Mixed" when it's partly
   one project and partly another or none; Productive is still the app's. The same segments for the whole day (first
   capture to last) are a new tab, **Recorded Segments**, after Applications. The side panel is 440 px (segment rows
   didn't fit), lengths in segment rows are h:mm. Fixed: Set Project ▾ and the monitor menu came up empty — Avalonia
   12's menu flyout takes its item collection when its popup is made, before Opening, so a collection handed over in
   Opening never showed; each flyout now keeps one collection, refilled when it opens. Also fixed: Day review played
   on its own when a day was opened from the Overview — its play timer was made with the `DispatcherTimer`
   constructor that takes a callback, which starts it; unseen while Day review opened at a day's last capture (the
   first tick stopped at the end), it played the day through once days opened at their first. The developer expects
   to iterate on how the side panel assigns time to projects: handed off as a design task in `docs/HANDOFF.md`
   (2026-10-19).
5. Timesheet view. (Phases 5–6 were handed off on 2026-10-18; that HANDOFF.md is in git history.)
   **Built 2026-10-18, committed (b903c08) after the developer looked at it.** `TimesheetView` / `TimesheetViewModel`: tool bar ‹ range ›,
   This Week (This Month), Week / Month (a radio pair drawn as toggle tool buttons, so clicking the one that's on
   leaves it on), Export CSV… (a save dialog; the range read again, written by `TimesheetCsv` as UTF-8 with a byte
   order mark for spreadsheets; a failure, such as the file open in a spreadsheet, shows in a bar under the tool
   bar). The grid: a heading row per day that has lines or a day note (the date is a link to Day review, as is
   double-clicking the row; the day note; the day's totals), then a line per project (colour, name with
   "(archived)", notes joined or *No notes*, Active, Productive, Assigned, Logged typed as on the Time Log and saved at
   once, empty clears it); week or month totals at the bottom; beside it, Totals by category → project (Assigned,
   Logged; projects without a category under "No category"). Loaded off the UI thread with a connection of its own
   (`TimesheetData.Load`; a newer load wins, a load finishing after the window closed is dropped), when the view is
   shown, when the window is activated (Day review and Settings share the database) and after a data folder change;
   rows are kept by day and project, so those still there are updated in place (today growing while recording) and a
   cell being typed in keeps its text (refresh rules and row keeping revised after review, see phase 6). Logged time typed for a project deleted meanwhile isn't saved; the view reloads. Shared grid
   styles (`Grid.gridHeader`, `Border.row`, `TextBox.cell`) and a `Button.link` style moved to `Styles/Breeze.axaml`.
   Measured: a month (30 days × 8 h at 10 s, 86,400 captures) loads in 140–190 ms with ~50 MB of short-lived
   allocations. Checked with a headless harness on a scratch October (screenshots light and dark, week, month, an
   empty month; toggles clicked; typing, clearing and invalid logged time; a project deleted while open; export and
   export to a locked file; opening a day). Not seen: the save dialog (headless can't show it).
6. Overview (week, month; month totals need a lighter query than loading every capture).
   **Built 2026-10-18.** The app opens on it (and so does the tray's Show Worklapse
   when the window was closed). `OverviewView` / `OverviewViewModel`: tool bar ‹ range ›, This Week (This Month),
   Week / Month (the radio pair as in the Timesheet). Week: a row per day (date and first–last time, a `DayStrip`
   — projects lane above a thin activity lane in the timeline's colours, on one axis for the week in whole hours,
   with today's time marked — and the day's Assigned · recorded); clicking a row opens the day in Day review.
   Month: a calendar of whole weeks, each day of the month with Assigned · recorded and a `ShareBar` of its time by
   category, time without a project hatched; days of the neighbouring months only show their number; clicking a day
   opens it. Below: the totals tree (category → project, then "No category", then **No project** with Productive and
   Other; categories expand and collapse and stay so across reloads), a column per day or per ISO week (only the
   month's days counted), then Assigned and Logged; footer rows Assigned and Recorded (time captured, active or idle).
   Beside it: the days with a day note or block notes, each expanding to its block notes (project, block time,
   note; notes whose block is gone, without a time). Status bar: recording, then the period's assigned, logged and
   active-without-a-project totals. Month cells show Assigned · recorded, as the week rows and the board do (the
   handoff said "logged and recorded"; the board's numbers were assigned time). **The lighter path:** measured
   first — building every day from `LoadActivity` took 140–190 ms and ~50 MB of short-lived allocations for a month
   (30 × 8 h at 10 s). Now `HistoryStore.LoadSamples(from, to)` reads the whole range in one query as small
   `ActivitySample` structs (time, kind, productive and linked project resolved in SQL, no names or titles) and
   `DaySummaries.Build` runs the same `ProjectTimeline.Build` per day on them, plus `ProjectTimeline.Activity` (runs
   for the lane) when asked: 90–140 ms and ~26 MB for a month, most of it the query itself, so no cache. The
   Timesheet uses the same path. `ProjectTime` gained `Recorded` and `Assigned`. Shared with the Timesheet: `Period`
   (week/month stepping and titles), `ProjectCatalog` (names with "(archived)", colours, categories). Loads like the
   Timesheet (own connection, newest wins, dropped after the window closes; on show, on activation, after a folder
   change). Checked with tests (samples equal the full day's captures; per-day summaries, extent, recorded,
   activity runs, a day with only a range set by hand, an empty day) and the headless harness (week, month, empty
   month, light, dark, recording paused; collapsing a category across Week → Month; clicking a month day and a week
   row opens the right day — this caught every month day opening the grid's last day, a loop variable captured
   by the commands).
   **Reviewed 2026-10-18** by two separate reviewers (correctness against this brief; engineering), then changed:
   - Opening a day from the Overview or Timesheet read it two or three times on the UI thread (Day review made on the
     latest day, then the day, then the refresh on show). Now Day review can be made on a day, and `MainViewModel`
     (`RefreshView`) skips the refresh of a Day review just read; the window only refreshes on a change of view (not
     when opening a day makes Day review in the background), and once when it's made (a window shown from the tray
     isn't always activated).
   - Refreshing: on show and after a folder change as before; after the Settings dialog closes (`App` asks the
     window; projects live in the history, not the settings file); on window activation only when the period holds
     today (a month was ~100 ms and ~26 MB of garbage per Alt-Tab; past periods change only through Day review and
     Settings, which refresh on their own).
   - `PeriodViewModel<TData>`: the stepping, Week/Month pair, background load (newest wins, dropped after close) and
     error handling the two views had twice. Read errors are SQLite, file and access errors and the new
     `DatabaseVersionException` (a data folder from a newer Worklapse used to throw a plain `InvalidOperationException`,
     which would have crashed the app from the views' `async void` refresh). A failed load clears what the view showed
     (the Overview kept the previous period's figures under the new title).
   - `Period` and the tree's arithmetic moved to Core and are tested (`PeriodTests`): `Period.ColumnOf`/`ColumnStart`/
     `GridStart`/`GridEnd`, `PeriodTotals.Build` (assigned per project per column, logged, no-project productive and
     other, recorded). Switching Week ↔ Month now keeps to the same time: the period holding today becomes the one
     holding today; otherwise a week becomes the month its Thursday is in and a month the week of its first Thursday,
     so switching back returns to where it was (before, a week straddling two months gave the earlier month even
     when today was in the later one, and March 2026 → week → month gave February).
   - Timesheet: a save starts a fresh load (a load begun before the save would have shown the old value); rows are
     synced by key with Insert/Remove/Move instead of rebuilt; `ProjectStore.ProjectExists` instead of listing
     projects; double-clicking the date link opens the day once.
   - `WorklapseSettings.MaxCaptureLength` (one and a half intervals) replaces five copies of the rule. A day's extent
     leaves out captures marked for deletion. The week's axis can reach 25 h (the day the clocks go back) and strips
     clip rather than squeeze what's outside it; hour labels are spaced by their measured width; project brushes are
     made once per colour; `ThemedControl` holds the theme subscription the custom controls shared.
   - Confirmed by the developer (2026-10-19): the Timesheet's day and footer totals add up the project lines only
     (time without a project is the Overview's business); Logged cells save when they lose the focus, as on Day
     review's Time Log; month cells show Assigned · recorded. Not handled: on a day the clocks change, the timelines
     measure elapsed time from midnight (as Day review does), so that day's strip is an hour off the week's hour
     labels; the developer's province (Saskatchewan) doesn't change its clocks.
   160 tests pass.

**Day review side panel, redesigned 2026-10-19 (agreed with the developer, not yet built).** Mockups: the canvas's
**Round 5** page. Agreed:
- *Scopes.* Two radio tool buttons at the top of the panel, **Day** and **Selection**. Selection is disabled without a
  selection. A selection made on the timeline switches the panel to Selection. Clearing it goes back to the scope
  shown before. Switching to Day keeps the selection on the timeline.
- *Day tabs:* Recorded Segments, Applications, Time Log, Marked (Recorded Segments first since 2026-10-19, "that
  seems to be where I'm living"); the day note underneath. The Projects tab goes, and
  with it the Join spots (selecting the gap and choosing Set Project does the same). Time Log and Marked exist only
  at day level.
- *Selection:* start and end boxes, the bar, then tabs Recorded Segments (clipped to the selection), Applications and
  Projects (each project's time inside the selection, no notes). The action bar is underneath: Set Project ▾,
  Clear Project, Mark for Deletion.
- *Frame:* always shown as a slice of the panel rather than a scope (developer's call: "always visible, as a small
  slice"). It shows the frame's time, app or site, its segment's range and length, Productive (the app, every day)
  and Project (that segment only). At the top of the panel. The status bar's frame line goes (the slice says it).
- *Notes:* one note per project per day, plus the day note. Block notes and `NotePlacement` go (schema 10; there are
  no notes in real data to migrate). On the Time Log, a note line under each project. In the Timesheet, the Notes
  column becomes editable.
- *Productive stays per app or site.* The developer never meant per stretch.
- *Highlighting.* Pointing at (or focusing) a control lights up on the timeline exactly the time it would change on
  this day: the rest of the lanes dim and a bar runs under each affected stretch.
  - Productive: the app's or site's own time. A browser's flag doesn't reach pages with a known site.
  - An app's link: its active time not set by hand, plus, for a browser, sites without a link of their own.
  - A segment's Project: that stretch.
  - A Time Log row: that project's time.
  - An open row menu: what it acts on.
- *Picking rows.* Click a segment row (Idle and Not recorded too) or a Marked row to select its time; Shift or
  Ctrl+click extends the range to cover everything between. **One range only**: the developer considered several and
  chose one. App rows and Time Log rows stand for scattered time, so they highlight but don't select. Picking rows in
  Day doesn't switch the panel to Selection.
- *Row menu (right-click).* Acts on the selection when the row is inside it, else on the row. Items: Select, Extend
  Selection to Here, Start Selection Here, End Selection Here, Set Project ▸, Clear Project, Mark for Deletion,
  Mark ‹app› as Productive/Other, Go To.
- *Timeline.*
  - A click inside the selection moves the playhead; a click outside also clears the selection.
  - Dragging near an edge moves that edge (↔ cursor).
  - Shift+click moves the nearer edge.
  - A drag elsewhere starts a new selection; Esc clears.
  - Double-clicking a project block or an activity run selects it.
  - Zoom (built 2026-10-20): the wheel zooms at the pointer, down to 5 minutes across; Shift+wheel or a sideways
    wheel scrolls, and so does the scroll bar under the lanes. A drag held past either end scrolls that way. Labels go
    down to minutes. The playhead is kept in view when it moves (to the middle after a long jump). Another day starts
    zoomed out; Refresh keeps the zoom.
- *Resizable panel.* Drag its left edge, 400–720 px (360 in the mockup left no room for names in segment rows); the width is remembered.

**Built 2026-10-19** (commits after 3a41ae3).
- *Notes:* `ProjectStore.GetNotes(from, to)` / `SetNote(day, project, text)` on `project_notes` (schema 10; the
  `notes` table is dropped). `TimesheetLine.Note` is one string; the Timesheet's Notes cells are typed in and saved like
  Logged. The Overview lists a day's project notes under its day note.
- *Reach:* `MarkReach.Productive` / `MarkReach.Link` in Core (tested).
- *Panel:* `DayReviewViewModel.Panel.cs` holds the scope, the frame slice (`FrameSegment`, a `SegmentRow` worked out
  when the playhead leaves the one shown), picking, the row menu's actions and the reach.
- *Timeline:* `TimelineControl` raises `Clicked(seconds, shift)`, `EdgeDragged` and `DoubleClicked`; the view model
  decides (`Click`, `DragEdge`, `DoubleClick`). It's 5 px taller, for the bars under the time reached.
- *Width:* `WorklapseSettings.ReviewPanelWidth`, saved when a drag of the edge ends.

Choices made while building, to confirm:
- Time Log and Marked rows have no right-click menu; they show their reach on pointing, and Marked rows can be picked.
- Highlights follow the pointer, Tab focus and an open row menu; not focus from a click (it would outstay the pointer).
- Double-clicking where nothing was recorded selects that gap.
- Idle and Not recorded frames show no marks in the slice, as their rows have none.
- The Project column in the Applications and segment lists grows with the panel (120 px at its narrowest).
- With the status bar's frame line gone, the window title isn't shown anywhere.

Changed after the developer used it (2026-10-19):
- Shift or Ctrl+click on a picked row at either end of the selection lets it go (the last one clears it).
- The Day / Selection pair is a tool bar across the panel (icon and text tool buttons); its right side shows the
  day's span, or the selection's typeable ends, length and Clear (no longer a row of their own).
- Time set to a project while nothing was recorded is a row of its own on Recorded Segments, with its project shown
  (no list to change it in); the day's list, and its span, reach out to such time beyond the recording (an evening call).
- Applications rows have a right-click menu too (the developer wanted to remove all of a game's captures): Set
  Project ▸, Clear Project, Mark for Deletion, Mark as Productive/Other, Go To, acting on all of the app's time (a
  browser's pages of every site included, `MarkReach.TimeOf`) on the day, or inside the selection on its list. The
  selection's own items don't apply: an app's time is scattered, and the selection is one range.
- The Overview's week rows and month cells show Active · Assigned (was Assigned · recorded; idle time isn't wanted
  there), from `ProjectTime.Active`.
- The selection's "No project" counts active time only (`MarkReach.Unassigned`), not idle or unrecorded time.
- **Set Unassigned to ▸** (Set Project button, the timeline's and the rows' menus): sets everything without a
  project in what it acts on to one by hand, idle and unrecorded gaps included, so blocks join (what Join did).
- Fixed: the monitor menu laid its items out in a row; the tool buttons' horizontal layout reached into the menu that
  drops from one (`:is(Button).tool > StackPanel` now).
- Day review's last capture lasts the maximum, as in Core's totals and project blocks (it lasted one interval, so a
  block ran 5 s past the end of the segments and the gap after it read "Mixed").

#### Window title rules (designed on the canvas, page "Round 6", and built 2026-10-19)

Asked for to mark IDE time by the open solution: VS ("Worklapse (Running) - File.cs - Microsoft Visual Studio") and
JetBrains ("Worklapse – File.cs") put it in the title. The developer chose general rules they write over per-IDE parsing.
- A project has a list of rules: *title contains* text, optionally *only in* one app (exe name). Case is ignored.
- A matching rule decides before the site's link and the app's (`CaptureRecord.RuleProjectId`, then `SiteProjectId`,
  then `AppProjectId`); time set by hand still decides over all of them; only active time, like links.
- Several rules matching one title: a rule for one app beats one for any app, then the longer text, then the older rule
  (`TitleRule.Precedence`). Per-project lists have no order across projects, hence this rather than "first listed".
- Looked up when history is read (`HistoryStore.LoadDay`, `LoadSamples`), like links, so rules apply to all history and
  nothing is stored per capture. Schema 11: `title_rules` (project, text, app or NULL, position), cascade-deleted with
  the project. `ProjectStore.SetTitleRules` keeps ids (they break ties).
- Settings ▸ Projects: a *Window titles* grid (Title contains, Only in, Matched = all-history time of the titles the rule
  matches) with Add / Remove, and under it the titles the selected rule matches (`HistoryStore.GetTitles`), each marked
  with the other project whose rule decides for it, if any. Works on the dialog's unsaved rules; saved on Apply.
- Day review: an app's or site's link no longer reaches time a rule decides (`MarkReach.Link`).

Choices made while building, to confirm:
- *Only in* is one app; the list holds "Any app" and every app with a recorded title.
- Text is trimmed; a rule left empty isn't saved.
- Matched counts a rule's own matches, including titles another project's rule decides for (they're marked in the list).

### 1. Make it usable day to day

Items 1 and 2 were handed off on 2026-10-16 (that HANDOFF.md is in git history); both are built (2026-10-17).

1. **Productive apps & sites — built 2026-10-17** (see Decisions, How sites are read). Checked end to end on a
   copy of real data plus a fresh recording with browsing: marking `google.ca` and `firefox.exe` on the Programs
   page changed Day review's totals for the new day (0 s → 6 m productive: google.ca and Firefox pages without a
   site; github.com, google.com, mail.google.com stayed Other) and for an old day (0 s → 2 m: old Firefox captures
   have no site), matching an independent SQL count. Still to try by hand: the right-click menu. Possible later:
   more browsers (Brave, Vivaldi and Opera are Chromium and would likely read the same way). TimeSnapper tracked
   domains as "programs" (`PROGRAM.PROGTYPE` 1 = website), relevant for the import.
2. **Remove deleted video from disk — built 2026-10-16/17** (see How deleting removes video). The purpose is
   that deleted content is gone, not space. Day review after a delete was checked through the UI before the
   rebuild and by decoding after it, not yet by stepping through it in the window afterwards (the test window
   was closed mid-run). Four gaps for that purpose, agreed with the developer 2026-10-17, are closed: the
   segment being recorded is closed and encoded at the next tick after a delete touches it; the encoder runs
   while recording is paused or locked; deleted rows are zeroed in the database and its log truncated; and
   what couldn't be removed is shown in the status window. The status-window line was checked by build and
   tests only, not looked at on screen. What remains is listed under Known gaps.
3. **TimeSnapper import — built 2026-10-19** (agreed 2026-10-17 as a one-time tool; the choices below agreed
   2026-10-19). Settings ▸ Recording ▸ Storage ▸ *TimeSnapper: Import history…* (shown on Windows when TimeSnapper is
   installed) opens a dialog: the database path (default `%LOCALAPPDATA%\TimeSnapper\Database\TIMESNAPPER.FDB`,
   Browse…), Import, a progress bar, a result line and what was left out; Stop / closing stops it, and importing
   again goes on from there. It imports into the history folder in use.
   - **Reading:** the Firebird 1.5 file opens only with TimeSnapper's own 32-bit `fbembed.dll` and its bundled
     FirebirdSql 2.1 (.NET 2.0) driver, from a 32-bit process (modern FirebirdClient fails with "user name and password
     are not defined"; a distro's `libfbclient` is Firebird 3+ and can't open it). So `Worklapse.TimeSnapperReader.exe`
     (net48, x86, loads both from `%ProgramFiles(x86)%\TimeSnapper` by reflection + `SetDllDirectory`) reads a **copy**
     of the database (`TimeSnapperSource` copies it to %TEMP% first: the engine writes to the file, and TimeSnapper
     may be running; the copy works while it is) and streams programs, classified ids and activities to stdout in
     `BinaryWriter` encoding (`Import/TimeSnapperStream.cs`, compiled into both). Windows only.
   - **Pictures stay where they are** (TimeSnapper's Snapshots folder): each becomes a JPEG image segment with an
     absolute path, on a monitor of its own named `TimeSnapper`; Day review draws such a picture at its own size and
     fits each one to the view (the developer chose "fit each picture", like TimeSnapper's Day Browser); its Monitor
     list calls it "TimeSnapper window". **Deleting an imported capture deletes its JPEG** (developer's choice:
     same privacy rule as Worklapse's own images; TimeSnapper's Day Browser then shows those rows without a picture).
     Moving the pictures into the data folder later is still only an idea.
   - **What comes in:** every capture as *active* (TimeSnapper has no idle rows) with its app (exe name and
     `PROGRAM.PATH`), title and site. Left out: captures whose picture is gone (deleted in TimeSnapper's Day Browser)
     — except "(Unknown)" programs, which come in without a picture and without an app; captures falling in the time
     one of Worklapse's own counts for (until its next, at most 1.5 × the interval; idle ones too), so Worklapse
     decides wherever it recorded and TimeSnapper only fills what it didn't (not running, paused, an excluded app),
     with no interleaving at longer intervals (agreed 2026-10-19); captures imported before (`timesnapper_imports`, kept after a delete so it doesn't come back).
     A picture that isn't a JPEG (the three first PNGs) comes in without a picture; one that can't be opened now is
     left for the next import. The 2025-12 → 2026-01 block (pictures only) comes in as active time with no app.
   - **Productive:** `CLASSIFICATION`'s programs and sites are marked productive by exe name / site in Worklapse
     (only apps and sites Worklapse has; nothing is unmarked). Worklapse's rule still applies (the site decides),
     so imported days can total differently from TimeSnapper's grade, which counted exe *or* site.
   - **Sites:** `TimeSnapperImport.SiteName`: the name read as an address bar (`SiteDomain`), kept only if it has a dot
     (or is localhost) and the name starts with the host it gives; page titles ("GITHUB - GITHUB.COM", "X - VISIT"),
     paths ("C:/USERS"), words ("SETTINGS") and numbers ("1720") are dropped, leaving the browser.
   - Checked 2026-10-19 by a full import of the real archive into a scratch copy of the history (410,548 captures in
     about 4–5 min; a second run 7 s, adding nothing) and headless screenshots of the dialog and of Day review on
     imported days. Not yet clicked through in the real app. That run found a bug the tests can't reach (they don't
     run the helper): it sent one program instead of ~2,000.

   What the data holds (read-only look, 2026-10-17, at the 2026-10-15 copy):
   - **Productive** = the program or site is in `CLASSIFICATION` (90 rows, one per `PROGRAM.ID`, `KEYWORDS` all
     empty; no unproductive or neutral class). TimeSnapper's daily grade counts a row productive if its exe *or* its
     site is classified (Worklapse: the site decides). Map by lower-case exe name / domain: `PROGRAM` has a row per
     path or version (Rider ×4, Presayo ×9).
   - **Programs and sites:** `PROGRAM.NAME` is the upper-case exe name (`ACTIVITY.PROGRAM` the original case),
     sites (`PROGTYPE` 1) the upper-case host without `www.`, port kept; `WEBPROGID` links a row to its site
     (firefox, msedge, chrome only). `URL` is scraped address-bar text, often junk, can hold login tokens: use the
     site's name, not the URL. 77 old site names are page titles (181 rows) and need cleaning or dropping.
   - **Time:** `SNAPDATE` is local wall-clock time; this machine is on Saskatchewan time (UTC−6 all year, no DST).
     `DURATION` is a fixed 10 s credit, not a measurement; idle time is simply no rows. Rows are 10 s apart.
   - **Images:** all 400,286 files exist (34.7 GB), absolute paths under `%LOCALAPPDATA%\TimeSnapper\Snapshots`,
     one per row, baseline 4:2:0 JPEGs of the active window (6,725 different sizes); 3 PNGs of the whole desktop
     (the first three rows) that the image code can't read yet. Use `FILENAME` as stored (two are in the next
     day's folder).
   - **The 15,450 rows without a file:** 4,215 are "(Unknown)" programs TimeSnapper couldn't identify (games, Task
     Manager, installers; titles kept); 11,104 are ordinary rows in 102 runs (Jan–May 2026: YouTube, Facebook,
     OpenCode overnight, Parsec, games) whose images were most likely deleted in TimeSnapper's Day Browser, which
     keeps the row; 131 belong to the block below. (Imported: the "(Unknown)" ones, without a picture; see above.)
   - **An earlier import inside TimeSnapper:** rows 48,295–64,810 (2025-12-01 to 2026-01-06, before TimeSnapper was
     installed) are 15 s apart with no program or title, `DURATION` 1: only pictures.
   - **Titles:** characters outside Windows-1252 were stored as `?` (lost); titles are cut at 254 characters.
4. **Day review polish:** day picker (calendar) and Alt+←/→ for days if wanted; showing which segment
   is still being encoded; remember window size and monitor choice; a frame-accurate time readout when
   dragging.
5. **Everyday app behaviour (confirm each with the developer before building):** a second launch bringing up the
   main window (see 1a). Done 2026-10-20: the log file, crash reports, *Start with Windows* (in the tray when started
   that way), *Record when Worklapse starts*, the tray badge and notifications.
6. **Packaging:** a self-contained publish with the native DLL, and an installer or zip. Done 2026-10-21: the
   developer's FolderProfile (win-x64, self-contained, single file) works from Visual Studio and `dotnet publish`
   (`RuntimeIdentifiers` in the App project; without it VS failed with NETSDK1047), keeps
   Worklapse.TimeSnapperReader.exe outside the bundle (and the helper ignores the win-x64 runtime VS passes to every
   project: `TreatAsLocalProperty`), leaves out the packages' native .pdb files, and
   worklapse_native.dll links the C runtime statically (`x64-windows-static`), so no Visual C++ Redistributable is
   needed. The developer plans
   Velopack, code signing (already set up for another project) and a GitHub page/website later. PRIVACY.md is written.
   **License: GPLv3** (developer's choice 2026-10-20, `LICENSE`): it can still be relicensed more permissively later
   while the developer holds all the copyright (outside contributions would need a CLA). Still to do when packaging: a
   THIRD-PARTY-NOTICES file made from what the publish actually ships (libaom with the AOM patent license,
   libjpeg-turbo, libwebp, Avalonia, SkiaSharp/HarfBuzzSharp, SQLite/SQLitePCLRaw, CommunityToolkit, FluentIcons,
   Tmds.DBus, the .NET runtime). The app icon is a placeholder, per the developer (its black frame nearly vanishes on a
   dark task bar); the tray badges are drawn from whatever `.ico` is there.

### 1b. Memory: split the recorder from the UI (proposed, not yet agreed)

A small background recorder process (capture, staging, batch encoding, a Win32 tray icon; no Avalonia)
with the Avalonia app started only for Day review and Settings. Estimated 60–80 MB while recording,
plus the UI's ~100–150 MB only while a window is open; a UI crash would no longer stop recording. Native
AOT for the recorder could save another 10–20 MB. On Linux the recorder's tray icon needs a
StatusNotifierItem implementation.

### 2. Linux (Kubuntu first) — built 2026-10-19, needs a smoke test on Kubuntu

Built from Windows (WSL and containers) while the developer was away; see *How capture works on KDE Plasma* and
`docs/HANDOFF-LINUX.md`, which has what was verified, what wasn't, and what to do next on Kubuntu. In short: the native
library builds on Linux with the same pinned static libraries (the developer chose static over the distro's), the
whole solution builds and all tests pass on Linux, and against a real KWin 6.7 (headless) the script, the screens,
the active window, idle, lock and the screenshot permission all work and the app starts; only a real screenshot
couldn't be taken there (no GPU). The C code was also rewritten for readability, with output checked byte-identical.

Later: X11 (XFCE, Mint, MATE) is simpler: `_NET_ACTIVE_WINDOW`, `XGetImage`/XShm, XScreenSaver idle. Shared history
with Windows needs a decision on where the data lives.

### 3. Experiments (after the app is usable)

- Storing only the changed parts of the screen as WebP tiles (about 2× the disk of video instead of 10–20× for
  whole images) — not TimeSnapper-like (the files aren't pictures of the screen), so not part of image recording.
- Two-level quality (key q45 / other frames q10) as an option, for those who need small text exact.
- Hardware AV1 encoding (NVENC) — needs a newer NVIDIA driver, and wakes the discrete GPU.
