using Worklapse.Core.History;
using Worklapse.Core.Platform;

namespace Worklapse.Core.Export;

/// <summary>What goes into a timelapse, and how it looks.</summary>
/// <param name="Ranges">The time to include (the whole day or the selection, or a project's blocks in it), in time order.</param>
/// <param name="Monitor">The monitor to show (as chosen in Day review), or null for all of them.</param>
/// <param name="FocusOnApplication">Show only the window that had focus, as Day review's Focus on Active App.</param>
/// <param name="IncludeIdle">Keep idle captures (the screen as last captured); otherwise only active ones.</param>
/// <param name="Box">The largest video size; the picture is fitted in it.</param>
/// <param name="Clock">Draw each capture's time of day in a corner.</param>
public sealed record TimelapseOptions(
    IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Ranges,
    MonitorRecord? Monitor,
    bool FocusOnApplication,
    bool IncludeIdle,
    int FramesPerSecond,
    (int Width, int Height) Box,
    bool Clock);

/// <summary>One frame of a timelapse: a capture, and what it shows (as in Day review).</summary>
public sealed record TimelapseFrame(long CaptureId, DateTimeOffset Time, IReadOnlyList<PictureTile> Tiles, PixelRect? Focus)
{
    /// <summary>The desktop area shown, before the pictures are read (a TimeSnapper window's own size isn't known yet).</summary>
    public PixelRect Content => CapturePictures.Content(Tiles.Select(t => t.Monitor.Bounds), Focus);
}

public static class TimelapsePlan
{
    /// <summary>
    /// The frames of a timelapse of <paramref name="day"/>: every capture in the ranges, except those marked for
    /// deletion, idle ones unless included, and those with nothing recorded to show.
    /// </summary>
    public static List<TimelapseFrame> Frames(DayHistory day, TimelapseOptions options)
    {
        var monitorsById = day.Monitors.ToDictionary(m => m.Id);
        var frames = new List<TimelapseFrame>();
        var captures = day.Captures;
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            if (capture.PendingDelete || (!capture.IsActive && !options.IncludeIdle) || !InRanges(capture.Time, options.Ranges))
                continue;
            if (FrameAt(day, monitorsById, i, options.Monitor, options.FocusOnApplication) is { } frame)
                frames.Add(frame);
        }
        return frames;
    }

    /// <summary>
    /// What capture <paramref name="index"/> of <paramref name="day"/> shows, as in Day review; null when it has
    /// nothing recorded to show or <paramref name="monitor"/> wasn't attached then.
    /// </summary>
    public static TimelapseFrame? FrameAt(DayHistory day, int index, MonitorRecord? monitor, bool focusOnApplication) =>
        FrameAt(day, day.Monitors.ToDictionary(m => m.Id), index, monitor, focusOnApplication);

    private static TimelapseFrame? FrameAt(DayHistory day, Dictionary<long, MonitorRecord> monitorsById, int index,
        MonitorRecord? monitor, bool focusOnApplication)
    {
        var captures = day.Captures;
        var capture = captures[index];

        // Only the monitors attached then: those of other times can be in the same place.
        var attached = capture.MonitorIds.Select(id => monitorsById[id]).ToList();
        var monitors = monitor is not { } chosen ? attached
            : attached.Where(m => m.Id == chosen.Id).ToList() is { Count: > 0 } same ? same
            : attached.Where(m => m.Bounds == chosen.Bounds).Take(1).ToList();
        if (monitors.Count == 0)
            return null; // the chosen monitor wasn't attached

        var tiles = CapturePictures.Tiles(captures, index, monitors);
        if (tiles.All(t => t.Frame is null))
            return null;
        var focus = focusOnApplication ? CapturePictures.Focus(captures, index, tiles) : null;
        // Focused on a window, monitors with nothing recorded are left out: the window's part on them is empty.
        if (focus is not null)
            tiles = tiles.Where(t => t.Frame is not null).ToList();
        return new TimelapseFrame(capture.Id, capture.Time, tiles, focus);
    }

    /// <summary>
    /// The video's size: the largest area any frame shows, scaled to fit <paramref name="box"/>. Each frame is then
    /// fitted into it, so pictures of other shapes get bars. Even sizes, which every player handles.
    /// </summary>
    public static (int Width, int Height) VideoSize(IReadOnlyList<TimelapseFrame> frames, (int Width, int Height) box)
    {
        int width = 0, height = 0;
        foreach (var frame in frames)
        {
            var content = frame.Content;
            width = Math.Max(width, content.Width);
            height = Math.Max(height, content.Height);
        }
        if (width == 0 || height == 0)
            return box;
        var scale = Math.Min((double)box.Width / width, (double)box.Height / height);
        return (Even(width * scale), Even(height * scale));

        static int Even(double size) => Math.Max(2, (int)Math.Round(size / 2) * 2);
    }

    private static bool InRanges(DateTimeOffset time, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges)
    {
        foreach (var (start, end) in ranges)
            if (time >= start && time < end)
                return true;
        return false;
    }
}
