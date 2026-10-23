using CommunityToolkit.Mvvm.ComponentModel;
using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;

namespace Worklapse.App.ViewModels;

/// <summary>Where the time shown on the timeline as reached comes from; a menu beats the pointer, which beats the keyboard.</summary>
public enum ReachSource
{
    Menu,
    Pointer,
    Keyboard,
}

/// <summary>
/// The side panel: what it's about (the day or the selection), the frame under the playhead, rows picked to select
/// their time, what a row's menu acts on, and the time a control would change, shown on the timeline.
/// </summary>
public sealed partial class DayReviewViewModel
{
    private bool _panelOnSelection;
    private (DateTimeOffset From, DateTimeOffset To)? _pickAnchor;
    private (int First, int Last)? _frameRun;
    private readonly Dictionary<ReachSource, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>> _reach = [];

    // --- what the panel is about ---------------------------------------------------------------

    /// <summary>
    /// Segments and Applications list the day: the Day button of their Day / Selection pair. The other tabs (Suggestions,
    /// Time Log, Marked) are always about the whole day.
    /// </summary>
    public bool ShowsDay
    {
        get => !ShowsSelection;
        set
        {
            if (value)
                ShowSelectionPanel(false);
        }
    }

    /// <summary>Segments and Applications list the selection, while there is one: the Selection button.</summary>
    public bool ShowsSelection
    {
        get => _panelOnSelection && HasSelection;
        set
        {
            if (value && HasSelection)
                ShowSelectionPanel(true);
        }
    }

    /// <summary>The day's first capture to the end of its last, beside the Day button.</summary>
    [ObservableProperty] private string _daySpanText = "";

    private void ShowSelectionPanel(bool on)
    {
        if (_panelOnSelection == on)
            return;
        _panelOnSelection = on;
        OnPropertyChanged(nameof(ShowsDay));
        OnPropertyChanged(nameof(ShowsSelection));
    }

    /// <summary>The panel's width, dragged at its edge; remembered in the settings when the drag ends.</summary>
    [ObservableProperty] private double _panelWidth;

    public void ResizePanel(double width) =>
        PanelWidth = Math.Clamp(width, WorklapseSettings.MinReviewPanelWidth, WorklapseSettings.MaxReviewPanelWidth);

    public void SavePanelWidth() => _settings.Save(_settings.Current with { ReviewPanelWidth = PanelWidth });

    // --- the frame under the playhead ----------------------------------------------------------

    /// <summary>The segment the playhead is in, with its two marks; null with no captures.</summary>
    [ObservableProperty] private SegmentRow? _frameSegment;
    [ObservableProperty] private string _frameName = "";
    [ObservableProperty] private string _frameIn = "";
    [ObservableProperty] private string _frameRangeText = "";
    [ObservableProperty] private string _frameLengthText = "";

    /// <summary>Works out the playhead's segment, unless it's still in the one shown (playing moves it every tick).</summary>
    private void UpdateFrameSegment(bool force = false)
    {
        if (Current < 0 || Current >= Captures.Count)
        {
            _frameRun = null;
            FrameSegment = null;
            FrameName = FrameIn = FrameRangeText = FrameLengthText = "";
            return;
        }
        if (!force && _frameRun is { } shown && Current >= shown.First && Current <= shown.Last)
            return;

        var (first, last) = RunAround(Current);
        _frameRun = (first, last);
        var capture = Captures[first];
        var (from, to) = (capture.Time, Captures[last].Time + CaptureLength(last));
        FrameSegment = Segment(from, to, capture);
        FrameName = FrameSegment.Name;
        FrameIn = capture.IsActive && !capture.PendingDelete && capture.Site is not null && capture.AppName is { } app ? $"in {app}" : "";
        FrameRangeText = RangeText(from, to);
        FrameLengthText = FrameSegment.LengthText;
    }

    /// <summary>The captures in the same segment as one: the same app or site (or idle), each starting where the last ends.</summary>
    private (int First, int Last) RunAround(int index)
    {
        var (first, last) = (index, index);
        while (first > 0 && Joined(first - 1, first))
            first--;
        while (last + 1 < Captures.Count && Joined(last, last + 1))
            last++;
        return (first, last);

        bool Joined(int a, int b) => Captures[b].Time - Captures[a].Time <= _maxCaptureLength && SameRun(Captures[a], Captures[b]);
    }

    // --- the timeline --------------------------------------------------------------------------

