using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.History;

public sealed record MonitorRecord(long Id, string DeviceName, PixelRect Bounds, bool IsPrimary);

public readonly record struct MonitorFrame(long MonitorId, FrameRef Frame);

/// <summary>One capture tick as recorded, with how its app and site are classified now.</summary>
/// <param name="Site">The website a browser showed (domain only); null for other apps and older captures.</param>
/// <param name="SiteProductive">The site's classification; null when there is no site.</param>
/// <param name="MonitorIds">
/// The monitors attached at the time, captured or not (ascending ids, see <see cref="DayHistory.Monitors"/>): as
/// recorded, or for captures recorded before that, as <see cref="MonitorLayouts"/> works it out.
/// </param>
public sealed record CaptureRecord(
    long Id,
    DateTimeOffset Time,
    CaptureKind Kind,
    string? AppName,
    string? Site,
    string? WindowTitle,
    PixelRect? Window,
    bool AppProductive,
    bool? SiteProductive,
    bool PendingDelete,
    IReadOnlyList<MonitorFrame> Frames,
    IReadOnlyList<long> MonitorIds)
{
    public bool IsActive => Kind == CaptureKind.Active;

    /// <summary>
    /// The more specific classification decides: the site's when there is one, otherwise the app's. So a
    /// browser marked productive counts only for pages without a known site.
    /// </summary>
    public bool Productive => SiteProductive ?? AppProductive;
}

public enum ProgramKind
{
    App,
    Site,
}

/// <summary>An app (by exe name) or a website (by domain), as classified productive or not.</summary>
public readonly record struct ProgramKey(ProgramKind Kind, string Name);

/// <summary>An app or site with its classification and how long it was recorded.</summary>
/// <param name="Excluded">Nothing is recorded while it has the focus.</param>
public sealed record ProgramUsage(ProgramKey Program, bool Productive, bool Excluded, TimeSpan Recorded);

/// <summary>Everything recorded on one local calendar day, in time order.</summary>
/// <param name="Monitors">
/// Every monitor attached at some point that day. Not all at once: plugging monitors in or out records them as
/// new ones, which can occupy the same place; each capture's <see cref="CaptureRecord.MonitorIds"/> says which.
/// </param>
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
        foreach (var (capture, length) in Lengths(captures, start, end, maxCaptureLength))
        {
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

    /// <summary>
    /// Active time (not marked for deletion) per app and per site within the span, longest first. A browser
    /// capture with a site counts for both its app and its site.
    /// </summary>
    public static List<(ProgramKey Program, TimeSpan Time)> ByProgram(IReadOnlyList<CaptureRecord> captures,
        DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength)
    {
        var times = new Dictionary<ProgramKey, TimeSpan>();
        foreach (var (capture, length) in Lengths(captures, start, end, maxCaptureLength))
        {
            if (!capture.IsActive || capture.PendingDelete)
                continue;
            if (capture.AppName is { } app)
                Add(new ProgramKey(ProgramKind.App, app));
            if (capture.Site is { } site)
                Add(new ProgramKey(ProgramKind.Site, site));

            void Add(ProgramKey key) => times[key] = times.GetValueOrDefault(key) + length;
        }
        return times.Select(t => (t.Key, t.Value)).OrderByDescending(t => t.Value).ThenBy(t => t.Key.Name).ToList();
    }

    /// <summary>
    /// Each capture stands for the time until the next one, capped at <paramref name="maxCaptureLength"/>,
    /// clipped to the span; captures with nothing inside the span are left out.
    /// </summary>
    private static IEnumerable<(CaptureRecord Capture, TimeSpan Length)> Lengths(IReadOnlyList<CaptureRecord> captures,
        DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength)
    {
        for (var i = 0; i < captures.Count; i++)
        {
            var from = captures[i].Time;
            var to = i + 1 < captures.Count ? captures[i + 1].Time : from + maxCaptureLength;
            if (to - from > maxCaptureLength)
                to = from + maxCaptureLength;

            if (from < start) from = start;
            if (to > end) to = end;
            if (to > from)
                yield return (captures[i], to - from);
        }
    }
}
