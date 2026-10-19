# Handoff: the Timesheet and the Overview

> **Done 2026-10-18:** phase 5 committed (b903c08), phase 6 built; see PROJECT.md *1a*. Kept for the record.

Written 2026-10-18 for a new Claude session. Phases 1–4 of the projects work are built, committed and walked
through by the developer; what's left is **phase 5 (Timesheet view)** and **phase 6 (Overview)**. Do them in that
order; finish, verify and report each one before starting the next, and commit each when the developer asks (they
asked for a commit per phase last time).

## Before you start

1. Read `docs/PROJECT.md` in full, especially **Next steps → 1a** (the agreed design, every decision with its
   reason, and the build log of phases 1–4). Don't re-open settled decisions without new data.
2. Look at the approved mockups: https://claude.ai/artifact/2HyWXXPGWRR6bJR232Nomx, page **Round 4** (the developer's
   own link; read it with the Artifact tool, not a web fetch). Boards that matter now: *Timesheet*, *Overview · week*,
   *Overview · month*, *Overview · recording paused*. Later feedback overrides the boards where they differ (listed
   under "What the boards don't show" below).
3. Working rules from the developer:
   - Ask before adding anything not described here or in PROJECT.md 1a. When a choice changes what the developer
     sees or keeps, ask; for internal design choices, pick the sensible option and say so in the report.
   - Push back if something here looks wrong once you're in the code.
   - Commit only when asked. From bash: `git commit -F - <<'EOF' … EOF` (never a PowerShell here-string).
   - Fix bugs in your path properly (root cause), and think through edge cases yourself: first run, an empty
     day or week, deleted data, async work finishing after the window closed, a change of data folder.
   - **Desktop idiom** (the developer checks this): no segmented "pill" controls (use toggle tool buttons or combo
     boxes), real framed tabs, grids with column headers and cells that turn into a text box only while edited, no
     accent buttons in tool bars, no cards, badges or hover-only controls. UI text terse: one short line, no figures.
   - Keep `docs/PROJECT.md` current as part of each phase.
4. Build and test: `dotnet build`; then run the tests with
   `tests/Worklapse.Tests/bin/Debug/net10.0/Worklapse.Tests.exe` (`dotnet test` reports "Zero tests ran" on this
   machine). 153 tests pass at `351660f`. The developer's own Worklapse may be running and locks
   `src/Worklapse.App/bin`; build the app with `-o <scratch folder>` then.
5. Never touch the real data (`%LOCALAPPDATA%\Worklapse`, settings in `%APPDATA%\Worklapse`). Never bring
   windows to the front or send keys: the developer may be using the machine.

## How to check UI work (what worked in the last session)

Headless screenshots, no window on the developer's screen and no real data:

- A scratch console project (outside the repo, `net10.0`, with empty `Directory.Build.props` and
  `Directory.Packages.props` beside it and `ManagePackageVersionsCentrally=false`) referencing
  `src/Worklapse.App/Worklapse.App.csproj` plus `Avalonia.Headless`, `Avalonia.Skia` and `Avalonia.Desktop`
  **12.1.4**. Build it with `-o bin` into its own folder.
- Set `WORKLAPSE_CONFIG_DIR` to a scratch config folder first; save a `WorklapseSettings` whose `DataFolder` is a
  scratch folder (assert it is before going on); build test data with `RecordingStore` / `HistoryStore` /
  `ProjectStore` (captures every 10 s, links with `HistoryStore.SetProject`, ranges with `ProjectStore.SetSpan`).
- `AppBuilder.Configure<Worklapse.App.App>().UseSkia().UseHeadless(new() { UseHeadlessDrawing = false })
  .LogToTrace(LogEventLevel.Warning, LogArea.Binding, …).SetupWithoutStarting();` then
  `ThemeManager.Use(settings)` (pass `ThemeMode.Dark` for dark shots).
