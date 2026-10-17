# Handoff: next two tasks

**Both tasks are done (2026-10-17); see docs/PROJECT.md for what was built and what is open.**

Written 2026-10-16 for a new Claude session. Work through the tasks **in order**; finish, verify and
report each one before starting the next.

## Before you start

1. Read `docs/PROJECT.md` in full. It holds the decisions (with the measurements behind them), how the code
   is put together, how to build and test, and the status. Don't re-open settled decisions without new data.
2. Working rules from the developer:
   - Ask before adding anything not described here. When a choice comes up that this file doesn't settle and
     that changes what the developer sees or keeps (storage format, quality, UI behaviour), ask; for internal
     design choices, pick the sensible option and say so in your report.
   - Push back if something here looks wrong once you're in the code. This file was written from outside it.
   - Commit only when asked. Use `git commit -F - <<'EOF' … EOF` from bash (never a PowerShell here-string).
   - Fix bugs you find in your path properly (root cause, no try/catch band-aids), and think through edge
     cases yourself: first run, deleted data, async work finishing after a window closed.
   - Keep `docs/PROJECT.md` current (decisions, status, next steps) as part of each task.
3. Build: `dotnet build` (also builds the native library), `dotnet test tests/Worklapse.Tests`. All tests pass
   as of commit `cec5e6a`.
