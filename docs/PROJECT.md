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
| Look | **KDE Breeze-style with a little Windows flavour**: Breeze control styling, system font (9 pt Segoe UI / 10 pt Noto Sans), 4 px corners, Fluent System Icons, desktop control sizes (Fluent compact density plus own templates for scroll bars, tabs, check boxes, spinners). Colours follow the OS, live: Windows 11 light/dark with the system accent; on KDE the active colour scheme from kdeglobals; elsewhere Breeze light/dark with the portal accent. Timeline meaning colours stay fixed (dark variants). A dense desktop-app layout (menu bar, tool bar, status bar), not a web-app look. | Asked for explicitly; an earlier pass with cards and big padding looked like a web app, and a later one still looked touch-sized (2026-10-18). |
| What to capture | Choice of active application, primary monitor, selected monitors, or all monitors. The developer uses all monitors. | Matches TimeSnapper's options; all monitors became practical once video compression was in. |
| Idle | Stop capturing after N minutes without input (default 5) and while the screen is locked. When the limit is hit, the captures since the last input are re-marked as idle. | Idle time should be time without input, not time after the threshold. |
| Video format | AV1 (libaom), one stream per monitor, 90-frame segments (15 min at 10 s), constant quantizer, default 40, adjustable 20–55 in Settings (warning from 48). | Measured: roughly 2.5× smaller than per-frame WebP or JPEG at the same readability. |
| Recording as images | **Optional, video stays the default.** Settings → Recording: Video (AV1), JPEG images or WebP images, each format with its own quality slider over its full range (JPEG 1–100, default 15, TimeSnapper's; WebP 0–100, default 20, agreed 2026-10-18). A file per changed capture and monitor, `images/YYYY-MM-DD/HHmmss-m<monitor>.jpg/.webp`, which other programs can open. Each choice has a one-line trade-off under it, and a preview (as in TimeSnapper) shows the primary monitor's most detailed part as stored, at 1:1, with a running estimate of the space: while Settings is open the primary monitor is captured every interval and stored as the recorder would (`StorageEstimate`), for the chosen format only, and the average per capture is shown, so video's falls from its key frame as captures come in. Agreed 2026-10-17/18. AVIF was measured (2026-10-18: clearest text at WebP 20's size, ~0.3 s per 3440×1440 image, ~+110 MB while encoding) and left out as not different enough; AV2 has only its slow reference encoder so far; JPEG XL was worse at that size. | Asked for, like TimeSnapper. WebP was checked by eye: text clearer at 10 than JPEG at 15 (developer). Images take far more disk (see Benchmarks) and WebP more CPU than JPEG, but each is exact for its moment (no stale small text), deleting is deleting files, and playback needs no decoder. |
| Encoding | **Batch encoding.** Captures are converted to I420 (YUV 4:2:0, 1.5 bytes per pixel) at capture time and go into a staging file per segment (XOR against the previous capture, Brotli-compressed); full segments are encoded one at a time on a low-priority thread. Staged segments left over by a crash or quit are encoded on the next start. | Four live libaom encoders used about 850 MB (their frame-buffer pool is fixed; options barely help). |
| Encoding modes | One mode only (no battery mode, no quick encode). | The developer's laptop is effectively always plugged in; simplest to build. |
| Small-text accuracy | Accepted: at the default quantizer, small changes (a clock digit, a status label) can show the previous value for a while. The app/title log is exact regardless. A two-level fix exists (key frames q45, other frames q10) but costs 30–125 MB/hour during video playback, so it's not used. | Tracking relies on the log; the video is for context. |
| Screen capture (Windows) | GDI BitBlt in 64-row strips into one small shared bitmap; each strip is hashed (XxHash3, for change detection) and converted straight into one shared I420 frame. No `CAPTUREBLT` (it makes the cursor flicker and isn't needed with DWM). | DirectX Desktop Duplication needed Direct3D devices that cost ~90 MB+ of driver memory on the hybrid-GPU laptop; strips and I420 cut pixel buffers from ~105 MB to ~28 MB. |
| Playback decoding | One libaom decoder per monitor, single-threaded, closed after 15 s unused; a 64 MB cache of recent frames as I420 in pooled buffers, converted straight into the on-screen bitmap. | Each open decoder holds ~60 MB (1920×1080) to ~145 MB (3440×1440) of reference frames and buffers; every extra thread adds 30–40 MB for little speed. The old setup (8 decoders × 8 threads, 400 MB BGRA cache, a new 20 MB array per frame) reached ~3 GB. |
| Rendering (Windows) | Avalonia software rendering by default; `"UseGpuRendering": true` in `settings.json` switches to the GPU (ANGLE/Direct3D 11) at the next start. Not in the Settings dialog. Agreed 2026-10-16. | Measured: GPU rendering cost ~80 MB more while recording, ~450 MB more while reviewing four monitors, and kept ~200 MB after the review closed, with no CPU saving in a normal-sized window. It may be smoother with Day review maximized on a very large or high-DPI screen (not measured). |
| Deleting | Mark ranges in Day review → they're hidden from totals at once → confirm later to delete permanently. The batch encoder then removes the deleted frames from disk: a segment with no frame left in use is deleted; one with some is rebuilt from those frames (decoded and encoded again at its quantizer), however few frames were deleted; a segment still staged leaves the deleted frames out when it's encoded. | Matches the review workflow: decide at the end of the day. **The purpose is that deleted content is gone** (nothing a client could be shown by accident); disk space is a bonus (developer, 2026-10-17). So there is no "only when it saves enough" rule, despite the small quality cost of a rebuild. |
| Productive time | Two classes, Productive and Other. Apps (by exe name) and websites are marked on Settings → Programs or by right-clicking in Day review. **The more specific one decides** (developer, 2026-10-17): a capture with a site follows the site; otherwise its app. So a browser marked productive covers only its pages without a known site. Classification is looked up when a day is read, so a change applies to all history. Kept in the database, not `settings.json` (it belongs with the history). | Decided 2026-10-16/17. |
| Excluded apps and sites | Marked on Settings → Programs (Excluded column, next to Productive). While an excluded app (by exe name) or site has the focus, **nothing is recorded** (no capture at all), so that time shows as *Not recorded*; once the idle limit passes it is recorded as idle as usual (an idle capture has no app or frames). Excluded if the app or the site is (a browser excluded covers all its pages). A browser tick whose site can't be read (private window, too slow) is recorded as the browser. Recorded history is left as it is. Kept in the database like the productive marks; an excluded app or site keeps its row when its captures are deleted. | Developer, 2026-10-18: nothing kept rather than time-and-name; the Programs list only (something must be recorded once before it can be excluded); no browser-specific handling for now. |
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
  worklapse_native.targets    MSBuild: builds native/win-x64/worklapse_native.dll (libraries static, via VS vcpkg) when needed
src/
  Worklapse.Core/             settings, SQLite storage, recorder loop, platform interfaces, history reading
  Worklapse.Video/            native interop, IVF files, staging files, frame sink (video or images), batch encoder,
                              frame provider for playback
  Worklapse.Capture.Windows/  GDI screen capture, foreground window, browser site (UI Automation), idle time, lock detection
  Worklapse.App/              Avalonia app: tray icon, status window, settings dialog, Day review
tests/Worklapse.Tests/        xUnit v3 tests (storage, recorder, staging, AV1 round trip, playback frames, history, reclaiming space, sites, monitor layouts, images)
docs/PROJECT.md               this file
docs/HANDOFF.md               briefs for the next tasks, for a new session
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
`worklapse.db`, `video/YYYY-MM-DD/HHmmss-m<monitor>.ivf` (plus `.ivf.staging` while a segment is open) and, when
recording images, `images/YYYY-MM-DD/HHmmss-m<monitor>.jpg` or `.webp`.

SQLite schema (version 8, migrations in `WorklapseDatabase`):

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

---

## Building, running, testing

Requirements: .NET 10 SDK; Visual Studio 2026 or its Build Tools with the C++ workload (for the native
library, which `dotnet build` builds when needed: ≈7 min the first time, see `native/README.md`). From the
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

## Status (2026-10-17, updated after image recording)

Working on Windows:

- Recording of all four capture modes, idle and lock detection, unchanged-frame reuse, batch AV1 encoding,
  crash recovery of staged segments.
- Status window and tray menu (pause/resume, Day review, settings, quit); Settings dialog (capture mode,
  monitors, interval, idle, lock, recording format and quality, history folder; an empty folder is refused, Default
  fills in the standard one, the folder button opens it in the file manager).
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
- Breeze-style theme across all windows, at desktop density, coloured to match the OS (Windows 11 light/dark + accent, KDE colour scheme, Breeze elsewhere), switching live (2026-10-18, not yet reviewed by the developer; Windows high contrast is not handled yet).
- An unreadable `settings.json` (bad JSON, unknown value) no longer crashes at startup: a window shows the
  file and the error and offers Quit (to fix it by hand) or starting with defaults, which renames the file
  to `settings.json.broken-<date>`.

- Recording as JPEG or WebP images (2026-10-17, not yet reviewed by the developer): Settings → Recording, each choice
  with a one-line trade-off, a preview of the primary monitor as stored and a running average per capture (2026-10-18;
  measured on a 3440×1440 monitor: while Settings is open, ~+260 MB with Video chosen, the AV1 encoder, ~+45 MB with
  WebP; given back when it closes); a file per changed capture; deletes remove the files; crash-safe naming; Day
  review plays them. Checked with tests (codec
  round trip and colours as other viewers show them, recording, format switching, deletes, failed ticks, crash
  leftovers) and benchmarks on real captures (see Benchmarks); the Settings dialog checked as rendered off-screen
  (Avalonia headless), not clicked. Not yet done by hand: an image recording looked at in Day review.
- Plugging monitors in and out (2026-10-17, not yet reviewed by the developer): layouts recorded per capture,
  Day review and its Monitor list follow them, older captures' layouts worked out; selected monitors by stable
  identity. Checked with tests (recorder with a fake desktop, the guess) and in Day review on a copy of the
  2026-10-17 recording (screenshots at 10:51, 11:30, 14:00 and a capture recorded with the new build; Monitor
  list; picking a monitor across the replug).
- Day review panning and Focus on App (2026-10-18, not yet reviewed by the developer): scroll bars while the
  picture is larger than the view, drag with the left or middle button; a zoom keeps its magnification as frames
  change size (Fit stays fitted). Focus on App (tool bar, View menu; remembered as `ReviewFocusOnApplication` in
  `settings.json`) shows only the focused window's rectangle (`captures.window_*`) on the monitors recorded, centred:
  for active-application recordings that is just what was recorded; idle captures use the last active one's window;
  a capture without a window, or whose window misses every recorded monitor, shows all monitors. Checked off-screen
  (Avalonia headless, synthetic two-monitor day: all monitors, app-only, idle; zoom, middle drag, scroll bars), not
  by hand.
- Excluded apps and sites (2026-10-18, not yet reviewed by the developer): Excluded column on Settings → Programs
  (schema 8); the recorder skips the tick while one has the focus and the status window shows *Excluded*. Tested
  with the recorder on a fake desktop (`ExclusionTests`) and the Programs page rendered off-screen; not tried in a
  real recording. Also fixed: Settings stayed dirty after Apply (Apply remained enabled).

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
3. **TimeSnapper import** (agreed 2026-10-17: a one-time tool, after image recording). Copy the old database first.
   The Firebird 1.5 file opens only with TimeSnapper's own 32-bit `fbembed.dll` and its bundled old FirebirdSql
   driver, from a 32-bit process (32-bit PowerShell works; modern FirebirdClient fails with "user name and password
   are not defined"; a distro's `libfbclient` is Firebird 3+ and can't open it either). The images become image
   segments (JPEG); the import adds what images from outside the data folder need. Agreed: skip TimeSnapper's
   captures where Worklapse has its own. Open: a read-only TimeSnapper folder the images stay in, with an offer to
   move them into the data folder later (the developer wants them left in place while trying it out); how Day review
   shows a window-sized picture with no monitor layout. What the data holds (read-only look, 2026-10-17, at the
   2026-10-15 copy):
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
     keeps the row; 131 belong to the block below. Whether to import them is the developer's call.
   - **An earlier import inside TimeSnapper:** rows 48,295–64,810 (2025-12-01 to 2026-01-06, before TimeSnapper was
     installed) are 15 s apart with no program or title, `DURATION` 1: only pictures.
   - **Titles:** characters outside Windows-1252 were stored as `?` (lost); titles are cut at 254 characters.
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

- Storing only the changed parts of the screen as WebP tiles (about 2× the disk of video instead of 10–20× for
  whole images) — not TimeSnapper-like (the files aren't pictures of the screen), so not part of image recording.
- Two-level quality (key q45 / other frames q10) as an option, for those who need small text exact.
- Hardware AV1 encoding (NVENC) — needs a newer NVIDIA driver, and wakes the discrete GPU.
