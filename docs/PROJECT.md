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
| Look | **KDE Breeze-style with a little Windows flavour**: Breeze Light palette and control styling, system font (Segoe UI / Noto Sans), 4 px corners, Fluent System Icons. A dense desktop-app layout (menu bar, tool bar, status bar), not a web-app look. | Asked for explicitly; an earlier pass with cards and big padding looked like a web app. |
| What to capture | Choice of active application, primary monitor, selected monitors, or all monitors. The developer uses all monitors. | Matches TimeSnapper's options; all monitors became practical once video compression was in. |
| Idle | Stop capturing after N minutes without input (default 5) and while the screen is locked. When the limit is hit, the captures since the last input are re-marked as idle. | Idle time should be time without input, not time after the threshold. |
| Video format | AV1 (libaom), one stream per monitor, 90-frame segments (15 min at 10 s), constant quantizer, default 40, adjustable 20–55 in Settings (warning from 48). | Measured: roughly 2.5× smaller than per-frame WebP or JPEG at the same readability. |
| Encoding | **Batch encoding.** Captures are converted to I420 (YUV 4:2:0, 1.5 bytes per pixel) at capture time and go into a staging file per segment (XOR against the previous capture, Brotli-compressed); full segments are encoded one at a time on a low-priority thread. Staged segments left over by a crash or quit are encoded on the next start. | Four live libaom encoders used about 850 MB (their frame-buffer pool is fixed; options barely help). |
| Encoding modes | One mode only (no battery mode, no quick encode). | The developer's laptop is effectively always plugged in; simplest to build. |
| Small-text accuracy | Accepted: at the default quantizer, small changes (a clock digit, a status label) can show the previous value for a while. The app/title log is exact regardless. A two-level fix exists (key frames q45, other frames q10) but costs 30–125 MB/hour during video playback, so it's not used. | Tracking relies on the log; the video is for context. |
| Screen capture (Windows) | GDI BitBlt in 64-row strips into one small shared bitmap; each strip is hashed (XxHash3, for change detection) and converted straight into one shared I420 frame. No `CAPTUREBLT` (it makes the cursor flicker and isn't needed with DWM). | DirectX Desktop Duplication needed Direct3D devices that cost ~90 MB+ of driver memory on the hybrid-GPU laptop; strips and I420 cut pixel buffers from ~105 MB to ~28 MB. |
| Playback decoding | One libaom decoder per monitor, single-threaded, closed after 15 s unused; a 64 MB cache of recent frames as I420 in pooled buffers, converted straight into the on-screen bitmap. | Each open decoder holds ~60 MB (1920×1080) to ~145 MB (3440×1440) of reference frames and buffers; every extra thread adds 30–40 MB for little speed. The old setup (8 decoders × 8 threads, 400 MB BGRA cache, a new 20 MB array per frame) reached ~3 GB. |
| Rendering (Windows) | Avalonia software rendering by default; `"UseGpuRendering": true` in `settings.json` switches to the GPU (ANGLE/Direct3D 11) at the next start. Not in the Settings dialog. Agreed 2026-10-16. | Measured: GPU rendering cost ~80 MB more while recording, ~450 MB more while reviewing four monitors, and kept ~200 MB after the review closed, with no CPU saving in a normal-sized window. It may be smoother with Day review maximized on a very large or high-DPI screen (not measured). |
| Deleting | Mark ranges in Day review → they're hidden from totals at once → confirm later to delete permanently. Disk space is reclaimed by re-encoding affected segments in the background (not built yet). | Matches the review workflow: decide at the end of the day. |
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

---

## Code layout

```
native/
  av1/worklapse_av1.c         C wrapper around libaom (encode BGRA, decode to BGRA, set options)
  build-windows.sh            builds native/win-x64/worklapse_av1.dll (libaom static, via VS vcpkg)
src/
  Worklapse.Core/             settings, SQLite storage, recorder loop, platform interfaces, history reading
  Worklapse.Video/            AV1 interop, IVF files, staging files, batch encoder, frame provider for playback
  Worklapse.Capture.Windows/  GDI screen capture, foreground window, idle time, lock detection
  Worklapse.App/              Avalonia app: tray icon, status window, settings dialog, Day review
tests/Worklapse.Tests/        xUnit v3 tests (storage, recorder, staging, AV1 round trip, playback frames, history)
docs/PROJECT.md               this file
```

### How recording works

1. `Recorder` (Core) ticks every interval on a background task. It skips the tick when paused or locked,
   records an idle capture when there's been no input for the idle limit, and otherwise:
2. asks `IForegroundWindowProbe` for the focused window (app, title, bounds) and `IScreenSource` for each
   monitor chosen by the capture mode;
3. reuses the previous frame for a monitor whose pixels didn't change (hash), otherwise appends the
   capture to `StagedVideoSink` (in active-application mode, everything outside the window is blacked out first);
4. writes the capture and frame references in one SQLite transaction.
5. When a segment has 90 frames (or recording pauses), `BatchEncoder` encodes it to AV1 on its own thread,
   rewrites the frame offsets to point into the `.ivf` file, marks the segment encoded and deletes the staging file.

### How playback works

`HistoryStore` loads a day (captures, frame references, monitors). `FrameProvider` decodes any frame,
whether its segment is staged or encoded; it keeps one single-threaded decoder per monitor positioned in
that monitor's current segment, so stepping forward decodes one frame. Stepping back restarts from the
segment's key frame; on the way it keeps the two frames before the target, so the next steps back are free.
Frames are `DecodedFrame`s: packed I420 in pooled buffers, reference-counted between the provider's 64 MB
cache and the window (each holder calls `Release`). `FrameRenderer` runs the provider on a background
thread, newest request wins, and closes decoders unused for 15 s. `DayReviewWindow` converts each frame
straight into its `WriteableBitmap`, and closing the window runs an aggressive GC so the memory goes back to
the OS. `TimelineControl` and `FrameView` are custom-drawn controls; `DayReviewViewModel` holds the playhead,
selection, summaries and the mark/restore/confirm deletion logic.

### Storage

Default data folder `%LOCALAPPDATA%\Worklapse` (Linux: `~/.local/share/Worklapse`); settings in
`%APPDATA%\Worklapse\settings.json` (Linux: `~/.config/Worklapse`). The data folder holds
`worklapse.db` and `video/YYYY-MM-DD/HHmmss-m<monitor>.ivf` (plus `.ivf.staging` while a segment is open).

SQLite schema (version 3, migrations in `WorklapseDatabase`):

- `apps` (exe name, path, `productive` flag)
- `monitors` (device name and geometry)
- `captures` (UTC ms + local offset, active/idle, app, window title and bounds, `pending_delete`)
- `segments` (monitor, relative path, size, quantizer, `state` 0 = staged / 1 = encoded)
- `segment_frames` (segment, frame index, file offset and length, key flag)
- `frames` (capture × monitor → segment frame; unchanged monitors point at an earlier frame)

---

## Building, running, testing

Requirements: .NET 10 SDK; Visual Studio 2026 or its Build Tools with the C++ workload (for the native
library). From Git Bash in the repository root:

```sh
bash native/build-windows.sh            # once, or after changing native/av1 (≈5 min the first time)
dotnet build
dotnet test tests/Worklapse.Tests
dotnet run --project src/Worklapse.App
```

Worklapse starts with a small status window and a tray icon; closing the window keeps it recording.
Quit from the tray menu. Only one instance runs at a time.

Testing without touching real data: set `WORKLAPSE_CONFIG_DIR` to a folder containing a `settings.json`
whose `DataFolder` points somewhere disposable (copy the real data folder there to test Day review on real
recordings). Captures are idle (no frames) while nobody uses the keyboard or mouse, so for unattended
test runs set `"IdleMinutes": 120` in that settings file.

The UI can be driven for screenshots with Windows UI Automation from PowerShell (find a button by name,
invoke it, copy the window's rectangle from the screen); icon-only buttons carry `AutomationProperties.Name`
for this and for screen readers.

---

## Status (2026-10-16, updated after the Day review memory fix)

Working on Windows:

- Recording of all four capture modes, idle and lock detection, unchanged-frame reuse, batch AV1 encoding,
  crash recovery of staged segments.
- Status window and tray menu (pause/resume, Day review, settings, quit); Settings dialog (capture mode,
  monitors, interval, idle, lock, quality, history folder).
- Day review: timeline of the recorded hours, all or one monitor, zoom/pan, ← / → frame stepping,
  Shift+arrows and drag to select, playback at four speeds, selection/whole-day summary (length, active,
  productive, other, idle, not recorded, marked), mark for deletion / restore / delete permanently, F5 refresh.
- Breeze-style theme across all windows.
- An unreadable `settings.json` (bad JSON, unknown value) no longer crashes at startup: a window shows the
  file and the error and offers Quit (to fix it by hand) or starting with defaults, which renames the file
  to `settings.json.broken-<date>`.

Tested less or not at all: active-application mode over a longer session, selected monitors, plugging
displays in and out, rotated displays (GDI should handle them, untested), Store-app window detection,
drag selection and keyboard stepping by a person (only toolbar buttons were exercised automatically).

Known gaps and caveats:

- Nothing can be marked productive yet, so all active time shows as "Other".
- Deleting permanently removes the captures but not their video; segments still need to be rebuilt to free space.
- Small text can lag behind in the video (see Decisions).
- Avalonia and .NET are the bulk of the remaining footprint while recording (~88 MB in all with software
  rendering, measured shortly after start); see the recorder/UI split below.

---

## Next steps

The priority is a complete, usable Windows app. Alternatives and ports come after.

### 0. Memory while reviewing — done 2026-10-16 (not yet reviewed by the developer)

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

### 1. Make it usable day to day

1. **Productive apps & sites.** Settings page (or Day review context menu) to mark apps productive;
   the `apps.productive` column already exists. Websites need browser-domain detection: TimeSnapper tracked
   domains as "programs" (its `PROGRAM` table has `PROGTYPE` 1 = website). Options: read the address bar via
   UI Automation, or start with window-title matching.
2. **Reclaim disk space after deletes.** Re-encode segments whose frames are no longer all referenced,
   keeping the frames still in use (including frames that later unchanged captures point to), and update
   `segment_frames` / `frames`. The batch encoder thread is the natural place to run it.
3. **TimeSnapper import.** Copy the old database first. The Firebird 1.5 file opens only with
   TimeSnapper's own 32-bit `fbembed.dll` and its bundled old FirebirdSql driver: load them from a
   32-bit process (32-bit PowerShell works; modern FirebirdClient fails with "user name and password are not
   defined"). Import `ACTIVITY` rows (time, duration, program, caption, file name) as captures with a
   "legacy JPEG" frame kind that Day review shows directly; map `PROGRAM` and the productive classification.
   Note that 15,450 activity rows have no file name (meaning unknown).
4. **Day review polish:** day picker (calendar) and Alt+←/→ for days if wanted; showing which segment
   is still being encoded; remember window size and monitor choice; a frame-accurate time readout when
   dragging.
5. **Everyday app behaviour (confirm each with the developer before building):** start with Windows,
   start hidden in the tray, a second launch bringing up the status window, a log file for errors.
6. **Packaging:** a self-contained publish with the native DLL, and an installer or zip.

### 1b. Memory: split the recorder from the UI (proposed, not yet agreed)

A small background recorder process (capture, staging, batch encoding, a Win32 tray icon; no Avalonia)
with the Avalonia app started only for Day review and Settings. Estimated 60–80 MB while recording,
plus the UI's ~100–150 MB only while a window is open; a UI crash would no longer stop recording. Native
AOT for the recorder could save another 10–20 MB. On Linux the recorder's tray icon needs a
StatusNotifierItem implementation.

### 2. Linux (Kubuntu first)

KDE Wayland capture through KWin's `org.kde.KWin.ScreenShot2` D-Bus interface (needs a `.desktop` file
declaring `X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2`), the focused window from a small
KWin script, idle from `ext-idle-notify-v1`, and lock from `org.freedesktop.ScreenSaver`. X11 (XFCE, Mint,
MATE) is simpler: `_NET_ACTIVE_WINDOW`, `XGetImage`/XShm, XScreenSaver idle. The native library needs a
Linux build (static libaom, or the distro's `libaom` with a matching ABI check). Shared history with
Windows needs a decision on where the data lives.

### 3. Experiments (after the app is usable)

- **WebP-only recording (developer's idea).** Store each changed capture as a WebP image instead of
  video. Expected effect, from the benchmarks above:
  - removes libaom and the 15-minute encoding spike;
  - steady memory would barely change (it no longer includes encoders);
  - disk use would rise a lot: roughly 10–20× for whole screens, or about 2× if only changed
    regions are stored as WebP tiles;
  - deletes become trivial (drop the images).

  The existing `IFrameSink` / `FrameProvider` split is the seam for a second storage format. Worth
  measuring with real captures before switching.
- Two-level quality (key q45 / other frames q10) as an option, for those who need small text exact.
- Hardware AV1 encoding (NVENC) — needs a newer NVIDIA driver, and wakes the discrete GPU.
