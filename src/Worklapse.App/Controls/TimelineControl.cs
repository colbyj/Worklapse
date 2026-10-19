using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Worklapse.App.Theming;

namespace Worklapse.App.Controls;

public enum TimelineKind
{
    Productive,
    Other,
    Idle,
}

/// <summary>A run of time drawn in one style. Times are seconds since the day's local midnight.</summary>
public readonly record struct TimelineSpan(double Start, double End, TimelineKind Kind, bool Marked);

/// <summary>A block of time marked with a project, in its colour. Times as in <see cref="TimelineSpan"/>.</summary>
public readonly record struct ProjectLaneBlock(double Start, double End, IBrush Brush, string Label);

/// <summary>
/// The day as two lanes: projects above, activity (coloured runs) below, with hour labels, a selection,
/// marked-for-deletion ranges and the playhead across both, and the time a control in the side panel reaches. Click
/// to move the playhead (the view model clears a selection clicked outside); drag to select; drag a selection's edge
/// to move it; Shift+click to move its nearer edge; double-click to select a block or a run.
/// </summary>
public sealed class TimelineControl : ThemedControl
{
    private const double Gutter = 58;          // lane names on the left
    private const double ProjectHeight = 20;
    private const double LaneGap = 3;
    private const double TrackHeight = 30;     // activity
    private const double ReachGap = 2;         // between the activity and the bars under the time reached
    private const double ReachHeight = 3;
    private const double LabelHeight = 16;
    private const double DragThreshold = 3;
    private const double EdgeGrab = 5;         // how near a selection's edge a press takes the edge
    private const double TrackTop = ProjectHeight + LaneGap;

    // The brushes are recoloured in place when the OS theme changes (see ThemedControl).
    private static readonly IBrush TrackBrush = AppBrushes.TimelineTrack;
    private static readonly IPen TrackBorder = new Pen(AppBrushes.TimelineFrame);
    private static readonly IPen HourLine = new Pen(AppBrushes.TimelineHourLine);
    private static readonly IBrush MarkedFill = AppBrushes.MarkedFill;
    private static readonly IPen MarkedBorder = new Pen(AppBrushes.Marked, 1, DashStyle.Dash);
    private static readonly IBrush SelectionFill = AppBrushes.SelectionFill;
    private static readonly IPen SelectionEdge = new Pen(AppBrushes.SelectionEdge, 2);
    private static readonly IPen PlayheadPen = new Pen(AppBrushes.Marked, 2);
    private static readonly IBrush ReachVeil = AppBrushes.ReachVeil;
    private static readonly IBrush ReachBar = AppBrushes.ReachBar;
    private static readonly Cursor SelectCursor = new(StandardCursorType.Cross);
    private static readonly Cursor EdgeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly IBrush LabelBrush = AppBrushes.TimelineLabel;
    private static readonly IBrush OnProjectBrush = Brushes.White;
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    private Point? _pressAt;
    private bool _dragging;
    private int? _edge;                        // the selection's edge being dragged: 0 its start, 1 its end
    private bool _doubleClicked;

    public TimelineControl()
    {
        Height = TrackTop + TrackHeight + ReachGap + ReachHeight + LabelHeight + 2;
        Cursor = SelectCursor;
        ClipToBounds = true;
    }

    public IReadOnlyList<TimelineSpan> Spans { get; private set; } = [];
    public IReadOnlyList<ProjectLaneBlock> Projects { get; private set; } = [];
    public double ViewStart { get; private set; } = 8 * 3600;
    public double ViewEnd { get; private set; } = 17 * 3600;
    public (double Start, double End)? Selection { get; private set; }
    public double? Playhead { get; private set; }

    /// <summary>The time a control in the side panel reaches, while it's pointed at; null when none is.</summary>
    public IReadOnlyList<(double Start, double End)>? Reach { get; private set; }

    /// <summary>A click (no drag) at this time, and whether Shift was held.</summary>
    public event Action<double, bool>? Clicked;

    /// <summary>A drag in progress or finished: anchor time, current pointer time, and whether it ended.</summary>
    public event Action<double, double, bool>? Dragged;

    /// <summary>A selection's edge dragged: which (0 its start, 1 its end), the time it's at, and whether it ended.</summary>
    public event Action<int, double, bool>? EdgeDragged;

