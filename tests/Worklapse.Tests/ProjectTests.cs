using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

public class ProjectTests
{
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly DateOnly Day = DateOnly.FromDateTime(Nine.LocalDateTime);

    private static CaptureRecord Capture(int seconds, long? appProject = null, CaptureKind kind = CaptureKind.Active,
        string? site = null, long? siteProject = null, bool productive = false, bool marked = false) =>
        new(seconds, Nine.AddSeconds(seconds), kind, "app.exe", site, "Title", null, productive, site is null ? null : productive,
            marked, [], [], appProject, siteProject);

    private static ProjectTime Build(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan>? spans = null) =>
        ProjectTimeline.Build(captures, spans ?? [], Nine.AddHours(-1), Nine.AddHours(1), Max);

    private static (int From, int To, long Project)[] Blocks(ProjectTime time) =>
        time.Blocks.Select(b => ((int)(b.Start - Nine).TotalSeconds, (int)(b.End - Nine).TotalSeconds, b.ProjectId)).ToArray();

    private static ProjectSpan Span(int from, int to, long? project) => new(Nine.AddSeconds(from), Nine.AddSeconds(to), project);

    [Fact]
    public void LinksMarkActiveTimeAndASiteDecidesBeforeItsBrowser()
    {
        var time = Build([
            Capture(0, appProject: 1),
            Capture(10, appProject: 1),
            Capture(20, appProject: 2, site: "github.com", siteProject: 3), // the site's link wins
            Capture(30, appProject: 2, site: "docs.example"),                 // an unlinked site leaves it to the browser
            Capture(40, kind: CaptureKind.Idle),
            Capture(50, appProject: 1),                                       // the last capture lasts the maximum
        ]);

        Assert.Equal([(0, 20, 1L), (20, 30, 3L), (30, 40, 2L), (50, 65, 1L)], Blocks(time));
        Assert.Equal(TimeSpan.FromSeconds(10), time.Unassigned.Idle);
        Assert.Equal(TimeSpan.FromSeconds(35), time.Totals[1].Assigned);
        Assert.Equal(TimeSpan.FromSeconds(35), time.Totals[1].Active);
    }

    [Fact]
    public void IdleUnrecordedAndMarkedTimeBelongToNoProject()
    {
        var time = Build([
            Capture(0, appProject: 1),
            Capture(10, appProject: 1, kind: CaptureKind.Idle), // an idle capture of a linked app
            Capture(100, appProject: 1),                        // after a gap: not recorded in between
            Capture(110, appProject: 1, marked: true),
            Capture(120, appProject: 1),
        ]);

        Assert.Equal([(0, 10, 1L), (100, 110, 1L), (120, 135, 1L)], Blocks(time));
        Assert.Equal(TimeSpan.FromSeconds(15), time.Unassigned.Idle);
        Assert.Equal(TimeSpan.FromSeconds(35), time.Totals[1].Assigned);
    }

