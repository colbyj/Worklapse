using Worklapse.Core.History;

namespace Worklapse.Core.Projects;

/// <summary>A stretch of time marked with one project, with nothing else in between.</summary>
/// <param name="Active">Time with input inside it (captures marked for deletion left out).</param>
/// <param name="Productive">The productive part of <paramref name="Active"/>.</param>
/// <param name="SetByHand">How much of it comes from ranges set by hand rather than links.</param>
public sealed record ProjectBlock(DateTimeOffset Start, DateTimeOffset End, long ProjectId, TimeSpan Active, TimeSpan Productive, TimeSpan SetByHand)
{
    /// <summary>Everything in the block, idle or not recorded included: the project's Assigned time.</summary>
    public TimeSpan Length => End - Start;
}

/// <param name="Assigned">All time marked with the project, idle or not recorded included.</param>
/// <param name="Active">Time with input.</param>
/// <param name="Productive">Active time in productive apps and sites.</param>
public sealed record ProjectTotals(TimeSpan Assigned, TimeSpan Active, TimeSpan Productive);

/// <summary>Recorded time marked with no project.</summary>
public sealed record UnassignedTotals(TimeSpan Productive, TimeSpan Other, TimeSpan Idle);

/// <summary>A span of time (usually a day) worked out per project.</summary>
/// <param name="Recorded">Time captured, active or idle (captures marked for deletion left out), project or not.</param>
public sealed record ProjectTime(IReadOnlyList<ProjectBlock> Blocks, IReadOnlyDictionary<long, ProjectTotals> Totals, UnassignedTotals Unassigned,
    TimeSpan Recorded)
{
    public static ProjectTime Empty { get; } = new([], new Dictionary<long, ProjectTotals>(),
        new UnassignedTotals(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero), TimeSpan.Zero);

    /// <summary>All time marked with a project.</summary>
    public TimeSpan Assigned => Blocks.Aggregate(TimeSpan.Zero, (sum, b) => sum + b.Length);

    /// <summary>All time with input, marked with a project or not.</summary>
    public TimeSpan Active => Totals.Values.Aggregate(Unassigned.Productive + Unassigned.Other, (sum, t) => sum + t.Active);
}

public enum ActivityKind
{
    Productive,
    Other,
    Idle,
}

/// <summary>A stretch of recorded time of one kind (captures marked for deletion left out).</summary>
public readonly record struct ActivityRun(DateTimeOffset Start, DateTimeOffset End, ActivityKind Kind);

/// <summary>
/// Works out which project each moment belongs to. A range set by hand decides inside it (its project, or none);
/// elsewhere active captures follow their links (<see cref="CaptureRecord.LinkedProjectId"/>), and idle or
/// unrecorded time belongs to no project. Captures marked for deletion count for nothing.
/// </summary>
public static class ProjectTimeline
{
    /// <param name="spans">The ranges set by hand, in time order, not overlapping.</param>
    /// <param name="maxCaptureLength">As in <see cref="SpanSummary.For"/>: each capture lasts until the next, at most this.</param>
    public static ProjectTime Build(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength) =>
        Build(captures.Select(ActivitySample.Of).ToArray(), spans, start, end, maxCaptureLength);