    /// <summary>A double click at this time, and whether it was on the projects lane.</summary>
    public event Action<double, bool>? DoubleClicked;

    public void SetData(IReadOnlyList<TimelineSpan> spans, double viewStart, double viewEnd)
    {
        Spans = spans;
        ViewStart = viewStart;
        ViewEnd = Math.Max(viewEnd, viewStart + 60);
        InvalidateVisual();
    }

    public void SetProjects(IReadOnlyList<ProjectLaneBlock> projects)
    {
        Projects = projects;
        InvalidateVisual();
    }

    public void SetMarkers((double Start, double End)? selection, double? playhead)
    {
        Selection = selection;
        Playhead = playhead;
        InvalidateVisual();
    }

    public void SetReach(IReadOnlyList<(double Start, double End)>? reach)
    {
        Reach = reach;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var laneWidth = Math.Max(0, width - Gutter - 1);
        var lane = new Rect(Gutter + 0.5, 0.5, laneWidth, ProjectHeight);
        var track = new Rect(Gutter + 0.5, TrackTop + 0.5, laneWidth, TrackHeight);
        context.FillRectangle(TrackBrush, lane);
        context.FillRectangle(TrackBrush, track);
        DrawLaneName("Projects", lane);
        DrawLaneName("Activity", track);

        // Hour grid and labels.
        var firstHour = (int)Math.Ceiling(ViewStart / 3600);
        var pixelsPerHour = laneWidth * 3600 / (ViewEnd - ViewStart);
        var labelEvery = pixelsPerHour < 34 ? 2 : 1;
        for (var hour = firstHour; hour * 3600 <= ViewEnd; hour++)
        {
            var x = X(hour * 3600.0);
            context.DrawLine(HourLine, new Point(x, lane.Top), new Point(x, lane.Bottom));
            context.DrawLine(HourLine, new Point(x, track.Top), new Point(x, track.Bottom));
            if (hour % labelEvery != 0)
                continue;
            var label = new FormattedText(HourLabel(hour), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush);
            var lx = Math.Clamp(x - label.Width / 2, Gutter, Math.Max(Gutter, width - label.Width));
            context.DrawText(label, new Point(lx, track.Bottom + ReachGap + ReachHeight + 2));
        }

        // Projects: each block in its colour, named when there's room.
        foreach (var block in Projects)
        {
            var x0 = X(block.Start);
            var x1 = Math.Max(X(block.End), x0 + 1);
            var rect = new Rect(x0, lane.Top + 2, x1 - x0, ProjectHeight - 3);
            context.FillRectangle(block.Brush, rect, 2);
            var name = new FormattedText(block.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, OnProjectBrush);
            if (name.Width + 8 <= rect.Width)
                using (context.PushClip(rect))
                    context.DrawText(name, new Point(rect.Left + 4, rect.Top + (rect.Height - name.Height) / 2));
        }
        context.DrawRectangle(null, TrackBorder, lane);

        // Activity: active time fills most of the track, idle a thin band near the bottom.
        foreach (var span in Spans)
        {
            var x0 = X(span.Start);
            var x1 = Math.Max(X(span.End), x0 + 1);
            var rect = span.Kind == TimelineKind.Idle
                ? new Rect(x0, track.Bottom - 11, x1 - x0, 6)
                : new Rect(x0, track.Top + 4, x1 - x0, TrackHeight - 8);
            var brush = span.Kind switch
            {
                TimelineKind.Productive => AppBrushes.Productive,
                TimelineKind.Other => AppBrushes.Other,
                _ => AppBrushes.Idle,
            };
            using (context.PushOpacity(span.Marked ? 0.3 : 1))
                context.FillRectangle(brush, rect);
        }

        // Marked ranges, merged across kinds.
        double? markStart = null, markEnd = null;
        foreach (var span in Spans.Append(default))
        {
            if (span.Marked && markEnd is { } e && span.Start - e < 1)
            {
                markEnd = span.End;
                continue;
            }
            if (markStart is { } s && markEnd is { } end)
                context.DrawRectangle(MarkedFill, MarkedBorder, new Rect(X(s), track.Top + 1, Math.Max(2, X(end) - X(s)), TrackHeight - 2));
            markStart = span.Marked ? span.Start : null;
            markEnd = span.Marked ? span.End : null;
        }

        context.DrawRectangle(null, TrackBorder, track);

        // What a control in the side panel reaches: everything else veiled, a bar under each stretch it reaches.
        if (Reach is { } reach)
        {
            var from = ViewStart;
            foreach (var (start, end) in reach.Where(r => r.End > ViewStart && r.Start < ViewEnd).OrderBy(r => r.Start))
            {
                if (start > from)
                    context.FillRectangle(ReachVeil, new Rect(X(from), lane.Top, X(start) - X(from), track.Bottom - lane.Top));
                var x0 = X(Math.Max(start, ViewStart));
                context.FillRectangle(ReachBar, new Rect(x0, track.Bottom + ReachGap, Math.Max(2, X(Math.Min(end, ViewEnd)) - x0), ReachHeight), 1);
                from = Math.Max(from, end);
            }
            if (from < ViewEnd)
                context.FillRectangle(ReachVeil, new Rect(X(from), lane.Top, X(ViewEnd) - X(from), track.Bottom - lane.Top));
        }

        if (Selection is { } selection)
        {
            var x0 = X(selection.Start);
            var x1 = Math.Max(X(selection.End), x0 + 2);
            context.FillRectangle(SelectionFill, new Rect(x0, lane.Top, x1 - x0, track.Bottom - lane.Top));
            context.DrawLine(SelectionEdge, new Point(x0, lane.Top), new Point(x0, track.Bottom));
            context.DrawLine(SelectionEdge, new Point(x1, lane.Top), new Point(x1, track.Bottom));
        }

        if (Playhead is { } playhead)
        {
            var x = X(playhead);
            context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, track.Bottom + 1));
        }

        void DrawLaneName(string text, Rect beside)
        {
            var name = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush);
            context.DrawText(name, new Point(0, beside.Top + (beside.Height - name.Height) / 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            // The release that ends a double click isn't another click.
            _doubleClicked = true;
            DoubleClicked?.Invoke(Time(point.Position.X), point.Position.Y < ProjectHeight + LaneGap / 2);
            return;
        }
        _pressAt = point.Position;
        _dragging = false;
        _edge = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? null : EdgeAt(point.Position.X);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_pressAt is not { } start)
        {
            Cursor = EdgeAt(position.X) is not null ? EdgeCursor : SelectCursor;
            return;
        }
        if (!_dragging && Math.Abs(position.X - start.X) < DragThreshold)
            return;
        _dragging = true;
        if (_edge is { } edge)
            EdgeDragged?.Invoke(edge, Time(position.X), false);
        else
            Dragged?.Invoke(Time(start.X), Time(position.X), false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_doubleClicked)
        {
            _doubleClicked = false;
            return;
        }
        if (_pressAt is not { } start)
            return;
        var position = e.GetPosition(this);
        if (!_dragging)
            Clicked?.Invoke(Time(start.X), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        else if (_edge is { } edge)
            EdgeDragged?.Invoke(edge, Time(position.X), true);
        else
            Dragged?.Invoke(Time(start.X), Time(position.X), true);
        _pressAt = null;
        _dragging = false;
        _edge = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressAt = null;
        _dragging = false;
        _edge = null;
    }

    /// <summary>The selection's edge near this position, if one is: 0 its start, 1 its end (the nearer, when both are).</summary>
    private int? EdgeAt(double x)
    {
        if (Selection is not { } selection)
            return null;
        var (start, end) = (Math.Abs(x - X(selection.Start)), Math.Abs(x - X(selection.End)));
        if (Math.Min(start, end) > EdgeGrab)
            return null;
        return start <= end ? 0 : 1;
    }

    private double LaneWidth => Math.Max(1, Bounds.Width - Gutter - 1);

    private double X(double seconds) => Gutter + (seconds - ViewStart) / (ViewEnd - ViewStart) * LaneWidth;

    private double Time(double x) => ViewStart + Math.Clamp((x - Gutter) / LaneWidth, 0, 1) * (ViewEnd - ViewStart);

    /// <summary>"9 AM" or "09:00", following the user's clock format.</summary>
    internal static string HourLabel(int hour)
    {
        var time = DateTime.Today.AddHours(hour % 24);
        var culture = CultureInfo.CurrentCulture;
        return culture.DateTimeFormat.ShortTimePattern.Contains('H') ? time.ToString("HH:mm", culture) : time.ToString("h tt", culture);
    }
}
