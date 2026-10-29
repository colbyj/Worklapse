using Worklapse.Core.History;

namespace Worklapse.Core.Projects;

/// <summary>One day worked out per project, for views that show many days (the Timesheet, the Overview).</summary>
/// <param name="Extent">
/// From the first moment recorded or set to a project by hand to the last (captures marked for deletion left out);
/// null for an empty day.
/// </param>
/// <param name="Activity">The recorded time as runs of on-task, off-task and idle time; empty unless asked for.</param>
/// <param name="Notes">The day's notes on projects.</param>
public sealed record DaySummary(
    DateOnly Day,
    (DateTimeOffset First, DateTimeOffset Last)? Extent,
    ProjectTime Time,
    IReadOnlyList<ActivityRun> Activity,
    IReadOnlyList<ProjectNote> Notes);

public static class DaySummaries
{
    /// <summary>
    /// Works out each day from <paramref name="from"/> up to (not including) <paramref name="to"/>, reading the
    /// history once for the whole range (a month of 8-hour days at 10 s is ~86,000 captures; see
    /// <see cref="HistoryStore.LoadSamples"/>).
    /// Each day is bounded as Day review bounds it: a capture lasts until the next one of the same day, at most
    /// <paramref name="maxCaptureLength"/>.
    /// </summary>
    /// <param name="withActivity">Also work out <see cref="DaySummary.Activity"/> (for timelines).</param>
    public static List<DaySummary> Build(HistoryStore history, ProjectStore projects, DateOnly from, DateOnly to,
        TimeSpan maxCaptureLength, bool withActivity = false)
    {
        var samples = history.LoadSamples(from, to);
        var (rangeStart, _) = history.DayRange(from);
        var (rangeEnd, _) = history.DayRange(to);
        var spans = projects.GetSpans(rangeStart, rangeEnd);
        var notes = projects.GetNotes(from, to);

        var days = new List<DaySummary>();
        var next = 0;
        for (var day = from; day < to; day = day.AddDays(1))
        {
            var (start, end) = history.DayRange(day);
            var first = next;
            while (next < samples.Count && samples[next].Time < end)
                next++;
            var captures = samples.GetRange(first, next - first);
            var daySpans = spans.Where(s => s.Start < end && s.End > start).ToList();
            var dayNotes = notes.Where(n => n.Day == day).ToList();

            var time = ProjectTimeline.Build(captures, daySpans, start, end, maxCaptureLength);
            days.Add(new DaySummary(day, ExtentOf(captures, time.Blocks, end, maxCaptureLength), time,
                withActivity ? ProjectTimeline.Activity(captures, start, end, maxCaptureLength) : [],
                dayNotes));
        }
        return days;
    }

    private static (DateTimeOffset, DateTimeOffset)? ExtentOf(IReadOnlyList<ActivitySample> captures, IReadOnlyList<ProjectBlock> blocks,
        DateTimeOffset dayEnd, TimeSpan maxCaptureLength)
    {
        DateTimeOffset? first = null, last = null;
        var firstKept = Enumerable.Range(0, captures.Count).FirstOrDefault(i => !captures[i].PendingDelete, -1);
        if (firstKept >= 0)
        {
            var lastKept = Enumerable.Range(0, captures.Count).Last(i => !captures[i].PendingDelete);
            first = captures[firstKept].Time;
            // The last capture kept lasts until the next one (marked or not), at most the maximum.
            var end = captures[lastKept].Time + maxCaptureLength;
            if (lastKept + 1 < captures.Count && captures[lastKept + 1].Time < end)
                end = captures[lastKept + 1].Time;
            last = end < dayEnd ? end : dayEnd;
        }
        if (blocks.Count > 0)
        {
            if (first is null || blocks[0].Start < first) first = blocks[0].Start;
            if (last is null || blocks[^1].End > last) last = blocks[^1].End;
        }
        return first is { } f && last is { } l ? (f, l) : null;
    }
}