4. Never test against the real data in `%LOCALAPPDATA%\Worklapse`. Copy it, and point a test run at the copy
   with `WORKLAPSE_CONFIG_DIR` (see PROJECT.md → Building, running, testing). Write that `settings.json` with
   PowerShell's `ConvertTo-Json`, not by hand: an unescaped backslash makes it unreadable. Only one Worklapse
   can run at a time (the developer's may be running; check before launching yours).
5. Memory matters to the developer. Recording runs at ~88 MB and Day review peaks at ~640 MB with four
   monitors. Measure before and after anything that could change that: private bytes from
   `Process.PrivateMemorySize64`, and GC heap vs. committed from `dotnet-counters`
   (`dotnet tool install dotnet-counters --tool-path <scratch>`). Drive the UI through Windows UI Automation from
   PowerShell; buttons carry `AutomationProperties.Name`.

---

## Task 1 — Free disk space after deletes

**Why:** deleting captures in Day review (mark, then "Delete permanently") removes the captures but leaves their
video on disk. PROJECT.md → Next steps 1.2.

**What happens today:** `HistoryStore.DeleteMarked` deletes `captures` rows; `frames` rows cascade away.
`segments` and `segment_frames` stay, and so does the `.ivf` file. A segment frame can still be needed after
its own capture is gone: an unchanged monitor in a *later* capture points at an earlier frame (`frames` row
with another capture's segment frame). AV1 frames also depend on the frames before them, so a frame can't
simply be cut out of a segment.

**What to build:**

- Find segments with frames that no `frames` row references any more.
  - **Nothing referenced:** delete the file and its `segments` / `segment_frames` rows. No re-encode.
  - **Some referenced:** re-encode a new segment containing only the referenced frames, in order, from a key
    frame. Repoint their `frames` rows to the new segment, and delete the old rows and file.
- Run it on the batch encoder's thread (`BatchEncoder`, low priority, one segment at a time). Trigger it after
  `DeleteMarked`, and also on startup to catch deletes made before a crash or quit.
- Staged segments (still `.staging`, not yet encoded): don't rebuild them. When the encoder gets to one, it can
  leave out unreferenced frames directly. Check that this is safe against frames the recorder is still adding.

**Design points to get right:**

- **Treat segments as immutable:** write the rebuilt one as a new `segments` row and file, and switch the
  `frames` rows over in one transaction.
  - The Day review may have the old segment open. `FrameProvider` opens files with `FileShare.Delete`, and its
    loaded day holds `FrameRef(segmentId, frameIndex)` values that will go stale.
  - Make Day review cope: reload the day, or re-resolve a frame that no longer exists, instead of showing an error.
- **Rebuilding loses quality** (decode, then encode again at the segment's quantizer).
  - Measure it on real captures: PSNR of a twice-encoded frame against a once-encoded one, plus a visual check
    of small text, because SSIM misses the stale-text problem (see PROJECT.md).
  - Proposed rule, to confirm with the developer once you have numbers: rebuild only when it frees enough space
    (for example at least a quarter of the segment's bytes). Otherwise leave the segment until more of it is deleted.
- **Re-encoding uses the encoder** (~200 MB for a 3440×1440 segment while it runs, then freed). One at a time,
  never alongside the regular batch encode.
- **Crash safety:** a crash at any point must leave a consistent database and no lost referenced frames.
  - Write the new file, commit the switch, then delete the old file.
  - On startup, clean up orphaned files from either side.

**Done when:**

- Tests cover: whole-segment removal, partial rebuild (including frames referenced by later unchanged
  captures), a crash between the steps, and Day review reading frames during a rebuild.
- Disk space measurably drops after deleting a range in a copy of real data.
- Day review shows the remaining frames correctly before and after.

---

## Task 2 — Productive apps and websites

**Why:** nothing can be marked productive yet, so all active time shows as "Other". PROJECT.md → Next steps 1.1.

**Decided with the developer (2026-10-16):**

- **Two classes only:** Productive and Other. The timeline colours and summary already show them.
- **Mark things in two places:**
  - a **Programs page in Settings** listing every app and website seen, each with a productive checkbox;
  - a **right-click in Day review** on the current frame (and on a selection), e.g. "Mark firefox.exe
    productive" / "Mark github.com productive", with the reverse when already marked.
- **Websites are told apart by reading the browser's address bar** through UI Automation at capture time.
  Store **the domain only** (host name, not the full URL) per capture. This applies to captures from now on;
  older browser captures fall back to the browser app's classification.
- **Classification is looked up when a day is read**, not stored per capture. Changing it applies to all
  history at once, which is how `apps.productive` already works.

**What exists:**

- `apps (exe_name, exe_path, productive)`, with schema version 3 adding `productive`.
- `HistoryStore.LoadDay` joins it into `CaptureRecord.Productive`.
- `SpanSummary` and the timeline already split Productive vs. Other.
- The foreground window comes from `IForegroundWindowProbe` → `ForegroundWindow(ProcessName, ProcessPath,
  Title, Bounds)` in `Worklapse.Capture.Windows`.

**What to build:**

1. **Schema (next migration):**
   - a `sites (id, domain UNIQUE, productive)` table;
   - a nullable `captures.site_id`.
   - Rule: a capture is productive if it has a site and the site is productive. Otherwise it follows the
     app's flag.
   - Agree with the developer what a productive *browser app* means when the site is set and not productive.
     The proposal: the site decides, so marking firefox.exe productive covers only browser captures without a
     known site.
2. **Domain capture on Windows:**
   - Read the address bar of Chrome, Edge and Firefox through UI Automation when the foreground app is one of
     them, and add the domain to `ForegroundWindow` (keep the interface platform-neutral for the Linux port).
   - Constraints:
     - It runs every capture tick (10 s by default), so measure its time and memory.
     - Don't pull the WPF/WindowsDesktop framework into the recorder just for `System.Windows.Automation`. The
       COM `IUIAutomation` interface through interop is the lighter route.
     - Recording memory must stay close to ~88 MB.
     - Time out and record no domain rather than delay a capture.
     - Private/incognito windows: record no domain.
3. **Settings → Programs page:**
   - Apps and sites seen, with total recorded time, sortable, with a filter box and a productive checkbox each.
   - Follow the existing Breeze look and the Settings dialog's Apply/OK behaviour. The dialog already keeps
     settings it doesn't show.
   - Classification lives in the database, not `settings.json`, since it belongs with the history. Say so if
     you disagree.
4. **Day review context menu:**
   - On the frame view and on the timeline selection.
   - For a selection with several apps or sites, list each one (up to a sensible number) with its time in the
     selection.
   - After a change, the summary and timeline update immediately.
5. **Tests:** classification rules (app vs. site precedence), the migration on an existing database, and
   domain parsing from address-bar text. That last one includes text without a scheme, `about:` / `chrome://`
   pages, IP addresses and ports, and internationalized domains.

**Done when:**

- With a copy of real data plus a fresh recording that includes browsing, marking an app and a site productive
  changes the Day review summary and timeline correctly for old and new captures.
- Recording memory and capture timing are measured and reported against the numbers above.

---

## When you finish each task

Report to the developer:
- what you built;
- the measurements;
- what you decided and why;
- anything left open.

Update `docs/PROJECT.md`, and leave committing to the developer's say-so.