    [Fact]
    public void RangesSetByHandOverrideLinksAndCoverIdleAndUnrecordedTime()
    {
        var time = Build(
            [Capture(0, appProject: 1), Capture(10, appProject: 1), Capture(20, appProject: 1), Capture(30, kind: CaptureKind.Idle), Capture(100, appProject: 1)],
            [Span(25, 100, 2), Span(100, 105, null)]); // a phone call while idle and away, then "no project"

        Assert.Equal([(0, 25, 1L), (25, 100, 2L), (105, 115, 1L)], Blocks(time));
        var call = time.Totals[2];
        Assert.Equal(TimeSpan.FromSeconds(75), call.Assigned);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Active); // 25..30 was recorded as active
        Assert.Equal(TimeSpan.FromSeconds(75), time.Blocks[1].SetByHand);
        Assert.Equal(TimeSpan.Zero, time.Blocks[0].SetByHand);
        Assert.Equal(TimeSpan.Zero, time.Unassigned.Idle);
    }

    [Fact]
    public void ShortGapsBetweenBlocksOfTheSameProjectAreOfferedForJoiningNeverJoinedOnTheirOwn()
    {
        CaptureRecord[] captures = [
            Capture(0, appProject: 1), Capture(10, appProject: 1),
            Capture(20), Capture(30),                                        // 20 s elsewhere
            Capture(40, appProject: 1),
            Capture(50),                                                     // between two different projects
            Capture(60, appProject: 2),
            Capture(70, kind: CaptureKind.Idle), Capture(80, appProject: 2), // idle counts as a gap too
        ];
        var time = Build(captures);
        Assert.Equal([(0, 20, 1L), (40, 50, 1L), (60, 70, 2L), (80, 95, 2L)], Blocks(time));

        var gaps = ProjectTimeline.JoinableGaps(time.Blocks, TimeSpan.FromSeconds(21));
        Assert.Equal([Span(20, 40, 1), Span(70, 80, 2)], gaps);
        Assert.Equal([Span(70, 80, 2)], ProjectTimeline.JoinableGaps(time.Blocks, TimeSpan.FromSeconds(20))); // shorter than, strictly

        // Joining is setting the gap by hand.
        var joined = Build(captures, [gaps[0]]);
        Assert.Equal([(0, 50, 1L), (60, 70, 2L), (80, 95, 2L)], Blocks(joined));
        Assert.Equal(TimeSpan.FromSeconds(20), joined.Blocks[0].SetByHand);
        Assert.Equal(TimeSpan.FromSeconds(50), joined.Totals[1].Active);

        // A block of another project in between means nothing is offered; ten minutes or more neither.
        ProjectBlock Block(int fromMinute, int toMinute, long project) =>
            new(Nine.AddMinutes(fromMinute), Nine.AddMinutes(toMinute), project, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal([new ProjectSpan(Nine.AddMinutes(10), Nine.AddMinutes(15), 1)], ProjectTimeline.JoinableGaps(
            [Block(0, 10, 1), Block(15, 20, 1), Block(30, 40, 1), Block(42, 50, 2), Block(52, 60, 1)], ProjectTimeline.JoinableGap));
    }

    [Fact]
    public void ProductiveTimeIsCountedPerProjectAndForTimeWithoutOne()
    {
        var time = Build([Capture(0, appProject: 1, productive: true), Capture(10, appProject: 1), Capture(20, productive: true), Capture(30)]);

        Assert.Equal(new ProjectTotals(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)), time.Totals[1]);
        Assert.Equal(new UnassignedTotals(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.Zero), time.Unassigned);
    }

    [Fact]
    public void TimeIsClippedToTheSpanAsked()
    {
        var time = ProjectTimeline.Build([Capture(0, appProject: 1), Capture(10, appProject: 1)], [Span(-100, 5, 2)],
            Nine, Nine.AddSeconds(20), Max);

        Assert.Equal([(0, 5, 2L), (5, 20, 1L)], Blocks(time));
    }

    [Fact]
    public void SettingARangeReplacesWhatWasThereAndJoinsItsNeighbours()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new ProjectStore(db);
        var (a, b) = (store.AddProject("A", null), store.AddProject("B", null));
        (int, int, long?)[] Spans() => store.GetSpans(Nine.AddHours(-1), Nine.AddHours(1))
            .Select(s => ((int)(s.Start - Nine).TotalSeconds, (int)(s.End - Nine).TotalSeconds, s.ProjectId)).ToArray();

        store.SetSpan(Nine, Nine.AddSeconds(100), a);
        store.SetSpan(Nine.AddSeconds(40), Nine.AddSeconds(60), b);
        Assert.Equal([(0, 40, a), (40, 60, b), (60, 100, a)], Spans());

        store.SetSpan(Nine.AddSeconds(60), Nine.AddSeconds(80), b);
        Assert.Equal([(0, 40, a), (40, 80, b), (80, 100, a)], Spans());

        // Clear Project: "no project", which overrides links too.
        store.SetSpan(Nine.AddSeconds(10), Nine.AddSeconds(20), null);
        Assert.Equal([(0, 10, a), (10, 20, null), (20, 40, a), (40, 80, b), (80, 100, a)], Spans());

        store.SetSpan(Nine.AddSeconds(30), Nine.AddSeconds(90), null);
        Assert.Equal([(0, 10, a), (10, 20, null), (20, 30, a), (30, 90, null), (90, 100, a)], Spans());

        Assert.Throws<ArgumentException>(() => store.SetSpan(Nine, Nine, a));
    }

    [Fact]
    public void LinksApplyToAllHistoryAndToNewCopiesOfAnApp()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        var worklapse = projects.AddProject("Worklapse", null);
        var client = projects.AddProject("Northwind", null);

        var code = recording.GetOrAddApp("Code.exe", @"C:\Code\Code.exe");
        var firefox = recording.GetOrAddApp("firefox.exe", null);
        recording.InsertCapture(Nine, CaptureKind.Active, code, "t", null);
        recording.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, firefox, "t", null, recording.GetOrAddSite("northwind.example"));

        history.SetProject(new ProgramKey(ProgramKind.App, "code.exe"), worklapse); // app names ignore case
        history.SetProject(new ProgramKey(ProgramKind.Site, "northwind.example"), client);
        Assert.Equal([worklapse, client], history.LoadDay(Day).Captures.Select(c => c.LinkedProjectId));
        Assert.Equal([worklapse, client], history.LoadSamples(Day, Day.AddDays(1)).Select(c => c.LinkedProjectId));

        // Another copy of Code (another path) is linked too.
        recording.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, recording.GetOrAddApp("Code.exe", @"D:\Portable\Code.exe"), "t", null);
        Assert.Equal(worklapse, history.LoadDay(Day).Captures[^1].LinkedProjectId);

        var programs = history.GetPrograms(Max).ToDictionary(p => p.Program);
        Assert.Equal(worklapse, programs[new ProgramKey(ProgramKind.App, "Code.exe")].ProjectId);
        Assert.Null(programs[new ProgramKey(ProgramKind.App, "firefox.exe")].ProjectId);

        // A linked app keeps its row (and link) when all its captures are deleted.
        history.SetPendingDelete(Nine, Nine.AddMinutes(1), true);
        history.DeleteMarked(Day);
        Assert.Contains(history.GetPrograms(Max), p => p.Program.Name == "Code.exe" && p.ProjectId == worklapse);
        Assert.DoesNotContain(history.GetPrograms(Max), p => p.Program.Name == "firefox.exe");

        history.SetProject(new ProgramKey(ProgramKind.App, "Code.exe"), null);
        Assert.Null(history.GetPrograms(Max).Single(p => p.Program.Name == "Code.exe").ProjectId);
    }

    [Fact]
    public void DeletingAProjectUnlinksItsAppsAndRemovesItsRangesNotesAndLoggedTime()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        var clients = projects.AddCategory("Clients", 0xC0397A);
        var gone = projects.AddProject("Gone", clients);
        var kept = projects.AddProject("Kept", clients, color: 0x7D3C98);

        recording.InsertCapture(Nine, CaptureKind.Active, recording.GetOrAddApp("Code.exe", null), "t", null);
        history.SetProject(new ProgramKey(ProgramKind.App, "Code.exe"), gone);
        projects.SetSpan(Nine, Nine.AddMinutes(1), gone);
        projects.SetSpan(Nine.AddMinutes(1), Nine.AddMinutes(2), kept);
        projects.AddNote(gone, Nine, "note");
        projects.SetLogged(Day, gone, TimeSpan.FromHours(1));

        projects.DeleteProject(gone);
        Assert.Null(history.LoadDay(Day).Captures[0].LinkedProjectId);
        Assert.Equal([kept], projects.GetSpans(Nine.AddHours(-1), Nine.AddHours(1)).Select(s => s.ProjectId));
        Assert.Empty(projects.GetNotes(Nine.AddHours(-1), Nine.AddHours(1)));
        Assert.Empty(projects.GetLogged(Day, Day.AddDays(1)));

        // A project takes its category's colour unless it has its own; deleting the category keeps the project.
        var categories = projects.GetCategories().ToDictionary(c => c.Id);
        var other = projects.AddProject("Other", clients);
        Assert.Equal(0xC0397A, ProjectStore.ColorOf(projects.GetProjects().Single(p => p.Id == other), categories));
        Assert.Equal(0x7D3C98, ProjectStore.ColorOf(projects.GetProjects().Single(p => p.Id == kept), categories));
        projects.DeleteCategory(clients);
        var orphan = projects.GetProjects().Single(p => p.Id == other);
        Assert.Null(orphan.CategoryId);
        Assert.Equal(ProjectStore.DefaultColor, ProjectStore.ColorOf(orphan, projects.GetCategories().ToDictionary(c => c.Id)));

        projects.UpdateProject(orphan with { Name = "Renamed", Archived = true });
        Assert.DoesNotContain(projects.GetProjects(includeArchived: false), p => p.Id == other);
        Assert.Equal("Renamed", projects.GetProjects().Single(p => p.Id == other).Name);
    }

    [Fact]
    public void NotesGoOnTheBlockAroundThemOrTheNearestOfTheirProject()
    {
        ProjectBlock Block(int from, int to, long project) =>
            new(Nine.AddSeconds(from), Nine.AddSeconds(to), project, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        ProjectNote Note(long id, int at, long project) => new(id, project, Nine.AddSeconds(at), $"n{id}");

        var placed = NotePlacement.Place(
            [Note(1, 5, 1), Note(2, 32, 1), Note(3, 25, 1), Note(4, 35, 2), Note(5, 0, 3)],
            [Block(0, 10, 1), Block(10, 20, 2), Block(30, 40, 1)]);

        Assert.Equal(["n1"], placed.PerBlock[0].Select(n => n.Text));
        Assert.Equal(["n4"], placed.PerBlock[1].Select(n => n.Text));      // its project's only block
        Assert.Equal(["n3", "n2"], placed.PerBlock[2].Select(n => n.Text)); // n3 is nearer this one than the first
        Assert.Equal(["n5"], placed.Orphans.Select(n => n.Text));
    }

    [Fact]
    public void TimesheetHasALinePerProjectPerDayAndExportsDecimalHours()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        var clients = projects.AddCategory("Clients", 0xC0397A);
        var worklapse = projects.AddProject("Worklapse", null);
        var client = projects.AddProject("Northwind, Inc.", clients);
        var admin = projects.AddProject("Admin", null);

        // 45 minutes in Code (one capture a minute), then a 30-minute call set by hand while nothing was recorded.
        var code = recording.GetOrAddApp("Code.exe", null);
        history.SetProject(new ProgramKey(ProgramKind.App, "Code.exe"), worklapse);
        for (var minute = 0; minute < 45; minute++)
            recording.InsertCapture(Nine.AddMinutes(minute), CaptureKind.Active, code, "t", null);
        var interval = TimeSpan.FromMinutes(1);
        projects.SetSpan(Nine.AddHours(1), Nine.AddHours(1.5), client);

        var time = ProjectTimeline.Build(history.LoadSamples(Day, Day.AddDays(1)), projects.GetSpans(Nine.AddHours(-1), Nine.AddHours(2)),
            Nine.AddHours(-1), Nine.AddHours(2), interval);
        projects.AddNote(worklapse, time.Blocks[0].Middle, "Panning");
        projects.AddNote(worklapse, time.Blocks[0].Middle.AddSeconds(1), "Replug fix");
        projects.AddNote(client, time.Blocks[1].Middle, "Call about the \"header\"");
        projects.SetLogged(Day, worklapse, TimeSpan.FromMinutes(45));
        projects.SetLogged(Day, admin, TimeSpan.FromMinutes(15)); // logged with no time marked
        projects.SetLogged(Day.AddDays(1), admin, TimeSpan.FromMinutes(5));

        var lines = Timesheet.Build(history, projects, Day, Day.AddDays(1), interval);
        Assert.Equal([worklapse, client, admin], lines.Select(l => l.ProjectId));
        Assert.Equal(new TimesheetLine(Day, worklapse, lines[0].Notes, TimeSpan.FromMinutes(45), TimeSpan.Zero, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(45)), lines[0]);
        Assert.Equal("Panning; Replug fix", lines[0].NotesText);
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(30), (TimeSpan?)null), (lines[1].Active, lines[1].Assigned, lines[1].Logged));
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromMinutes(15)), (lines[2].Assigned, lines[2].Logged!.Value));

        var csv = new StringWriter();
        TimesheetCsv.Write(csv, lines, projects.GetProjects().ToDictionary(p => p.Id), projects.GetCategories().ToDictionary(c => c.Id));
        var date = Day.ToString("yyyy-MM-dd");
        Assert.Equal(
            "Date,Category,Project,Notes,Active,Productive,Assigned,Logged\n" +
            $"{date},,Worklapse,Panning; Replug fix,0.75,0.00,0.75,0.75\n" +
            $"{date},Clients,\"Northwind, Inc.\",\"Call about the \"\"header\"\"\",0.00,0.00,0.50,\n" +
            $"{date},,Admin,,0.00,0.00,0.00,0.25\n",
            csv.ToString().ReplaceLineEndings("\n"));

        projects.SetLogged(Day, worklapse, null);
        Assert.Null(Timesheet.Build(history, projects, Day, Day.AddDays(1), interval)[0].Logged);
    }

    [Fact]
    public void SamplesCarryWhatTotalsNeedAsAFullDayWouldForAWholeRange()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        var worklapse = projects.AddProject("Worklapse", null);
        var client = projects.AddProject("Northwind", null);
        var code = recording.GetOrAddApp("Code.exe", null);
        var firefox = recording.GetOrAddApp("firefox.exe", null);
        var github = recording.GetOrAddSite("github.com");
        var northwind = recording.GetOrAddSite("northwind.example");
        history.SetProject(new ProgramKey(ProgramKind.App, "firefox.exe"), worklapse);
        history.SetProject(new ProgramKey(ProgramKind.Site, "northwind.example"), client);
        history.SetProductive(new ProgramKey(ProgramKind.App, "firefox.exe"), true);
        history.SetProductive(new ProgramKey(ProgramKind.App, "Code.exe"), true);

        for (var day = 0; day < 3; day++)
        {
            var at = Nine.AddDays(day);
            recording.InsertCapture(at, CaptureKind.Active, code, "t", null);
            recording.InsertCapture(at.AddSeconds(10), CaptureKind.Active, firefox, "t", null, github);    // site not productive, browser's link
            recording.InsertCapture(at.AddSeconds(20), CaptureKind.Active, firefox, "t", null, northwind); // the site's link
            recording.InsertCapture(at.AddSeconds(30), CaptureKind.Active, firefox, "t", null);            // no site: the browser decides
            recording.InsertCapture(at.AddSeconds(40), CaptureKind.Idle, null, null, null);
        }
        history.SetPendingDelete(Nine.AddSeconds(30), Nine.AddSeconds(31), true);

        var expected = new[] { Day, Day.AddDays(1) }
            .SelectMany(d => history.LoadDay(d).Captures.Select(ActivitySample.Of)).ToList();
        Assert.Equal(expected, history.LoadSamples(Day, Day.AddDays(2)));
        Assert.Equal(
            [(true, (long?)null), (false, worklapse), (false, client), (true, worklapse), (false, null)],
            expected.Take(5).Select(s => (s.Productive, s.LinkedProjectId)));
        Assert.True(expected[3].PendingDelete);
    }

    [Fact]
    public void DaySummariesWorkOutEachDayOnItsOwn()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        var worklapse = projects.AddProject("Worklapse", null);
        var client = projects.AddProject("Northwind", null);
        var code = recording.GetOrAddApp("Code.exe", null);
        var other = recording.GetOrAddApp("game.exe", null);
        history.SetProject(new ProgramKey(ProgramKind.App, "Code.exe"), worklapse);
        history.SetProductive(new ProgramKey(ProgramKind.App, "Code.exe"), true);

        // Day 1: Code for a minute, then a game, idle, and a capture marked for deletion; one more just before midnight.
        for (var s = 0; s < 60; s += 10)
            recording.InsertCapture(Nine.AddSeconds(s), CaptureKind.Active, code, "t", null);
        recording.InsertCapture(Nine.AddSeconds(60), CaptureKind.Active, other, "t", null);
        recording.InsertCapture(Nine.AddSeconds(70), CaptureKind.Idle, null, null, null);
        recording.InsertCapture(Nine.AddSeconds(80), CaptureKind.Active, code, "t", null);
        history.SetPendingDelete(Nine.AddSeconds(80), Nine.AddSeconds(81), true);
        var (_, midnight) = HistoryStore.DayRange(Day);
        recording.InsertCapture(midnight.AddSeconds(-5), CaptureKind.Active, code, "t", null);
        projects.AddNote(worklapse, Nine.AddSeconds(30), "Panning");

        // Day 2: nothing recorded, only a call set by hand in the evening, with a note. Day 3: nothing at all.
        var (dayTwo, _) = HistoryStore.DayRange(Day.AddDays(1));
        projects.SetSpan(dayTwo.AddHours(18), dayTwo.AddHours(18.5), client);
        projects.AddNote(client, dayTwo.AddHours(18.25), "Call");

        var days = DaySummaries.Build(history, projects, Day, Day.AddDays(3), Max, withActivity: true);
        Assert.Equal([Day, Day.AddDays(1), Day.AddDays(2)], days.Select(d => d.Day));

        // The same totals as Day review works out from the full day.
        var (start, end) = HistoryStore.DayRange(Day);
        var full = ProjectTimeline.Build(history.LoadDay(Day).Captures, projects.GetSpans(start, end), start, end, Max);
        var one = days[0];
        Assert.Equal(full.Blocks, one.Time.Blocks);
        Assert.Equal(full.Unassigned, one.Time.Unassigned);
        Assert.Equal(TimeSpan.FromSeconds(80 + 5), one.Time.Recorded);             // the marked capture left out; the last one ends at midnight
        Assert.Equal((Nine, midnight), one.Extent);
        Assert.Equal(
            [(0, 60, ActivityKind.Productive), (60, 70, ActivityKind.Other), (70, 80, ActivityKind.Idle)],
            one.Activity.Take(3).Select(r => ((int)(r.Start - Nine).TotalSeconds, (int)(r.End - Nine).TotalSeconds, r.Kind)));
        Assert.Equal(["Panning"], one.PlacedNotes.PerBlock[0].Select(n => n.Text));

        var two = days[1];
        Assert.Equal((dayTwo.AddHours(18), dayTwo.AddHours(18.5)), two.Extent);
        Assert.Equal(TimeSpan.FromMinutes(30), two.Time.Assigned);
        Assert.Equal(TimeSpan.Zero, two.Time.Recorded);
        Assert.Empty(two.Activity);
        Assert.Equal(["Call"], two.PlacedNotes.PerBlock.Single().Select(n => n.Text));

        Assert.Null(days[2].Extent);
        Assert.Empty(days[2].Time.Blocks);
        Assert.Empty(DaySummaries.Build(history, projects, Day, Day.AddDays(1), Max)[0].Activity); // only when asked for

        // Captures marked for deletion don't stretch the day: with the last one marked it ends where the idle capture does.
        history.SetPendingDelete(midnight.AddSeconds(-5), midnight, true);
        Assert.Equal((Nine, Nine.AddSeconds(80)), DaySummaries.Build(history, projects, Day, Day.AddDays(1), Max)[0].Extent);
    }

    [Fact]
    public void DayNotesAreSetPerDayAndClearedWhenEmpty()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var projects = new ProjectStore(db);

        projects.SetDayNote(Day, "Kickoff");
        projects.SetDayNote(Day.AddDays(2), "Release");
        projects.SetDayNote(Day, "Kickoff call");
        Assert.Equal("Kickoff call", projects.GetDayNote(Day));
        Assert.Equal([Day], projects.GetDayNotes(Day, Day.AddDays(2)).Keys);

        projects.SetDayNote(Day, "  ");
        Assert.Equal("", projects.GetDayNote(Day));

        var note = projects.AddNote(projects.AddProject("P", null), Nine, "text");
        projects.UpdateNote(note, "changed");
        Assert.Equal("changed", projects.GetNotes(Nine, Nine.AddSeconds(1)).Single().Text);
        projects.UpdateNote(note, "");
        Assert.Empty(projects.GetNotes(Nine, Nine.AddSeconds(1)));
    }
}