    /// <summary>
    /// A click on the timeline: moves the playhead there, clearing a selection clicked outside of; with Shift, moves
    /// the selection's nearer edge there instead.
    /// </summary>
    public void Click(double seconds, bool shift)
    {
        if (_range is { } range)
        {
            var (start, end) = (SecondsOf(range.Start), SecondsOf(range.End));
            if (shift)
            {
                DragEdge(Math.Abs(seconds - start) <= Math.Abs(seconds - end) ? 0 : 1, seconds, finished: true);
                return;
            }
            if (seconds < start || seconds > end)
                ClearSelection();
        }
        Seek(seconds);
    }

    /// <summary>A selection's edge dragged (0 its start, 1 its end), snapped as a drag is; the other edge stays put.</summary>
    public void DragEdge(int edge, double seconds, bool finished)
    {
        if (_range is not { } range)
            return;
        Stop();
        var (start, end) = edge == 0 ? (SnapStart(seconds), range.End) : (range.Start, SnapEnd(seconds));
        if (end <= start)
            return;
        SetRange((start, end));
        if (CaptureAtSeconds(seconds) is { } at)
            MoveTo(at);
    }

    /// <summary>A double click: selects the project block there, or on the activity lane the segment there (or the gap).</summary>
    public void DoubleClick(double seconds, bool projectLane)
    {
        (DateTimeOffset Start, DateTimeOffset End)? range = null;
        if (projectLane)
        {
            var at = TimeAt(seconds);
            if (_projectTime.Blocks.FirstOrDefault(b => b.Start <= at && at < b.End) is { } block)
                range = (block.Start, block.End);
        }
        else if (CaptureAtSeconds(seconds) is { } index)
        {
            var (first, last) = RunAround(index);
            range = (Captures[first].Time, Captures[last].Time + CaptureLength(last));
        }
        else if (Captures.Count > 0)
        {
            // Nothing recorded there: the gap between the captures around it.
            var at = TimeAt(seconds);
            var before = Enumerable.Range(0, Captures.Count).LastOrDefault(i => Captures[i].Time < at, -1);
            var after = before + 1;
            if (before >= 0 && after < Captures.Count)
                range = (Captures[before].Time + CaptureLength(before), Captures[after].Time);
        }
        if (range is not { } r || r.End <= r.Start)
            return;
        _anchor = null;
        SelectRange(r.Start, r.End);
        ShowSelectionPanel(true);
    }

    // --- rows picked in the day's lists --------------------------------------------------------

    /// <summary>
    /// A row clicked: selects its time; with Shift or Ctrl, from the row picked before to this one, and everything in
    /// between; a picked row at either end of the selection is let go instead (so Shift held can take rows off again).
    /// The panel stays on the day.
    /// </summary>
    public void Pick(DateTimeOffset from, DateTimeOffset to, bool extend)
    {
        if (extend && _pickAnchor is { } picked && _range is { } range)
        {
            var inside = from >= range.Start && to <= range.End;
            // The selection moved on since that row was picked (its ends typed or dragged): its far end anchors instead.
            (DateTimeOffset From, DateTimeOffset To) anchor = picked.From >= range.Start && picked.To <= range.End ? picked
                : to > range.End ? (range.Start, range.Start) : (range.End, range.End);
            if (inside && (from == range.Start || to == range.End))
            {
                if (from == range.Start && to == range.End)
                {
                    ClearSelection();
                    return;
                }
                var (start, end) = from == range.Start ? (to, range.End) : (range.Start, from);
                // Letting go of the row picked first: the row now at that end takes its place.
                if (anchor == (from, to))
                    _pickAnchor = PickedRowAt(from == range.Start ? start : end, atStart: from == range.Start);
                SelectRange(start, end);
                return;
            }
            SelectRange(anchor.From < from ? anchor.From : from, anchor.To > to ? anchor.To : to);
            return;
        }
        _pickAnchor = (from, to);
        SelectRange(from, to);
    }

    /// <summary>The segment or marked range starting (or ending) at a moment, as picked rows are kept; null if none does.</summary>
    private (DateTimeOffset From, DateTimeOffset To)? PickedRowAt(DateTimeOffset at, bool atStart)
    {
        foreach (var row in DaySegments)
            if (atStart ? row.From == at : row.To == at)
                return (row.From, row.To);
        foreach (var marked in MarkedRanges)
            if (atStart ? marked.Start == at : marked.End == at)
                return (marked.Start, marked.End);
        return null;
    }