- A `Recorder` built with `WindowsPlatform.Create()` and a sink factory that throws (it's never started) gives a
  `RecordingViewModel`; feed it states with `Update(new RecorderStatus(...))`. Then
  `new MainWindow(new MainViewModel(recording, createDayReview, () => Task.CompletedTask, MainView.X), store, () => Task.CompletedTask)`,
  `Show()`, pump `Dispatcher.UIThread.RunJobs()` a few times, `window.CaptureRenderedFrame()?.Save(png)`, and look
  at the PNGs. Print view-model state to the console to check behaviour as well as looks.
- Binding warnings in the trace are the thing to watch; "DayReview.… Value is null" while the window closes is
  expected. Popups (dropdowns, menus, flyouts) don't render in these shots: say so in the report.
- Shell gotchas: inline Python in a bash heredoc sometimes breaks on quotes — write scripts to a file and run
  that. Never run `python` (only `python3`) in Git Bash. Git's "LF will be replaced by CRLF" warnings are harmless.

## What exists to build on

- **Core** (`Worklapse.Core/Projects/`): `Timesheet.Build(history, projects, from, to, maxCaptureLength)` gives a
  line per project per day (notes joined, Active, Productive, Assigned, Logged); `TimesheetCsv.Write` writes it
  with decimal hours (two places), `Hours()` formats one value; `ProjectTimeline.Build` works out a day's blocks and
  totals (`Unassigned` holds productive/other/idle time with no project); `NotePlacement.Place` puts notes on
  blocks; `ProjectStore` has categories, projects (`ColorOf`), ranges, notes, day notes (`GetDayNotes(from, to)`)
  and logged time (`GetLogged(from, to)`, `SetLogged`). `HistoryStore.LoadActivity(day)` loads a day's captures
  without frames (fast enough per day; see the month note below); `HistoryStore.DayRange(day)` gives its bounds.
  A capture lasts until the next one, at most `interval × 1.5` (`maxCaptureLength`).
- **App**: `MainWindow` hosts the views; Overview and Timesheet are `PlaceholderView`s to replace
  (`MainWindow.axaml.cs`, fields `_overview` and `_timesheet`). `MainViewModel.View` switches views;
  `App.Start` opens on `MainView.DayReview` for now — **switch it to Overview in phase 6** (the agreed start view).
  Each view has the same frame: tool bar, `RecordingMessages` under it, status bar starting with
  `RecordingIndicator` (both get the `RecordingViewModel` as their DataContext in code; give them
  `DataContext="{x:Null}"` in XAML so they don't bind to the view's model first). Day review's side panel
  (`DayReviewViewModel.Projects.cs`) already has the pieces to copy: `ProjectTimes.Hm` (h:mm) and
  `ProjectTimes.ParseDuration` (4:45, 4.75, 4h45, 45m), `TimeLogRow` (an editable logged cell), project colours via
  `ProjectColors.Brush(ProjectStore.ColorOf(project, categories))`, cell styles (`TextBox.cell`, `Grid.gridHeader`,
  `Border.row`) in `DayReviewView.axaml` (move shared ones to `Styles/Breeze.axaml` if both views need them).
  `DayReviewViewModel.GoToDay(day)` opens a day.

## Phase 5 — Timesheet view

Replace the Timesheet placeholder (mockup board *Timesheet*, round 4):

- One line per project per day: **Project** (colour, name), **Notes** (block notes joined), **Active**,
  **Productive**, **Assigned**, **Logged** (editable cell; typed like Day review's Time Log; saved at once with
  `SetLogged`, empty clears it). The column names and their meaning are agreed (PROJECT.md 1a, *Times*); the status
  bar spells them out in one line.
- A heading row per day: the date (double-click or a link opens it in Day review), its day note, the day's totals.
- Tool bar: ‹ week › and This Week, toggle tool buttons **Week** / **Month**, and **Export CSV…** as a plain tool
  button (a save dialog; `TimesheetCsv`, decimal hours). Right of the grid, a group box with totals by category →
  project (Assigned, Logged) for the range.
- Load off the UI thread with its own database connection (connections are single-thread); a month is ~31
  `LoadActivity` calls — measure it. Refresh when the view is shown again and when Day review changes logged time
  or notes (they share the database; reloading on show is enough).
- Edge cases: days with nothing recorded but logged time or a hand-set range (they still get lines); archived
  projects (still listed with "(archived)"); a project deleted in Settings while the view is open.

## Phase 6 — Overview (the start view)

Replace the Overview placeholder (boards *Overview · week*, *Overview · month*, *Overview · recording paused*):

- **Week mode:** one row per day (Mon–Sun): the date and span, a timeline (projects lane above a thin activity
  lane; reuse `TimelineControl` or a lighter control drawing the same colours), and the day's Assigned and recorded
  totals. Clicking a day opens it in Day review (`MainViewModel.View = DayReview` + `GoToDay`). ‹ week ›, This Week.
- Below: a tree, category → project, with a column per day (Assigned), then Assigned and Logged totals; and a
  **No project** branch with Productive and Other (active time without a project). Categories expand and collapse.
  Beside it, the day notes; each expands to show that day's block notes.
- **Month mode** (toggle tool buttons Week / Month): a month grid replaces the timelines (each day: logged and
  recorded totals and a bar coloured by category); the tree's columns become weeks.
- Recording state: the status bar indicator and message bars like every view (the paused board shows it).
- **Month totals need a lighter path than building every day's timeline** — e.g. one SQL pass per day range that
  sums capture lengths per linked project, plus the hand-set ranges; or cache per day. Measure first; the developer
  cares about speed and memory.
- Make the app open on the Overview (`App.Start`, and `MainViewModel`'s default when the tray reopens the window).

## What the boards don't show (decided after them)

- Nothing is ever joined automatically; the Projects tab offers to join gaps under 10 minutes.
- Clear Project leaves the selection with no project (overriding links); there is no "back to links".
- A selection in Day review is a time range with typeable ends (so unrecorded time can be given a project).
- Settings stays a dialog; Projects and Applications are tabs in it; Defaults shows only on General and Appearance.
- Logged time is per project per day, typed in Day review's Time Log or the Timesheet; no per-block logged time.
- No suggestions anywhere.

## Known gaps to keep in mind

- Dropdowns, menus and flyouts haven't been seen in screenshots (headless can't show popups); the developer clicked
  through phases 3–4 and found them fine.
- A project renamed in Settings is re-sorted only when the dialog is reopened (re-sorting while typing takes the
  focus).
- Schema 9 is one-way; the developer's real data is most likely on it now (they ran the phase 3–4 build).
