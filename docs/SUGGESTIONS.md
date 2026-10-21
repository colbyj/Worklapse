# Suggested projects: plan

Worklapse suggests projects for time that has none, from what was on screen, and the developer accepts or changes each
suggestion on Day review. This file is the plan agreed so far (2026-10-20), with the proof-of-concept results it rests on and
the choices still open. Mockups: the design canvas's **Round 7** page.

## What the proof of concept showed

Run outside the repo (a session scratchpad) on a copy of the real database. Each day was scored against the developer's own
hand-set ranges, with that day left out of training. June 17 was labelled frame by frame for this; the other days were
labelled as billing blocks. Unproductive time counts as no project (see Decisions).

| Method | June 17 | All 34 labelled days |
|---|---|---|
| App/site links and title rules (what Worklapse does today) | 55% | 39% |
| Text classifier: naive Bayes on app, site and window-title words, then blocks | 64% | 78% |
| Local vision model (Gemma 4 e2b in LM Studio) on each capture | 75% | – |
| Text classifier, the vision model where the classifier is unsure | 87% | – |
| **Text classifier + words read off the screen (Windows OCR)** | **94%** | 82%* |

\* Only every third capture of the other imported days was read, and the October days (AV1) not at all, so the overall OCR
figure understates it; June 17 is the only like-for-like day.

- The classifier's misses are windows whose title says nothing new, mostly Claude Code terminal sessions. The screen gives them away
  (`direct_thermal_preset`, `Presayo.Core/...`); OCR words recover them.
- Cost: Windows OCR took ~83 ms per picture on one thread (~37 ms each on four). At a 10 s capture interval, reading every
  capture is under 1% of one core. The vision model took ~1.6 s per capture (5 s at the 95th percentile) on the GPU and needs
  LM Studio running. **No language model** (decided): the classifier and OCR did better, and run anywhere.
- Training the classifier on all labelled history takes milliseconds.

## Decisions

- **What gets suggestions.** Only active time with no project from a hand-set range, an app/site link or a title rule.
  Suggestions never change time that already has one.
- **Suggestions are billing blocks.** One suggestion is a stretch the developer would bill to one project, made of the unassigned
  pieces inside it. Accepting writes an ordinary hand-set range (`project_spans`), so the Timesheet and Overview need no change.
- **Breaks inside a block.** Shorter than the glance limit (default 2 min): part of the block. Longer than the split limit
  (default 30 min): end the block. In between: listed under the suggestion with a Count box for the developer to decide,
  **not counted** by default. Breaks include idle time (often waiting for a build) and time in other apps. Both limits are
  settings.
- **Unproductive time is not project time**, even inside a hand-set range. For training and scoring, a capture whose Productive
  mark is off (the site's when it has one, else the app's) counts as no project. Inside a suggested block it is a break.
- **Short switches to another project.** Inside a block, the developer sometimes labels a short stretch of another project's work
  as that project and sometimes leaves it in the block. So: training leaves out a short stretch that clearly belongs to another
  project (its links, its title rules, or a confident guess) instead of learning it as the surrounding project; scoring doesn't
  count those stretches against the classifier; and a suggested block lists them as **switches**, **split out** as their own
  project by default (a switch can be known for sure), with a Keep box to leave one in the block.
- **Rejecting is remembered**, so the same block isn't suggested again.
- **Learn off** for an app or site: its windows neither teach nor sway suggestions; its time is guessed from what's around it.
- **Opting out.** Per app or site: *Read screen* (OCR its windows or not) and *Learn* (its windows teach and sway suggestions or
  not). Per project: *Suggest it* (off: never suggested, and its time teaches nothing) and *Read the screen during its time*.
  Turning reading off deletes the text already read. A globally excluded app is never recorded, so it has neither.
- **Privacy.** Read text follows the same rule as pictures: deleted content must be gone. It is deleted with its capture
  (deletes, reclaim), and when reading is turned off for its app, site or project. Only words are kept, never the text as read.
