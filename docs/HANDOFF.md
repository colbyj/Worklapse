# Handoff: designing how Day review assigns time to projects

**Done 2026-10-19:** agreed (canvas Round 5) and built; see PROJECT.md *1a*, "Day review side panel, redesigned".

Written 2026-10-19 for a new Claude session. The projects work (PROJECT.md *Next steps → 1a*, phases 1–6) is built
and committed. Using it, the developer found that Day review's **side panel** doesn't yet feel right for the job it
exists for: saying which project each stretch of the day belongs to. In their words: *"I suspect we're going to have to
iterate on the sidebar and come up with a way to assign things to projects that feels right. I should have iterated on
this a bit more before moving forward with implementation."*

So this task is **design first**. Mock the options up, talk them through with the developer, and get one approved
before changing any code. (The previous handoff, for the Timesheet and Overview, is in git history.)

## Before you start

1. Read `docs/PROJECT.md` in full. The sections that matter most:
   - *Next steps → 1a*: the agreed design, the times (Active, Productive, Assigned, Logged), and the build log.
   - Phase 4's entry: what the side panel is today, and the 2026-10-19 changes.
   - Phase 1's entry: the data rules.
2. Look at the approved mockups: https://claude.ai/artifact/2HyWXXPGWRR6bJR232Nomx. It's the developer's design canvas; read
   it with the Artifact tool, not a web fetch. Page **Round 4**, boards *Day review · Applications tab*, *· Projects
   tab*, *· Time Log tab* and *· selection across two projects*. Add the new work there as a **Round 5** page and keep the
   earlier rounds as they are.
