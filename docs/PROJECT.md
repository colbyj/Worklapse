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
| Deleting | Mark ranges in Day review → they're hidden from totals at once → confirm later to delete permanently. The batch encoder then removes the deleted frames from disk: a segment with no frame left in use is deleted; one with some is rebuilt from those frames (decoded and encoded again at its quantizer), however few frames were deleted; a segment still staged leaves the deleted frames out when it's encoded. | Matches the review workflow: decide at the end of the day. **The purpose is that deleted content is gone** (nothing a client could be shown by accident); disk space is a bonus (developer, 2026-10-17). So there is no "only when it saves enough" rule, despite the small quality cost of a rebuild. |
| Productive time | Two classes, Productive and Other. Apps (by exe name) and websites are marked on Settings → Programs or by right-clicking in Day review. **The more specific one decides** (developer, 2026-10-17): a capture with a site follows the site; otherwise its app. So a browser marked productive covers only its pages without a known site. Classification is looked up when a day is read, so a change applies to all history. Kept in the database, not `settings.json` (it belongs with the history). | Decided 2026-10-16/17. |
| Websites | Read from the browser's address bar through UI Automation at capture time (Chrome, Edge, Firefox), storing the **host name only, minus a leading `www.`** (mail.google.com and docs.google.com are separate; www.github.com is github.com). New captures only; older ones fall back to their browser's classification. Private windows record no site. | Agreed 2026-10-16/17. Measured: ~1 ms per capture, no measurable cost to the browsers' memory (see Benchmarks). |
| Plugging monitors in and out | Each capture records which monitors were attached (its *layout*), captured or not; Day review shows only those, and its Monitor list only those. Captures from before (schema < 6) get a layout worked out from their frames. *Selected monitors* are kept by the monitor's stable identity (Windows: device interface path, EDID model + connector), not `\\.\DISPLAYn`. Agreed 2026-10-17. | A replug on 2026-10-17 made Windows move and rename the monitors three times in 20 s; each arrangement became new `monitors` rows, and Day review drew all seven at once, on top of each other ("impossible to use"). |
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
  av1/worklapse_av1.c         C wrapper around libaom (encode I420, decode to I420 or BGRA, set options)
  worklapse_av1.targets       MSBuild: builds native/win-x64/worklapse_av1.dll (libaom static, via VS vcpkg) when needed
src/
  Worklapse.Core/             settings, SQLite storage, recorder loop, platform interfaces, history reading
  Worklapse.Video/            AV1 interop, IVF files, staging files, batch encoder, frame provider for playback
  Worklapse.Capture.Windows/  GDI screen capture, foreground window, browser site (UI Automation), idle time, lock detection
  Worklapse.App/              Avalonia app: tray icon, status window, settings dialog, Day review
