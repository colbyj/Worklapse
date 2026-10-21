using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.History;

/// <summary>A monitor's place in a capture's picture and the frame shown there (null: nothing recorded).</summary>
public readonly record struct PictureTile(MonitorRecord Monitor, FrameRef? Frame);

/// <summary>
/// What a capture shows, for Day review and exported timelapses alike: the frame of each monitor, and the window to
/// focus on.
/// </summary>
public static class CapturePictures
{
    /// <summary>
    /// The frame of each of <paramref name="monitors"/> at capture <paramref name="index"/>. Idle captures have no
    /// frames; they show the screen as last captured.
    /// </summary>
    public static List<PictureTile> Tiles(IReadOnlyList<CaptureRecord> captures, int index, IReadOnlyList<MonitorRecord> monitors)
    {
        var capture = captures[index];
        var tiles = new List<PictureTile>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var frame = FrameOf(capture, monitor.Id);
            if (frame is null && !capture.IsActive)
                frame = LastFrameBefore(captures, index, monitor.Id);
            tiles.Add(new PictureTile(monitor, frame));
        }
        return tiles;
    }

    /// <summary>
    /// The window to focus on at capture <paramref name="index"/>, where it was recorded; null when there is none or
    /// it lies on none of the recorded <paramref name="tiles"/>. Recording only the active application blacks out the
    /// rest of its monitor and leaves the others out, so this shows just what was recorded.
    /// </summary>
    public static PixelRect? Focus(IReadOnlyList<CaptureRecord> captures, int index, IReadOnlyList<PictureTile> tiles) =>
        WindowAt(captures, index) is { } window && tiles.Any(t => t.Frame is not null && !t.Monitor.Bounds.Intersect(window).IsEmpty)
            ? window
            : null;

    /// <summary>The desktop area shown: what <paramref name="tiles"/> cover, cut to <paramref name="focus"/> unless that misses them all.</summary>
    public static PixelRect Content(IEnumerable<PixelRect> tiles, PixelRect? focus)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var tile in tiles)
        {
            left = Math.Min(left, tile.X);
            top = Math.Min(top, tile.Y);
            right = Math.Max(right, tile.Right);
            bottom = Math.Max(bottom, tile.Bottom);
        }
        if (left == int.MaxValue)
            return default;
        var all = new PixelRect(left, top, right - left, bottom - top);
        return focus is { } area && !all.Intersect(area).IsEmpty ? all.Intersect(area) : all;
    }

    public static FrameRef? FrameOf(CaptureRecord capture, long monitorId)
    {
        foreach (var f in capture.Frames)
            if (f.MonitorId == monitorId)
                return f.Frame;
        return null;
    }

    /// <summary>
    /// The window that had focus at a capture. Idle captures show the screen as last captured, so for them, the
    /// window of the last active capture before.
    /// </summary>
    private static PixelRect? WindowAt(IReadOnlyList<CaptureRecord> captures, int index)
    {
        for (var i = index; i >= 0; i--)
            if (captures[i].IsActive)
                return captures[i].Window;
        return null;
    }

    private static FrameRef? LastFrameBefore(IReadOnlyList<CaptureRecord> captures, int index, long monitorId)
    {
        for (var i = index - 1; i >= 0; i--)
            if (FrameOf(captures[i], monitorId) is { } frame)
                return frame;
        return null;
    }
}
