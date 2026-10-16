using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.History;

public sealed record MonitorRecord(long Id, string DeviceName, PixelRect Bounds, bool IsPrimary);

public readonly record struct MonitorFrame(long MonitorId, FrameRef Frame);

/// <summary>One capture tick as recorded.</summary>
public sealed record CaptureRecord(
    long Id,
    DateTimeOffset Time,
    CaptureKind Kind,
    string? AppName,
    string? WindowTitle,
    PixelRect? Window,
    bool Productive,
    bool PendingDelete,
    IReadOnlyList<MonitorFrame> Frames)
{
    public bool IsActive => Kind == CaptureKind.Active;
}

/// <summary>Everything recorded on one local calendar day, in time order.</summary>
public sealed record DayHistory(DateOnly Day, IReadOnlyList<CaptureRecord> Captures, IReadOnlyList<MonitorRecord> Monitors)
{
    public bool IsEmpty => Captures.Count == 0;
}

/// <summary>
/// Totals for a span of time; durations come from the captures inside it. Idle is time recorded as idle
/// (no input); NotRecorded is time inside the span with no captures at all (Worklapse not running, paused,
/// or the screen locked).
/// </summary>
public sealed record SpanSummary(TimeSpan Length, TimeSpan Active, TimeSpan Productive, TimeSpan Other, TimeSpan Idle, TimeSpan Marked, TimeSpan NotRecorded)
{
    /// <summary>
    /// Sums a span. Each capture stands for the time until the next one, capped at
    /// <paramref name="maxCaptureLength"/> so a gap in recording isn't credited to the capture before it.
    /// </summary>
    public static SpanSummary For(IReadOnlyList<CaptureRecord> captures, DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength)
    {
        var active = TimeSpan.Zero;
        var productive = TimeSpan.Zero;
        var marked = TimeSpan.Zero;
        var idle = TimeSpan.Zero;
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            var from = capture.Time;
            var to = i + 1 < captures.Count ? captures[i + 1].Time : from + maxCaptureLength;
            if (to - from > maxCaptureLength)
                to = from + maxCaptureLength;

            // Clip to the span.
            if (from < start) from = start;
            if (to > end) to = end;
            if (to <= from)
                continue;

            var length = to - from;
            if (capture.PendingDelete)
                marked += length;
            else if (capture.IsActive)
            {
                active += length;
                if (capture.Productive)
                    productive += length;
            }
            else
            {
                idle += length;
            }
        }

        var total = end - start;
        var notRecorded = total - active - marked - idle;
        return new SpanSummary(total, active, productive, active - productive, idle, marked,
            notRecorded < TimeSpan.Zero ? TimeSpan.Zero : notRecorded);
    }
}