    /// <inheritdoc cref="Build(IReadOnlyList{CaptureRecord}, IReadOnlyList{ProjectSpan}, DateTimeOffset, DateTimeOffset, TimeSpan)"/>
    public static ProjectTime Build(IReadOnlyList<ActivitySample> captures, IReadOnlyList<ProjectSpan> spans,
        DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength)
    {
        var pieces = Pieces(captures, start, end, maxCaptureLength);
        var recordedTime = TimeSpan.Zero;
        foreach (var piece in pieces)
            if (piece.Kind != PieceKind.Marked)
                recordedTime += piece.To - piece.From;

        // Every place where something changes, then walk the gaps between them.
        var cuts = new List<DateTimeOffset>(pieces.Count * 2 + spans.Count * 2 + 2) { start, end };
        foreach (var piece in pieces)
        {
            cuts.Add(piece.From);
            cuts.Add(piece.To);
        }
        foreach (var span in spans)
        {
            if (span.Start > start && span.Start < end) cuts.Add(span.Start);
            if (span.End > start && span.End < end) cuts.Add(span.End);
        }
        cuts.Sort();

        var blocks = new List<ProjectBlock>();
        var (unassignedProductive, unassignedOther, unassignedIdle) = (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        var (p, s) = (0, 0);
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var (from, to) = (cuts[i], cuts[i + 1]);
            if (to <= from)
                continue;
            while (p < pieces.Count && pieces[p].To <= from) p++;
            while (s < spans.Count && spans[s].End <= from) s++;
            Piece? piece = p < pieces.Count && pieces[p].From <= from ? pieces[p] : null;
            ProjectSpan? span = s < spans.Count && spans[s].Start <= from ? spans[s] : null;

            var length = to - from;
            var active = piece is { Kind: PieceKind.Active } ? length : TimeSpan.Zero;
            var productive = piece is { Kind: PieceKind.Active, Productive: true } ? length : TimeSpan.Zero;
            var project = span is not null ? span.ProjectId : piece?.Link;

            if (project is { } id)
            {
                var byHand = span is not null ? length : TimeSpan.Zero;
                if (blocks.Count > 0 && blocks[^1] is var last && last.ProjectId == id && last.End == from)
                    blocks[^1] = last with { End = to, Active = last.Active + active, Productive = last.Productive + productive, SetByHand = last.SetByHand + byHand };
                else
                    blocks.Add(new ProjectBlock(from, to, id, active, productive, byHand));
            }
            else if (piece is { } recorded)
            {
                if (recorded.Kind == PieceKind.Active)
                {
                    if (recorded.Productive) unassignedProductive += length;
                    else unassignedOther += length;
                }
                else if (recorded.Kind == PieceKind.Idle)
                {
                    unassignedIdle += length;
                }
            }
        }

        var totals = new Dictionary<long, ProjectTotals>();
        foreach (var block in blocks)
        {
            var sum = totals.GetValueOrDefault(block.ProjectId) ?? new ProjectTotals(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
            totals[block.ProjectId] = new ProjectTotals(sum.Assigned + block.Length, sum.Active + block.Active, sum.Productive + block.Productive);
        }
        return new ProjectTime(blocks, totals, new UnassignedTotals(unassignedProductive, unassignedOther, unassignedIdle), recordedTime);
    }

    /// <summary>
    /// The recorded time in start..end as runs of productive, other and idle time, for an activity lane; time not
    /// recorded and captures marked for deletion are gaps.
    /// </summary>
    public static List<ActivityRun> Activity(IReadOnlyList<ActivitySample> captures, DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength)
    {
        var runs = new List<ActivityRun>();
        foreach (var piece in Pieces(captures, start, end, maxCaptureLength))
        {
            if (piece.Kind == PieceKind.Marked)
                continue;
            var kind = piece.Kind == PieceKind.Idle ? ActivityKind.Idle : piece.Productive ? ActivityKind.Productive : ActivityKind.Other;
            if (runs.Count > 0 && runs[^1] is var last && last.Kind == kind && last.End == piece.From)
                runs[^1] = last with { End = piece.To };
            else
                runs.Add(new ActivityRun(piece.From, piece.To, kind));
        }
        return runs;
    }

    private enum PieceKind { Active, Idle, Marked }

    /// <summary>The time one capture stands for, clipped to the span, and the project its links point to.</summary>
    private readonly record struct Piece(DateTimeOffset From, DateTimeOffset To, PieceKind Kind, bool Productive, long? Link);

    private static List<Piece> Pieces(IReadOnlyList<ActivitySample> captures, DateTimeOffset start, DateTimeOffset end, TimeSpan maxCaptureLength)
    {
        var pieces = new List<Piece>(captures.Count);
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            var from = capture.Time;
            var to = i + 1 < captures.Count ? captures[i + 1].Time : from + maxCaptureLength;
            if (to - from > maxCaptureLength)
                to = from + maxCaptureLength;
            if (from < start) from = start;
            if (to > end) to = end;
            if (to <= from)
                continue;

            var kind = capture.PendingDelete ? PieceKind.Marked : capture.IsActive ? PieceKind.Active : PieceKind.Idle;
            pieces.Add(new Piece(from, to, kind, capture.Productive, kind == PieceKind.Active ? capture.LinkedProjectId : null));
        }
        return pieces;
    }
}
