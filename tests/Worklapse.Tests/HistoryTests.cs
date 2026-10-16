using Worklapse.Core.History;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

public class HistoryTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));

    private static CaptureRecord Capture(int secondsAfterNine, CaptureKind kind = CaptureKind.Active, bool productive = false, bool marked = false) =>
        new(secondsAfterNine, Nine.AddSeconds(secondsAfterNine), kind, "app.exe", "Title", null, productive, marked, []);

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
        Assert.Equal(2, history.DeleteMarked(day.Day));

        var after = history.LoadDay(day.Day);
        Assert.Equal(["t0", "t1", "t4", "t5"], after.Captures.Select(c => c.WindowTitle));
        history.Dispose();
    }
}
