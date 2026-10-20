using Worklapse.Core.History;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

public class HistoryTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));

    private static CaptureRecord Capture(int secondsAfterNine, CaptureKind kind = CaptureKind.Active, bool productive = false, bool marked = false,
        string app = "app.exe", string? site = null, bool? siteProductive = null) =>
        new(secondsAfterNine, Nine.AddSeconds(secondsAfterNine), kind, app, site, "Title", null, productive, siteProductive, marked, [], []);

    [Theory]
    // app productive, site (null = none), site productive → counted as productive
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, false, false)]  // a productive browser on a site that isn't
    [InlineData(false, true, true)]   // a site that is, in a browser that isn't
    [InlineData(true, true, true)]
    public void TheSiteDecidesWhenThereIsOneOtherwiseTheApp(bool appProductive, bool? siteProductive, bool expected)
    {
        var capture = Capture(0, productive: appProductive, app: "firefox.exe", site: siteProductive is null ? null : "github.com", siteProductive: siteProductive);
        Assert.Equal(expected, capture.Productive);
    }

    [Fact]
    public void ClassificationAppliesToAllHistoryAndSitesOverrideTheirBrowser()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var firefox = store.GetOrAddApp("firefox.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe");
        var rider = store.GetOrAddApp("rider64.exe", null);
        var github = store.GetOrAddSite("github.com");
        var youtube = store.GetOrAddSite("youtube.com");
        store.InsertCapture(Nine, CaptureKind.Active, firefox, "old capture, before sites were read", null);
        store.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, firefox, "Pull requests", null, github);
        store.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, firefox, "A video", null, youtube);
        store.InsertCapture(Nine.AddSeconds(30), CaptureKind.Active, rider, "Code", null);
        store.InsertCapture(Nine.AddSeconds(40), CaptureKind.Active, firefox, "private window", null);

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var day = DateOnly.FromDateTime(Nine.LocalDateTime);
        bool[] Productive() => history.LoadDay(day).Captures.Select(c => c.Productive).ToArray();
        Assert.Equal([false, false, false, false, false], Productive());

        history.SetProductive(new ProgramKey(ProgramKind.Site, "github.com"), true);
        history.SetProductive(new ProgramKey(ProgramKind.App, "RIDER64.EXE"), true); // app names ignore case
        Assert.Equal([false, true, false, true, false], Productive());

        // The browser covers only its captures without a known site; youtube.com stays Other.
        history.SetProductive(new ProgramKey(ProgramKind.App, "firefox.exe"), true);
        Assert.Equal([true, true, false, true, true], Productive());
        var loaded = history.LoadDay(day).Captures;
        Assert.Equal([null, "github.com", "youtube.com", null, null], loaded.Select(c => c.Site));

        // Another copy of a marked app (another path) is marked too.
        var portable = store.GetOrAddApp("firefox.exe", @"D:\Portable\firefox.exe");
        store.InsertCapture(Nine.AddSeconds(50), CaptureKind.Active, portable, "portable", null);
        Assert.True(history.LoadDay(day).Captures[^1].Productive);
        history.SetProductive(new ProgramKey(ProgramKind.App, "firefox.exe"), false);
        Assert.False(history.LoadDay(day).Captures[^1].Productive);
        Assert.False(history.LoadDay(day).Captures[0].Productive);
    }

    [Fact]
    public void ProgramsListEveryAppAndSiteWithItsActiveTime()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var firefox = store.GetOrAddApp("firefox.exe", null);
        var rider = store.GetOrAddApp("rider64.exe", null);
        var github = store.GetOrAddSite("github.com");
        // 09:00:00 firefox/github, :10 firefox/github, :20 rider, :30 idle, then a gap; 10:00 rider (last).
        store.InsertCapture(Nine, CaptureKind.Active, firefox, "t", null, github);
        store.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, firefox, "t", null, github);
        store.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, rider, "t", null);
        store.InsertCapture(Nine.AddSeconds(30), CaptureKind.Idle, null, null, null);
        store.InsertCapture(Nine.AddHours(1), CaptureKind.Active, rider, "t", null);

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        history.SetProductive(new ProgramKey(ProgramKind.Site, "github.com"), true);
        var programs = history.GetPrograms(Interval * 1.5).ToDictionary(p => p.Program);

        Assert.Equal(3, programs.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), programs[new ProgramKey(ProgramKind.App, "firefox.exe")].Recorded);
        Assert.Equal(TimeSpan.FromSeconds(20), programs[new ProgramKey(ProgramKind.Site, "github.com")].Recorded);
        Assert.Equal(TimeSpan.FromSeconds(25), programs[new ProgramKey(ProgramKind.App, "rider64.exe")].Recorded); // 10 + capped 15
        Assert.True(programs[new ProgramKey(ProgramKind.Site, "github.com")].Productive);
        Assert.False(programs[new ProgramKey(ProgramKind.App, "firefox.exe")].Productive);
    }

    [Fact]
    public void TimeInASelectionIsCountedPerAppAndPerSite()
    {
        var captures = new List<CaptureRecord>
        {
            Capture(0, app: "firefox.exe", site: "github.com", siteProductive: false),
            Capture(10, app: "firefox.exe", site: "youtube.com", siteProductive: false),
            Capture(20, app: "firefox.exe", site: "github.com", siteProductive: false),
            Capture(30, app: "rider64.exe"),
            Capture(40, app: "rider64.exe", marked: true),
            Capture(50, CaptureKind.Idle),
        };
        var times = SpanSummary.ByProgram(captures, Nine, Nine.AddSeconds(60), Interval * 1.5);
        Assert.Equal(
            [
                (new ProgramKey(ProgramKind.App, "firefox.exe"), TimeSpan.FromSeconds(30)),
                (new ProgramKey(ProgramKind.Site, "github.com"), TimeSpan.FromSeconds(20)),
                (new ProgramKey(ProgramKind.App, "rider64.exe"), TimeSpan.FromSeconds(10)),
                (new ProgramKey(ProgramKind.Site, "youtube.com"), TimeSpan.FromSeconds(10)),
            ],
            times);
    }

    [Fact]
    public void SitesLeftWithoutCapturesAreDeletedUnlessProductive()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var firefox = store.GetOrAddApp("firefox.exe", null);
        store.InsertCapture(Nine, CaptureKind.Active, firefox, "t", null, store.GetOrAddSite("youtube.com"));
        store.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, firefox, "t", null, store.GetOrAddSite("github.com"));
        store.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, firefox, "t", null, store.GetOrAddSite("stays.example"));

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        history.SetProductive(new ProgramKey(ProgramKind.Site, "github.com"), true);
        history.SetPendingDelete(Nine, Nine.AddSeconds(20), pending: true);
        history.DeleteMarked(DateOnly.FromDateTime(Nine.LocalDateTime));

        Assert.Equal(["github.com", "stays.example"], history.GetPrograms(Interval).Where(p => p.Program.Kind == ProgramKind.Site).Select(p => p.Program.Name).Order());
    }

    [Fact]
    public void UpgradingAnExistingDatabaseAddsSitesAndKeepsEverything()
    {
        using var folder = new TempFolder();
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            // Back to schema 4, with captures and a productive app recorded under it.
            db.Execute("""
                DROP TABLE suggestion_parts; DROP TABLE suggestions; DROP TABLE screen_words;
                ALTER TABLE apps DROP COLUMN read_screen; ALTER TABLE apps DROP COLUMN learn;
                DROP TABLE timesnapper_imports; DROP TABLE title_rules; DROP TABLE timesheet; DROP TABLE day_notes; DROP TABLE project_notes; DROP TABLE project_spans;
                ALTER TABLE apps DROP COLUMN project_id; ALTER TABLE sites DROP COLUMN project_id; DROP TABLE projects; DROP TABLE categories;
                ALTER TABLE apps DROP COLUMN excluded; DROP INDEX ix_segments_path; ALTER TABLE segments DROP COLUMN format;
                ALTER TABLE captures DROP COLUMN layout_id; DROP TABLE layouts;
                DROP INDEX ix_captures_site; ALTER TABLE captures DROP COLUMN site_id; DROP TABLE sites; PRAGMA user_version = 4;
                """);
            var (nine, offset) = (Nine.ToUnixTimeMilliseconds(), (int)Nine.Offset.TotalMinutes);
            db.Execute($"""
                INSERT INTO apps (id, exe_name, productive) VALUES (1, 'rider64.exe', 1), (2, 'firefox.exe', 0);
                INSERT INTO captures (ts_ms, utc_offset_min, kind, app_id, window_title)
                VALUES ({nine}, {offset}, 0, 1, 'Code'), ({nine + 10_000}, {offset}, 0, 2, 'Firefox');
                """);
        }

        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            using (var version = db.Command("PRAGMA user_version;"))
                Assert.Equal(13L, version.ExecuteScalar());
            var history = new HistoryStore(db);
            var captures = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime)).Captures;
            Assert.Equal([true, false], captures.Select(c => c.Productive));
            Assert.All(captures, c => Assert.Null(c.Site));
            Assert.All(history.GetPrograms(Interval), p => Assert.False(p.Excluded));

            // New captures can have a site.
            var store = new RecordingStore(db);
            store.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, store.GetOrAddApp("firefox.exe", null), "x", null, store.GetOrAddSite("github.com"));
            Assert.Equal("github.com", history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime)).Captures[^1].Site);
        }
    }

    [Fact]
    public void SummaryCountsEachCaptureUntilTheNextAndSeparatesGaps()
    {
        // 09:00:00–09:00:30 productive (3 captures), 09:00:30–09:00:50 other (2), then idle at 09:00:50,
        // then nothing until 09:10:00 (not recorded), one more active capture.
        var captures = new List<CaptureRecord>
        {
            Capture(0, productive: true), Capture(10, productive: true), Capture(20, productive: true),
            Capture(30), Capture(40),
            Capture(50, CaptureKind.Idle),
            Capture(600),
        };

        var summary = SpanSummary.For(captures, Nine, Nine.AddSeconds(610), Interval * 1.5);

        Assert.Equal(TimeSpan.FromSeconds(610), summary.Length);
        Assert.Equal(TimeSpan.FromSeconds(60), summary.Active);       // 50 s + the last capture's 10 s
        Assert.Equal(TimeSpan.FromSeconds(30), summary.Productive);
        Assert.Equal(TimeSpan.FromSeconds(30), summary.Other);
        Assert.Equal(TimeSpan.FromSeconds(15), summary.Idle);         // capped: the gap after it isn't idle
        Assert.Equal(TimeSpan.FromSeconds(535), summary.NotRecorded);
    }

    [Fact]
    public void MarkedCapturesAreLeftOutOfActiveTime()
    {
        var captures = new List<CaptureRecord> { Capture(0), Capture(10, marked: true), Capture(20, marked: true), Capture(30) };
        var summary = SpanSummary.For(captures, Nine, Nine.AddSeconds(40), Interval * 1.5);
        Assert.Equal(TimeSpan.FromSeconds(20), summary.Active);
        Assert.Equal(TimeSpan.FromSeconds(20), summary.Marked);
    }

    [Fact]
    public void SummaryIsClippedToTheSpan()
    {
        var captures = new List<CaptureRecord> { Capture(0), Capture(10), Capture(20) };
        var summary = SpanSummary.For(captures, Nine.AddSeconds(5), Nine.AddSeconds(25), Interval * 1.5);
        Assert.Equal(TimeSpan.FromSeconds(20), summary.Active);
    }

    [Fact]
    public void DaysLoadWithFramesAndMarkingCanBeUndoneOrConfirmed()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var app = store.GetOrAddApp("rider64.exe", null);
        var monitor = store.GetOrAddMonitor(new MonitorInfo("M", new PixelRect(0, 0, 100, 100), true));
        var segment = store.InsertSegment(monitor, "video/x.ivf", 100, 100, 40, Nine);
        for (var i = 0; i < 6; i++)
        {
            var capture = store.InsertCapture(Nine.AddSeconds(10 * i), CaptureKind.Active, app, $"t{i}", null);
            store.InsertSegmentFrame(segment, i, 0, 1, i == 0);
            store.InsertFrame(capture, monitor, new FrameRef(segment, i));
        }

        var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        Assert.Contains(DateOnly.FromDateTime(Nine.LocalDateTime), history.GetDays());
        var day = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime));
        Assert.Equal(6, day.Captures.Count);
        Assert.All(day.Captures, c => Assert.Single(c.Frames));
        Assert.Single(day.Monitors);

        // Mark 09:00:20–09:00:40 (captures 2 and 3), undo, mark again, confirm.
        Assert.Equal(2, history.SetPendingDelete(Nine.AddSeconds(20), Nine.AddSeconds(40), pending: true));
        Assert.Equal(2, history.LoadDay(day.Day).Captures.Count(c => c.PendingDelete));
        history.SetPendingDelete(Nine.AddSeconds(20), Nine.AddSeconds(40), pending: false);
        Assert.DoesNotContain(history.LoadDay(day.Day).Captures, c => c.PendingDelete);
        history.SetPendingDelete(Nine.AddSeconds(20), Nine.AddSeconds(40), pending: true);
        Assert.Equal(new DeleteResult(2, StagedSegmentsAffected: false), history.DeleteMarked(day.Day));

        var after = history.LoadDay(day.Day);
        Assert.Equal(["t0", "t1", "t4", "t5"], after.Captures.Select(c => c.WindowTitle));
        history.Dispose();
    }
}