- **Day review panel** (Round 7, agreed): tabs Segments (was Recorded Segments, shortened so five fit) · Applications ·
  Suggestions · Time Log · Marked. Only Segments and Applications follow Day/Selection (a toggle at the top of those two tabs).
  Suggestions, Time Log and Marked are always the whole day; the day note lives on the Time Log tab. The selection gets a strip at the bottom of the panel (typeable ends, length, clear) above Set Project /
  Clear Project / Mark for Deletion, on any tab. The frame slice is headed **Current frame** with the playhead's red mark.
  The selection-only Projects tab goes (each project's time in the selection is no longer shown; accepted).
- **Timeline.** Set projects filled, suggestions outlined in the project's colour with a faint tint, undecided breaks dashed
  with a "?"; a switch outlined in its own project's colour. Pointing at a suggestion, break or switch lights up its time,
  like the panel's other controls.

- **Overview** shows each day's count of open suggestions, so days needing review stand out.
- **Right-click on the timeline**: Accept and Reject in the menu of an outlined suggestion (and Count / Keep on a break or
  switch), next to the timeline's other right-click actions. The developer works the timeline by right-click a lot.

- **No suggestions on time a link or title rule already gave a project**, for now (e.g. June 17, 14:47: the `Frankensystem`
  title rule took a terminal the developer set to Homelab by hand). Links and rules are deliberate, and wrong ones are rare and
  quick to fix by hand.

No choices are open.

## Design

### Data (schema 13)

```sql
-- Words read off a capture's active window (lower case, each once), for suggestions only.
CREATE TABLE screen_words (
    capture_id  INTEGER PRIMARY KEY REFERENCES captures (id) ON DELETE CASCADE,
    words       TEXT NOT NULL                    -- space-separated
);

ALTER TABLE apps     ADD COLUMN read_screen INTEGER NOT NULL DEFAULT 1;
ALTER TABLE apps     ADD COLUMN learn       INTEGER NOT NULL DEFAULT 1;
ALTER TABLE sites    ADD COLUMN read_screen INTEGER NOT NULL DEFAULT 1;
ALTER TABLE sites    ADD COLUMN learn       INTEGER NOT NULL DEFAULT 1;
ALTER TABLE projects ADD COLUMN suggest     INTEGER NOT NULL DEFAULT 1;
ALTER TABLE projects ADD COLUMN read_screen INTEGER NOT NULL DEFAULT 1;

-- A suggested block and what became of it. Open ones are recomputed; decided ones are kept so a rejected block
-- isn't suggested again and an accepted one isn't offered twice.
CREATE TABLE suggestions (
    id          INTEGER PRIMARY KEY,
    start_ms    INTEGER NOT NULL,
    end_ms      INTEGER NOT NULL,
    project_id  INTEGER NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    confidence  REAL NOT NULL,
    evidence    TEXT NOT NULL,                   -- the words that decided it, for the panel
    state       INTEGER NOT NULL                 -- 0 = open, 1 = accepted, 2 = rejected
);

-- Breaks and switches inside a suggestion, and the developer's decision on each.
CREATE TABLE suggestion_parts (
    suggestion_id  INTEGER NOT NULL REFERENCES suggestions (id) ON DELETE CASCADE,
    start_ms       INTEGER NOT NULL,
    end_ms         INTEGER NOT NULL,
    kind           INTEGER NOT NULL,             -- 0 = break, 1 = switch
    project_id     INTEGER REFERENCES projects (id) ON DELETE SET NULL,   -- a switch's project
    counted        INTEGER NOT NULL,             -- break: counted toward the block; switch: kept in the block
    PRIMARY KEY (suggestion_id, start_ms)
) WITHOUT ROWID;
```

Settings (settings.json): `SuggestProjects`, `ReadScreenText`, `GlanceMinutes` (2), `SplitMinutes` (30).

### Core: `Worklapse.Core/Suggestions`

- `CaptureFeatures`: the words of a capture. App, site, title words (split on anything not a letter or digit, so
  `bride-of-frankensystem` gives `bride`, `frankensystem`), and screen words (from `screen_words`), each kind kept apart.
  Screen words weigh 0.3 of a title word (tuned on June 17); an app or site with Learn off contributes no words.
- `TrainingSet`: what history teaches. A capture's label is its hand-set range's project, else its link or rule's project; none
  if unproductive or the range has no project. Left out: captures without any of those, projects with Suggest off, apps/sites
  with Learn off, short switches inside a longer range (above). Accepted suggestions are hand-set ranges, so they count.
  Each project's name, links and title-rule texts are added as starter examples, so a new project can be suggested at all.
- `ProjectClassifier`: multinomial naive Bayes over those words, weighted by each capture's duration; retrained from scratch
  when Day review asks for suggestions and the labels have changed since (milliseconds; nothing persisted). Gives a probability
  per project and for "no project".
- `BlockBuilder`: per-capture probabilities → blocks, by a Viterbi pass that charges for switching projects (cost 6 in log
  units in the POC; to tune), then the glance and split limits, then breaks and switches as parts. A block whose best
  probability is low is still offered, marked unsure.
- `SuggestionStore`: reads and writes the two tables; `Accept` (writes `project_spans` for the block, counting the breaks and
  switches as decided, in one transaction), `Reject`, `SetPart`; recomputes a day's open suggestions, keeping decided ones.
- `SuggestionEvaluation` (test project): the leave-one-day-out score, runnable on a copy of real data, so a change to the
  classifier is measured, not guessed.

### Reading the screen

- `IScreenTextReader` in `Platform`: words from a picture of the active window.
  - Windows: `Windows.Media.Ocr` (built in; the user's languages), reached without changing the project's target framework
    (the build folder must stay `bin/<config>/net10.0/`).
  - Linux (KDE): Tesseract, later, with its own cost and quality test; until then suggestions run without screen words there.
- What is read: the active window's rectangle (`captures.window_*`) of each capture whose picture changed, not whole
  monitors. Skipped when Read screen is off for its app, site or (when already known from a link or rule) project.
- When: on the recorder's low-priority background thread, like batch encoding, from the frame while it is still staged
  (I420 → BGRA crop), so nothing is decoded again. Imported TimeSnapper days and older own days: a one-off "Read past days"
  job, decoding JPEGs or AV1 segments, cancellable.
- Words kept: lower case, 3+ characters, not all digits, each once per capture.
- Deleted: with the capture (cascade, so deletes and reclaim cover it), and for every capture of an app, site or project whose
  reading is turned off, and when time is set to a project with reading off.

### Day review

As on the Round 7 boards: the Projects lane draws suggestions and parts; the Suggestions tab lists open suggestions with project
(changeable), block, length, Accept/Reject, what it adds, evidence, and its parts with Count boxes; Accept All in the footer;
the current-frame header; the tab and scope changes; the selection strip. Suggestions are worked out when a day opens and
after Refresh, in the background, and redrawn when they arrive.

### Settings

- New **Suggestions** tab: suggest projects on/off, what it learns from (blocks and days), read text on the screen on/off,
  the glance and split limits.
- Applications tab: *Read screen* and *Learn* columns.
- Projects tab: a *Suggestions* row in a project's details with *Suggest it* and *Read the screen during its time*.

## Phases

Each phase ends with tests passing and, where there's UI, headless screenshots for the developer to walk.

1. **Classifier and scoring, no UI.** `CaptureFeatures`, `TrainingSet`, `ProjectClassifier`, `BlockBuilder`, unit tests on
   made-up days, and `SuggestionEvaluation`. Done when the leave-one-day-out score on a copy of real data matches the POC's
   title-only figures (~78% overall, 64% on June 17).
2. **Reading the screen.** Schema 13, `IScreenTextReader` on Windows, the background reader on new captures, the opt-out
   columns and their deletion rules, the past-days job. Done when June 17 scores ~94% again from inside the app's code,
   deleting a range removes its words (tested), and recording CPU and memory are measured before and after.
3. **Suggestions in Day review.** `SuggestionStore`, the Suggestions tab, the lane drawing, Accept/Reject/Count, and the panel
   restructure (tabs, scope toggle, selection strip, current-frame header).
4. **Settings.** The Suggestions tab and the opt-out controls on Applications and Projects.
5. **Linux.** Tesseract behind `IScreenTextReader`, its own cost and quality test on Kubuntu.

## Status

**Phase 1 built 2026-10-20** (not committed yet): `Worklapse.Core/Suggestions` (`CaptureWords`, `ScreenWords`, `TrainingSet`,
`SuggestionOptOuts`, `ProjectClassifier`, `SuggestionBuilder`, `BlockPath`), `SuggestionTests` (14 tests on made-up days) and
`SuggestionEvaluation`, an explicit test run on a copy of the data:

```
WORKLAPSE_EVAL_DATA=<folder with a COPY of worklapse.db> [WORKLAPSE_EVAL_OCR=<.jsonl of {id, text}>]
  tests/Worklapse.Tests/bin/Debug/net10.0/Worklapse.Tests.exe -method "*SuggestionEvaluation*" -explicit only -showliveoutput
```

Leave-one-day-out on the 34 labelled days (2 s without OCR, 10 s with the POC's OCR text). *Guess*: the classifier guesses
every active capture. *Filled*: as in use, the day's ranges not set yet, links and rules kept, the rest guessed, scored where
nothing was linked.

| | Links and rules | Guess | Filled | Guess, OCR | Filled, OCR |
|---|---|---|---|---|---|
| June 17 | 56% | 72% | 54% | **96%** | **96%** |
| All days | 40% | 81% | 77% | 84%* | 81%* |

\* With OCR on every third capture of the imported days and none on the October ones.

Better than the POC (64% / 78% without OCR) because unproductive time is now no project by definition, links and rules
outside ranges teach too, and each project starts from its name, links and rules. Details settled while building:
- Short switches left out of training are those found by links and rules (another project's linked windows inside a range set
  by hand, in a run under 10 minutes). Leaving out *guessed* switches too would need the classifier first; not done.
- A block is offered when it adds at least a minute or has breaks or switches to decide.
- Time set to no project by hand is left alone: neither added nor a break, and a block can continue around it.
- Time not recorded inside a block (under the split limit) is a break.
- Below 70% average certainty a suggestion is unsure.
- Archived projects are to be treated like projects with Suggest off (the caller adds them to the opt-outs).

**Phase 2 built 2026-10-20** (reading the screen; no UI yet):
- Schema 13 as designed above (`suggestion_parts.counted` is named `decided`: a break counted, a switch kept).
- `ScreenWordStore`: words per capture, refused where reading isn't allowed (`MayRead`), and `Prune`. The stores that
  change what's allowed prune by themselves, so words never outlive their permission: `ProjectStore.SetSpan` (that
  range), `SetTitleRules` and `SetSuggestionOptOuts` for a project with reading off, `HistoryStore.SetProject` (a link to
  such a project) and `SetReadScreen`. Deleting captures deletes their words (cascade). An app or site with reading or
  learning off keeps its row when its captures are deleted, and new copies of an app (another path) inherit both.
- `IScreenTextReader` on `PlatformServices.TextReader`. The first Windows reader used `Windows.Media.Ocr` through a
  Windows-only target framework (`net10.0-windows10.0.19041.0`), which moved the build to another folder; the developer
  didn't want that, so it was taken out (bf10122). Now `WindowsScreenTextReader` calls the same WinRT API through COM
  (`WindowsOcrInterop.cs`, as `UiAutomationInterop.cs` does for UI Automation) on plain `net10.0`: activation factories
  from `combase.dll`, a Gray8 `SoftwareBitmap` from a `CryptographicBuffer`, and its own completion handler for
  `RecognizeAsync`. Each reading thread gets its own engine (the recorder's queue and Read Past Days can run at once).
  Checked on a copy of the evaluation data: June 17 read again gave the same words as the earlier reader for all 543
  captures, in 45 s; scores unchanged (June 17 95% guess / 95% filled, all days 86.4% / 83.6%).
- The recorder crops the focused window from the luma (Y) plane of the captured I420 frame, on the monitor showing most of
  it, and queues it after the commit (`ScreenReadingQueue`: one lowest-priority thread, at most 4 pictures waiting,
  dropping new work when full). A capture showing the same frame and window copies the words instead.
- `PastScreenReading` (Worklapse.Video): reads the captures of a time range that have no words from their stored frames
  (AV1, images, imported TimeSnapper pictures), once per distinct frame and window, on `PastReadingThreads` lowest-priority
  threads (default 4; each its own decoder and OCR engine). A screen with no words is stored as an empty row, so it counts
  as read; a frame that can't be decoded, or a reader failure, leaves the capture for another run. First the segments holding a capture training learns
  from (`TrainingSet.Assignments` on the labelled days; a video segment whole, a picture is its own segment), then the
  rest; oldest first, each segment's frames in order. Reading a video's frames backward (the first version went newest
  first) restarted the decoder at the key frame every few frames: 412 ms a capture on Oct 19 against 128 ms in order on
  one thread, 19 ms on 8. Imported TimeSnapper pictures: ~110 ms on one thread, ~21 ms on 8 (OCR is ~90% of it). On the
  developer's history (2026-10-20): 404k captures unread, 32k of them training data.
- `BackgroundScreenReading` (Worklapse.Video), owned by the app: runs Read Past Days on its own thread until done or
  stopped, whatever windows are open. `PastReadingRunning` (settings) makes it go on at the next start, quietly;
  `PastReadingReadUpTo` records what a finished run covered. After that, every 5 minutes, one thread catches up the
  captures since then (older than 2 minutes) that the recorder's queue dropped; each gets one try (the mark moves on).
  Turning reading off stops it; another data folder starts the run again there. Changing threads starts the workers again
  (~1 s), counts carried over. When a run's threads have ended (done, stopped, failed) the app collects (finalizers in
  between: the OCR engines are COM objects) — about 550 MB back to ~50 MB after 8 threads.
- Progress window (`PastReadingWindow`): time with a project ✓/▸/·, everything else, a bar, "N of M · about X" (speed over
  the last 2 minutes, shown after 20 s), threads changeable while running (applied 1 s after the last click), Stop or
  Read Past Days, Close (reading goes on). Opened from Settings' Read Past Days, which reads "Show Progress" while it runs.
- Settings: `SuggestProjects`, `ReadScreenText` (both on), `GlanceMinutes` (2), `SplitMinutes` (30).
- Measured while recording (Active application mode, 10 s interval, real screen, alternating runs of 3 minutes): CPU
  25.8% / 27.3% of one core with reading against 27.1% / 29.5% without (within the noise; capture and AV1 encoding are
  most of it), private memory about +15–30 MB (46–64 MB against 32–33 MB average). Every active capture got words.
- Scores with every imported labelled day read (the scratch OCR, before the pipeline run): guess 87% and filled 84% over
  all days (81% and 77% without words). Read again through the app's own pipeline (`PastScreenReading` with
  `WindowsScreenTextReader`, grayscale crops of the window): June 17 94% guess / 93% filled, as with the scratch OCR, and
  87% / 85% over all days. Reading the 34 labelled days took ~70 minutes of OCR (~23,000 captures).
- Evaluation: `WORKLAPSE_EVAL_OCR=db` scores with the words stored in the database copy.
- With every capture of the 34 labelled days read through the pipeline (the October AV1 days took 4–16 minutes each):
  86% guess / 84% filled over all days, 95% / 95% on June 17. October 15 dropped (78% → 70% guess) where the others
  rose: worth a look.

**Phase 3 built 2026-10-20** (suggestions in Day review, and the Overview count):
- `SuggestionStore` and `SuggestionEngine` (Core), as designed. Also `SuggestionState.Chosen`: an open suggestion whose
  project the developer changed. It's kept when the day is worked out again (otherwise any edit would undo the choice),
  and stands for its time whatever project is computed there.
- Block building, settled on June 17's real interleaving of two projects:
  - A project's stretches are joined while the time away stays within the split limit, the other projects' work in each
    stretch away within 10 minutes (`SuggestionLimits.Switch`), and the block is still mostly its own project. Without the
    last two, whichever project started first swallowed the other's work as switches.
  - One part per stretch away without a project (split only at time that has one): a switch when mostly another
    project's work, else a break. Parts under a minute aren't asked about (they go with the block).
  - Evidence words shorter than 4 letters aren't shown.
- Day review: the five tabs, the Day/Selection pair inside Segments and Applications, the selection strip at the bottom, the
  day note on Time Log, the "Current frame" header, the Suggestions tab (project changeable, Accept/Reject, Count/Keep,
  Accept All, what it adds, the words it rests on, a count on the tab), suggestions drawn on the lane (outlined; undecided
  breaks dashed with "?"; switches split out in their project's colour), and the timeline's right-click on a suggestion
  (Accept, Reject, and Count/Keep for the part clicked). Suggestions are worked out in the background whenever the day's
  projects change; the stored ones show at once. The selection's activity bar and per-project breakdown went (the status
  bar shows the selection's totals).
- Overview: week mode works out the week's suggestions in the background and shows "N suggestions" under each day; month
  mode shows the stored counts beside the day numbers (working out 35 days on every visit would be slow).
- Checked in headless screenshots on a copy of June 17 with its ranges removed: three suggestions matching the developer's
  frame labels (12:01–12:17 Frankensystem with the DBeaver minutes as a switch to Presayo; 2:36–4:11 Presayo with the
  Facebook breaks); accepting the Presayo block added exactly the 0:47 it said, and its switch went to Frankensystem.

**Phase 4 built 2026-10-20** (Settings):
- A Suggestions tab: suggest projects on or off, what it learns from ("Time you set by hand: 164 blocks over 18 days"), text
  on the screen on or off, Read Past Days (reads the screens of everything recorded before, what training learns from first,
  in the background with its own progress window; it goes on after the dialog closes and after a restart; only where the
  platform has a reader), and the glance and split limits. Turning reading off here stops it but keeps the words read so far; turning
  it off for an app, site or project deletes them.
- Applications: Read screen and Learn columns (sortable, greyed while the app is excluded), saved on Apply.
- Projects: a Suggestions row in a project's details, Suggest it and Read the screen during its time, saved on Apply.

**Review and fixes 2026-10-20** (after the developer's first use on real data):
- A suggestion is drawn in pieces (the stretches between its breaks and switches, and those), so accepting it from one
  piece looked like accepting every Presayo bar around it (June 18). Now a thin bar along the lane's bottom joins a
  suggestion's pieces, and the right-click names what it takes: *Accept Whole Suggestion: Presayo, 11:31 AM – 1:02 PM*
  and *Accept This Piece Only* (the piece clicked, as the project it's drawn in; the rest stays open). Also *Change
  Suggestion to* (its project, as on the Suggestions tab).
- Working a day out again kept no ids (every open suggestion was deleted and inserted), so a click made just after a
  window activation could find nothing and do nothing. An unchanged suggestion now keeps its id; a click on one that
  changed meanwhile shows the new one.
- Decisions on breaks and switches are matched by overlap, not exact ends (a capture arriving moved them).
- A decided block hides a new one when they overlap by half of the *new* block (was: of the shorter, so a small rejected
  block hid a large one around it); a chosen block still by the shorter.
- A day's suggestions are those starting in it (neighbouring days' showed and were accepted after a day-start change).
- Accept and Accept All read the day's captures as stored now (on today, the ones shown can be older than the suggestion).
- A switch whose project was deleted is drawn as a break, as Accept treats it.
- Screen words: the permission check and the write are one transaction (reading turned off in between left words);
  Read Past Days logs a reader failure once and goes on instead of failing.
- "No suggestions for this day" no longer shows beside "Suggestions are off"; "Working out suggestions…" no longer sticks
  when they're turned off mid-run.

**After the review, 2026-10-20** (the developer's answers):
- *Accept This Piece as ▸* (any piece, a break too) on the timeline's right-click; *Accept as ▸* when the suggestion is one
  piece.
- A certainty limit, `MinimumCertainty` (51%, Settings → Suggestions → "Offer when"): a block less sure than that isn't
  offered. A block that guesses no time (only breaks to decide around linked time) counts as sure. Evidence from the
  evaluation's new table (offered blocks by certainty, all labelled days with words): under 51%, 3 blocks adding 5 minutes,
  almost all wrong; 60–70% right 100% (1 min), 70–80% 94%, 80–90% 74%, 90%+ 86%.
- Once the developer picks a suggestion's project, its evidence words are hidden and it's no longer shown as unsure.
- One `SuggestionEngine` for the app (made by `App`, shared by Day review and the Overview), taking one call at a time.
  It remembers what each day was worked out from (the classifier's stamp, the day's captures and words, the settings) and
  reads the stored suggestions instead when nothing changed.
- The Overview, week and month alike, shows the stored counts at once, then works out again (newest first, in the
  background) each day with time without a project or open suggestions, updating each count as it's done. On a copy of
  the developer's data, October (1–20, only 13–20 worked out before) took 13 s the first time.
- Settings text: the Applications hint is back to what it was (the Read screen and Learn notes moved to their column
  tips); "Accepted suggestions teach it too"; "some CPU while recording" (was "after"); shorter hints elsewhere.

**Project dates, 2026-10-20** (schema 14, `projects.starts_on` / `ends_on`, both optional and inclusive): a project is
never suggested on a day outside its dates. On such a day the classifier leaves it out and the projects running share its
probability, so the next likeliest wins rather than the time going unsuggested. Set under a project's Suggestions row
in Settings → Projects (first day, last day; blank: no limit). Changing the dates works the affected days out again.

**New suggestions wait after an edit, 2026-10-20**: accepting retrains (the accepted time is a range set by hand) and
works the day out again, which replaced the list a while after the click, mid-way through it. Now, after an edit in Day
review (an accept, a range set by hand), suggestions worked out again that change the blocks wait behind *Suggestions
changed · Refresh* above the list; the lane keeps drawing the ones listed. Only certainty or evidence changing is shown at
once, as is anything when no suggestion is open. Window activation reloads the day, so the wait also holds through
reloads of the same day; opening another day, or Refresh, ends it. What waits isn't stored
(`SuggestionEngine.WorkOutAsync`, then `Store` on Refresh), so rejecting, Count/Keep and changing a project meanwhile act
on the suggestions shown, and Refresh keeps those decisions as working a day out again always does. Rejecting and
changing a project don't work the day out again: the classifier learns only from time with a project.

Left for phase 5 (Linux): Tesseract behind `IScreenTextReader`, with its own cost and quality test on Kubuntu.

## Risks and unknowns

- Hand labels are noisy (billing blocks, inconsistent short switches); scores are a guide, not a guarantee.
- OCR cost was measured on window-sized TimeSnapper JPEGs; very large windows (3440×1440) cost more, still to measure.
- Tesseract's quality on terminals and code is unknown.
- A project with no history and no links or rules has nothing to learn from; it gets suggestions only after some time is set to it.