    /// <summary>What a row's menu acts on: the selection, when the row is inside it; otherwise the row's own time.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) MenuTarget(SegmentRow row) =>
        _range is { } range && row.From >= range.Start && row.To <= range.End ? range : (row.From, row.To);

    public bool CanExtendSelectionTo(SegmentRow row) => _range is { } range && (row.From < range.Start || row.To > range.End);

    public void ExtendSelectionTo(SegmentRow row)
    {
        if (_range is { } range)
            SelectRange(row.From < range.Start ? row.From : range.Start, row.To > range.End ? row.To : range.End);
    }

    public bool CanStartSelectionAt(SegmentRow row) => _range is { } range && row.From < range.End;

    public void StartSelectionAt(SegmentRow row)
    {
        if (_range is { } range && row.From < range.End)
            SelectRange(row.From, range.End);
    }

    public bool CanEndSelectionAt(SegmentRow row) => _range is { } range && row.To > range.Start;

    public void EndSelectionAt(SegmentRow row)
    {
        if (_range is { } range && row.To > range.Start)
            SelectRange(range.Start, row.To);
    }

    /// <summary>Sets a stretch of time to a project by hand, or to no project (null).</summary>
    public void SetProject((DateTimeOffset Start, DateTimeOffset End) range, long? projectId) => SetProject([range], projectId);

    /// <summary>Sets stretches of time (an app's, say) to a project by hand, or to no project (null).</summary>
    public void SetProject(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges, long? projectId)
    {
        if (ranges.Count == 0)
            return;
        Edit($"{(projectId is { } id ? $"Set Project: {NameOf(id)}" : "Clear Project")}, {RangeText(ranges.Min(r => r.Start), ranges.Max(r => r.End))}", () =>
        {
            foreach (var (start, end) in ranges)
                Projects.SetSpan(start, end, projectId);
            RebuildProjects();
        });
    }

    /// <summary>Sets what has no project in stretches of time to one, by hand: idle and unrecorded gaps too, so blocks join.</summary>
    public void SetUnassigned(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges, long projectId) =>
        Edit($"Set Unassigned to {NameOf(projectId)}", () => SetProject(UnassignedIn(ranges), projectId));

    public bool HasUnassigned(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges) => UnassignedIn(ranges).Count > 0;

    /// <summary>The projects with time in stretches of time (by hand, a link or a title rule), in the order they first show.</summary>
    public List<ProjectOption> ProjectsIn(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges) =>
        _projectTime.Blocks.Where(b => ranges.Any(r => b.Start < r.End && r.Start < b.End))
            .Select(b => b.ProjectId).Distinct().Select(id => OptionOf(id)).Where(o => o.Id is not null).ToList();

    /// <summary>
    /// Gives the time one project has in stretches of time to another, by hand, however it got the first (by hand, a link
    /// or a title rule); the rest of that time, other projects' or none, stays as it is.
    /// </summary>
    public void ReplaceProject(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges, long from, long to) =>
        Edit($"Replace {NameOf(from)} with {NameOf(to)}", () => SetProject(ranges.SelectMany(r => Clip(_projectTime.Blocks.Where(b => b.ProjectId == from).Select(b => (b.Start, b.End)), r)).ToList(), to));