tests/Worklapse.Tests/        xUnit v3 tests (storage, recorder, staging, AV1 round trip, playback frames, history, reclaiming space, sites, monitor layouts)
docs/PROJECT.md               this file
docs/HANDOFF.md               briefs for the next tasks, for a new session
```

### How recording works

1. `Recorder` (Core) ticks every interval on a background task. It skips the tick when paused or locked,
   records an idle capture when there's been no input for the idle limit, and otherwise:
2. asks `IForegroundWindowProbe` for the focused window (app, title, bounds, and for a browser the site) and
   `IScreenSource` for the monitors attached, records them as the capture's layout (idle captures too), and
   captures each monitor chosen by the capture mode;
3. reuses a monitor's previous frame when the new capture's pixel hash equals that frame's (the recorder keeps the
   hash with the frame; comparing per device name went wrong after a replug), otherwise appends the
   capture to `StagedVideoSink` (in active-application mode, everything outside the window is blacked out first);
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

### How deleting removes video

The purpose is that deleted content is gone from the data folder, not disk space (see Decisions).

`HistoryStore.DeleteMarked` deletes the captures (their `frames` rows cascade) and the apps left without
captures (unless marked productive), and, in the same transaction, queues the segments the captures used in
`reclaim_queue`. It reports whether any of those segments is still staged; if so the app calls
`Recorder.CloseSegmentsSoon`, and the recorder closes its open segments at the next tick (≤ 10 s), before
capturing, so they're encoded without the deleted frames instead of keeping them in staging for up to 15 minutes.
`BatchEncoder` works through the queue one segment per job, between encodes, when Day review asks
(`RequestReclaim`, through the app's current `StagedVideoSink`) and when it starts (deletes made before a crash
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
open, retried every minute) is in `BatchEncoder.DeletedContentLeft` and shown in the status window until it's gone.

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

### Storage

Default data folder `%LOCALAPPDATA%\Worklapse` (Linux: `~/.local/share/Worklapse`); settings in
`%APPDATA%\Worklapse\settings.json` (Linux: `~/.config/Worklapse`). The data folder holds
`worklapse.db` and `video/YYYY-MM-DD/HHmmss-m<monitor>.ivf` (plus `.ivf.staging` while a segment is open).

SQLite schema (version 6, migrations in `WorklapseDatabase`):

- `apps` (exe name, path, `productive` flag; classified by exe name; deleted with their last capture unless productive)
- `sites` (domain, `productive` flag; deleted with their last capture unless productive)
- `monitors` (device name and geometry; a monitor as arranged, so replugging adds rows, see above)
- `layouts` (the monitor ids attached at the same time, ascending, comma-separated; never deleted)
- `captures` (UTC ms + local offset, active/idle, app, site, layout (null before schema 6), window title and bounds, `pending_delete`)
- `segments` (monitor, relative path, size, quantizer, `state` 0 = staged / 1 = encoded)
- `segment_frames` (segment, frame index, file offset and length, key flag; indexes can have gaps after deletes)
- `frames` (capture × monitor → segment frame; unchanged monitors point at an earlier frame; indexed by segment frame)
- `reclaim_queue` (segments that may hold unreferenced frames, with a generation counter)
- `dead_segment_files` (segment paths whose files are to be deleted; see How deleting removes video)

---

## Building, running, testing

Requirements: .NET 10 SDK; Visual Studio 2026 or its Build Tools with the C++ workload (for the native
library, which `dotnet build` builds when needed: ≈5 min the first time, see `native/README.md`). From the
repository root:

```sh
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
invoke it, render the window with `PrintWindow`); icon-only buttons carry `AutomationProperties.Name`
for this and for screen readers. Lessons from 2026-10-17: the developer may be using the machine, so never
take the focus or send keys; only the selected tab's controls are in the tree, so check which control you type
into (a script once typed into the History folder box and pointed a test run at the real data); and check the
test `settings.json` still points at the copy after each step.

---

## Status (2026-10-17, updated after productive apps and sites)

Working on Windows:

- Recording of all four capture modes, idle and lock detection, unchanged-frame reuse, batch AV1 encoding,
  crash recovery of staged segments.
- Status window and tray menu (pause/resume, Day review, settings, quit); Settings dialog (capture mode,
  monitors, interval, idle, lock, quality, history folder; an empty folder is refused, Default fills in the
  standard one).
- Productive apps and websites (2026-10-17, not yet reviewed by the developer): sites read from Chrome, Edge
  and Firefox; Settings → Programs lists every app and site with its recorded time (filter, sort, checkbox,
  applied with Apply/OK); right-click in Day review on the frame (its site and app) or the timeline (each app and
  site in the selection, up to 12, with its time there); the site shows in Day review's status bar.
- Day review: timeline of the recorded hours, all or one monitor, zoom/pan, ← / → frame stepping,
  Shift+arrows and drag to select, playback at four speeds, selection/whole-day summary (length, active,
  productive, other, idle, not recorded, marked), mark for deletion / restore / delete permanently, F5 refresh.
- Deleted captures' video is removed from disk in the background (2026-10-16/17, not yet reviewed by the
  developer): whole segments deleted, partly deleted ones rebuilt, staged ones (including the one being
  recorded, closed at the next tick) encoded without the deleted frames, also while paused; titles and app
  names zeroed in the database; failures shown in the status window; crash-safe, and Day review reloads when a
  frame it shows has moved.
- Breeze-style theme across all windows.
- An unreadable `settings.json` (bad JSON, unknown value) no longer crashes at startup: a window shows the
  file and the error and offers Quit (to fix it by hand) or starting with defaults, which renames the file
  to `settings.json.broken-<date>`.

- Plugging monitors in and out (2026-10-17, not yet reviewed by the developer): layouts recorded per capture,
  Day review and its Monitor list follow them, older captures' layouts worked out; selected monitors by stable
  identity. Checked with tests (recorder with a fake desktop, the guess) and in Day review on a copy of the
  2026-10-17 recording (screenshots at 10:51, 11:30, 14:00 and a capture recorded with the new build; Monitor
  list; picking a monitor across the replug).

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
- Encoding failures (`BatchEncoder.EncodingFailed`) aren't shown or logged anywhere yet; the segment stays
  staged (with any deleted frames still in its staging file) and is retried at the next start. Failures to
  remove deleted video are shown in the status window.
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

Items 1 and 2 were handed off in `docs/HANDOFF.md` (2026-10-16); both are built (2026-10-17), awaiting review.

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
