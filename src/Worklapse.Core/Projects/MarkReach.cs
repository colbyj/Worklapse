using Worklapse.Core.History;

namespace Worklapse.Core.Projects;

/// <summary>
/// The time a mark on an app or site decides in a day's captures: what changing it would change, shown before it's
/// changed. Captures marked for deletion and idle time are never reached (they count for nothing, or for no app).
/// </summary>
public static class MarkReach
{
    /// <summary>
    /// Where the app's or site's Productive mark decides: a site's pages; an app's time on pages without a known site
    /// (a site always decides for its own pages, so a browser's mark doesn't reach them).
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Productive(IReadOnlyList<CaptureRecord> captures,
        ProgramKey program, TimeSpan maxCaptureLength) =>
        Ranges(captures, maxCaptureLength, c => program.Kind == ProgramKind.Site
            ? Same(c.Site, program.Name)
            : c.Site is null && Same(c.AppName, program.Name));

    /// <summary>
    /// Where the app's or site's project link decides: its time outside the ranges set by hand; for an app, also its
    /// pages whose site has no link of its own (they follow the browser's).
    /// </summary>
    /// <param name="spans">The ranges set by hand, in time order.</param>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Link(IReadOnlyList<CaptureRecord> captures,
        IReadOnlyList<ProjectSpan> spans, ProgramKey program, TimeSpan maxCaptureLength)
    {
        var linked = Ranges(captures, maxCaptureLength, c => program.Kind == ProgramKind.Site
            ? Same(c.Site, program.Name)
            : c.SiteProjectId is null && Same(c.AppName, program.Name));
        return Without(linked, spans);
    }

    /// <summary>
    /// All of an app's time (a browser's pages of every site included, as its Active time counts them) or a site's: what
    /// Day review's menu on an Applications row acts on.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> TimeOf(IReadOnlyList<CaptureRecord> captures,
        ProgramKey program, TimeSpan maxCaptureLength) =>
        Ranges(captures, maxCaptureLength, c => Same(program.Kind == ProgramKind.Site ? c.Site : c.AppName, program.Name));

    /// <summary>The time of the active captures that match, each lasting until the next (at most the maximum), joined up.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Ranges(IReadOnlyList<CaptureRecord> captures,
        TimeSpan maxCaptureLength, Func<CaptureRecord, bool> match)
    {
        var ranges = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            if (!capture.IsActive || capture.PendingDelete || !match(capture))
                continue;
            var from = capture.Time;
            var to = i + 1 < captures.Count && captures[i + 1].Time - from < maxCaptureLength ? captures[i + 1].Time : from + maxCaptureLength;
            if (ranges.Count > 0 && ranges[^1].End >= from)
                ranges[^1] = (ranges[^1].Start, to);
            else
                ranges.Add((from, to));
        }
        return ranges;
    }

    /// <summary>The ranges with the spans cut out of them.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Without(List<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        IReadOnlyList<ProjectSpan> spans)
    {
        var left = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var (start, end) in ranges)
        {
            var from = start;
            foreach (var span in spans)
            {
                if (span.End <= from || span.Start >= end)
                    continue;
                if (span.Start > from)
                    left.Add((from, span.Start));
                from = span.End;
            }
            if (from < end)
                left.Add((from, end));
        }
        return left;
    }

    private static bool Same(string? name, string other) => string.Equals(name, other, StringComparison.OrdinalIgnoreCase);
}