    /// <summary>The parts of stretches of time with no project block over them.</summary>
    private List<(DateTimeOffset Start, DateTimeOffset End)> UnassignedIn(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges)
    {
        var gaps = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var (start, end) in ranges)
        {
            var from = start;
            foreach (var block in _projectTime.Blocks)
            {
                if (block.End <= from || block.Start >= end)
                    continue;
                if (block.Start > from)
                    gaps.Add((from, block.Start));
                from = block.End;
            }
            if (from < end)
                gaps.Add((from, end));
        }
        return gaps;
    }

    /// <summary>
    /// What an Applications row's menu acts on: all of the app's (or site's) time on the day; on the selection's list,
    /// the part of it inside the selection.
    /// </summary>
    public List<(DateTimeOffset Start, DateTimeOffset End)> AppTime(DayAppRow row)
    {
        var time = row.Program is { } program ? MarkReach.TimeOf(Captures, program, _maxCaptureLength) : [];
        return SelectionApps.Contains(row) && _range is { } range ? Clip(time, range) : time;
    }

    /// <summary>Moves the playhead to the first capture kept at or after a moment.</summary>
    public void GoTo(DateTimeOffset time)
    {
        Stop();
        var index = Enumerable.Range(0, Captures.Count).FirstOrDefault(i => Captures[i].Time >= time && !Captures[i].PendingDelete, -1);
        if (index >= 0)
            MoveTo(index);
    }

    // --- the time a control reaches ------------------------------------------------------------

    /// <summary>The time shown as reached on the timeline (seconds of the day), or null.</summary>
    public IReadOnlyList<(double Start, double End)>? ReachSeconds
    {
        get
        {
            foreach (var source in new[] { ReachSource.Menu, ReachSource.Pointer, ReachSource.Keyboard })
                if (_reach.TryGetValue(source, out var ranges))
                    return ranges.Select(r => (SecondsOf(r.Start), SecondsOf(r.End))).ToList();
            return null;
        }
    }

    /// <summary>Shows on the timeline what a row's control reaches (<paramref name="part"/>: "productive", "project", or the whole row).</summary>
    public void ShowReach(ReachSource source, object? row, string? part)
    {
        if (row is not null && ReachOf(row, part) is { } ranges)
            _reach[source] = ranges;
        else
            _reach.Remove(source);
        TimelineChanged?.Invoke();
    }

    /// <summary>Shows a stretch of time as reached (what a row's menu acts on), or stops showing it (null).</summary>
    public void ShowReach(ReachSource source, (DateTimeOffset Start, DateTimeOffset End)? range) =>
        ShowReach(source, range is { } r ? [r] : (IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>?)null);

    /// <summary>Shows stretches of time as reached, or stops showing them (null).</summary>
    public void ShowReach(ReachSource source, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? ranges)
    {
        if (ranges is not null)
            _reach[source] = ranges;
        else
            _reach.Remove(source);
        TimelineChanged?.Invoke();
    }

    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? ReachOf(object row, string? part) => (row, part) switch
    {
        (ProgramMarksRow { Program: { } program }, "productive") => MarkReach.Productive(Captures, program, _maxCaptureLength),
        (DayAppRow { Program: { } program }, "project") => MarkReach.Link(Captures, _handSpans, program, _maxCaptureLength),
        (SegmentRow segment, _) => [(segment.From, segment.To)],
        (TimeLogRow log, _) => BlocksOf(log.ProjectId, null),
        (SuggestionGroupRow group, _) => group.Stretches.Select(s => (s.Start, s.End)).ToList(),
        (SuggestionRow stretch, _) => [(stretch.Suggestion.Start, stretch.Suggestion.End)],
        (BreakRow breakRow, _) => [(breakRow.Break.Start, breakRow.Break.End)],
        (MarkedRange marked, _) => [(marked.Start, marked.End)],
        _ => null,
    };

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Clip(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        (DateTimeOffset Start, DateTimeOffset End) within) =>
        ranges.Select(r => (Start: r.Start > within.Start ? r.Start : within.Start, End: r.End < within.End ? r.End : within.End))
            .Where(r => r.End > r.Start)
            .ToList();

    /// <summary>A project's blocks (or, for null, the time with none), within a range if one is given.</summary>
    private List<(DateTimeOffset Start, DateTimeOffset End)> BlocksOf(long? projectId, (DateTimeOffset Start, DateTimeOffset End)? within)
    {
        var (start, end) = within ?? (_projectTime.Blocks.Count > 0 ? _projectTime.Blocks[0].Start : DateTimeOffset.MinValue,
            _projectTime.Blocks.Count > 0 ? _projectTime.Blocks[^1].End : DateTimeOffset.MinValue);
        var ranges = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        if (projectId is { } id)
        {
            foreach (var block in _projectTime.Blocks.Where(b => b.ProjectId == id))
            {
                var (from, to) = within is null ? (block.Start, block.End)
                    : (block.Start > start ? block.Start : start, block.End < end ? block.End : end);
                if (to > from)
                    ranges.Add((from, to));
            }
            return ranges;
        }
        // No project: what's between the blocks.
        var cursor = start;
        foreach (var block in _projectTime.Blocks)
        {
            if (block.End <= cursor || block.Start >= end)
                continue;
            if (block.Start > cursor)
                ranges.Add((cursor, block.Start));
            cursor = block.End;
        }
        if (cursor < end)
            ranges.Add((cursor, end));
        return ranges;
    }
}