3. Look at what's actually built. Screenshots of the current panel, headless, on a scratch day, light theme, 1440×900:
   `C:\Users\colby\AppData\Local\Temp\claude\C--Users-colby-Desktop-TimeSnapperClone\868eee90-d3e5-4a9b-8c31-ed087dc64752\scratchpad\sidebar-shots\`
   - `day-applications-tab.png`, `day-segments-tab.png`, `day-projects-tab.png` and `day-timelog-tab.png`: the four tabs
     shown with no selection.
   - `day-selection.png`: a selection that spans two projects.
   If those are gone, see *Seeing the current UI* below.
4. Working rules from the developer:
   - Ask before adding anything they haven't asked for.
   - When a choice changes what they see or keep, ask; for internal choices, pick the sensible option and say so.
   - Push back if something looks wrong. Never tell them they're "absolutely right"; keep a neutral, professional tone.
   - Commit only when asked. From bash: `git commit -F - <<'EOF' … EOF`, never a PowerShell here-string.
   - **Desktop idiom** (they check it):
     - no segmented "pill" controls; use toggle tool buttons, radio tool buttons or combo boxes;
     - real framed tabs;
     - grids with column headers, and cells that turn into a text box only while edited;
     - no accent buttons in tool bars;
     - no cards, badges or hover-only controls;
     - the app's look is KDE Breeze with a little Windows flavour, at desktop density.
   - **UI text is terse:** one short line, no figures, no sentences of explanation.
   - **No suggestions anywhere** (nothing guessed or proposed by the app), and **nothing joined automatically**.
5. Never touch the real data (`%LOCALAPPDATA%\Worklapse`, settings in `%APPDATA%\Worklapse`). Never bring windows to the
   front or send keys; the developer may be using the machine.

## How assigning works today (the model the design has to sit on, or change on purpose)

- **Links:** an app (by exe name) or a site (by host name) can be linked to a project. A link marks that app's or
  site's **active** time with the project, on every day: links are looked up when a day is read.
  - A site's link beats its browser's; a site without a link leaves it to the browser's.
  - Idle and unrecorded time never come from links.
  - Links are set on Settings → Applications, or on Day review's Applications tab.
- **Ranges set by hand:** a stretch of time set to a project, or to "no project". They override links and can cover
  idle or unrecorded time (a call while away).
  - They never overlap. Setting one replaces whatever was set there before (`ProjectStore.SetSpan`), and neighbours
    with the same project merge.
  - There is no "back to links" command: once a stretch is set by hand, it stays set.
- **Project blocks** are computed from links and ranges (`ProjectTimeline.Build`): projects never overlap, and nothing is
  joined on its own. The Projects tab offers to **join** a gap under 10 minutes between two blocks of the same
  project, which sets the gap by hand.
- **Productive** is a flag per app or site: the site decides when there is one. It applies to all history and lives in
  the database.
- **Notes** belong to a project block. Each is stored with its project and a moment, and shown on the block that
  contains that moment (`NotePlacement`). There is also one **day note** per day.
- **Logged time** is typed per project per day, on the Time Log tab or in the Timesheet.
- **A selection** is a range of time with typeable ends, so unrecorded time can be selected. Deleting acts on the
  captures inside it.

## What the side panel is today (as of commit 42ac57f)

The panel is a toolbar toggle, 440 px wide, on the right of Day review.

**No selection: five tabs, with the day note under them.**
1. **Applications:** the day's apps and sites, with Active time, a Productive checkbox and a Project link combo. Both
   controls apply to all history. Hint: "Changes apply to the app on every day".
2. **Recorded Segments** (added 2026-10-19 at the developer's request): the day from first capture to last, as runs.
   - Each app or site in a row, listed again each time you switch back, plus Idle and Not recorded rows.
   - Columns: time, app or site, length, Pr. (Productive), Project.
   - Project here sets **that stretch only**, as a range set by hand. The combo shows "Mixed" when the stretch is split.
   - Idle and Not recorded rows have no controls.
3. **Projects:** the project blocks, each with "via Code.exe" or "set by hand", a note box, and a Join spot between
   close blocks of the same project.
. **Marked** (added 2026-10-19 at the developer's request, replacing the tool bar's Marked button; tooltip "Marked
   for removal"): ranges marked for deletion with Go To and Restore, and Delete Permanently with the marked total below.
   The panel's tabs have 8 px padding (not 14) so the five fit on one row at 440 px.

**With a selection:**
- Start and end boxes, the length, and a bar of active, idle and unrecorded time.
- **Recorded:** the same segment rows, clipped to the selection.
- **Projects and notes:** a note box per block in the selection, plus a "No project" line.
- Action bar: **Set Project ▾**, **Clear Project**, **Mark for Deletion**. The same commands are on the timeline's
  right-click menu.

Code: `src/Worklapse.App/Views/DayReviewView.axaml` (+ `.axaml.cs`), `src/Worklapse.App/ViewModels/DayReviewViewModel.Projects.cs`
(rows, building, saving), `src/Worklapse.App/Controls/TimelineControl.cs` (the projects lane). Data: `src/Worklapse.Core/Projects/`.

## What the developer has said, and the loose ends

- **2026-10-19, after using it:**
  - Choosing a project for a recorded segment should set only that segment. It used to change the app's link for all
    history; changed now.
  - The Recorded list was worth having on the panel without a selection too; that became the Recorded Segments tab.
  - Then: iterate on the panel before building more.
- **Loose ends visible in the current build:**
  - In a segment row, Project now applies to that stretch, but Productive still marks the app on every day. Two
    controls in one row with different reach.
  - Two different ways to put a project on time sit side by side with different reach: the Applications tab's combo
    (a link, all history) and a segment's combo (this stretch). Nothing in the UI tells them apart beyond a hint line.
  - Idle and Not recorded segments can only get a project through a selection plus Set Project.
  - Five tabs plus a selection mode in 440 px. The Applications tab's Active column cuts "4 h 43 m", and long
    project names get cut in segment rows.
  - "Clear Project" means a range set to no project, which overrides links. Undoing a hand-set stretch back to "follow
    the links" isn't possible; that was agreed, but it may matter more now that segment combos make hand-set ranges
    easy to create.

## Questions worth exploring with the developer (don't settle them yourself)

- Where does assigning happen: from segments, from the timeline (drag, then a project), from app links, or a mix with
  one obvious primary path?
- In a row about a stretch of time, what should Productive mean? Should it move to the Applications tab only?
- Should idle and unrecorded stretches have a project control in lists?
- Should links and hand-set ranges look different wherever a project is shown (e.g. "via Code.exe" vs "set by hand",
  as the Projects tab does)?
- Do the no-selection tabs still make sense as Applications → Recorded Segments → Projects → Time Log → Marked (raw
  to curated, then deletions)? Or should some merge? How does the selection panel relate to them?
- Is a way back to "follow the links" for a stretch now wanted?

Offer two or three distinct alternatives as boards rather than one, each with a sentence on its trade-off, and let the
developer react. They decide; record what's agreed in PROJECT.md *1a* (decisions with their reasons) before
building.

## Seeing the current UI (headless, nothing on the developer's screen, no real data)

- The last session's harness: `C:\Users\colby\AppData\Local\Temp\claude\C--Users-colby-Desktop-TimeSnapperClone\868eee90-d3e5-4a9b-8c31-ed087dc64752\scratchpad\shots\`.
  - It's a console project in `proj/` that references `src/Worklapse.App`, plus `Avalonia.Headless`, `Avalonia.Skia` and
    `Avalonia.Desktop` 12.1.4. Beside it sit empty `Directory.Build.props` and `Directory.Packages.props`, and
    `ManagePackageVersionsCentrally=false`.
  - `Data.cs` builds a scratch October.
  - Build it with `dotnet build -o ../bin`, then run `bin/Shots.exe day|overview|timesheet [dark]` with the env var
    `SHOTS_ROOT` set to the `shots` folder. PNGs go to `out/`.
  - It sets `WORKLAPSE_CONFIG_DIR` to a scratch folder and checks that the data folder is scratch before anything else.
- Popups (menus, combo drop-downs) don't render in these shots. To check a menu's items, inspect its presenter: see
  `DayHarness.cs`.
- Binding warnings in the output are what to watch. Those about `DayReview.…` "Value is null" while the window closes
  are expected.
- Inline Python in a bash heredoc sometimes breaks on quotes; write scripts to a file and run that. Use `python3`,
  never `python`.

## Building and testing

- **Build:** `dotnet build`. If the developer's Worklapse is running, it locks `src/Worklapse.App/bin`; build through the
  harness instead (`-o` into its own folder).
- **Tests:** run `tests/Worklapse.Tests/bin/Debug/net10.0/Worklapse.Tests.exe` (`dotnet test` reports "Zero tests ran"
  on this machine). 160 tests pass at `42ac57f`. The App has no test project, so put logic worth testing in Core.
- **Avalonia 12 gotchas met so far:**
  - A `MenuFlyout` takes its item collection when its popup is made, before `Opening`, so keep one collection and
    refill it there (`DayReviewView.FillWhenOpening`).
  - The `DispatcherTimer` constructor that takes a callback also starts the timer.
  - Exclusive tool-button pairs are `RadioButton.tool`, because a `ToggleButton` unchecks when clicked again.
