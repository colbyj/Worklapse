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
/// marked-for-deletion ranges and the playhead across both. Click to move the playhead; drag to select.
/// </summary>
public sealed class TimelineControl : Control
{
    private const double Gutter = 58;          // lane names on the left
    private const double ProjectHeight = 20;
    private const double LaneGap = 3;
    private const double TrackHeight = 30;     // activity
    private const double LabelHeight = 16;
    private const double DragThreshold = 3;
    private const double TrackTop = ProjectHeight + LaneGap;

    // The brushes are recoloured in place when the OS theme changes (see OnAttachedToVisualTree).
    private static readonly IBrush TrackBrush = AppBrushes.TimelineTrack;
    private static readonly IPen TrackBorder = new Pen(AppBrushes.TimelineFrame);
    private static readonly IPen HourLine = new Pen(AppBrushes.TimelineHourLine);
    private static readonly IBrush MarkedFill = AppBrushes.MarkedFill;
    private static readonly IPen MarkedBorder = new Pen(AppBrushes.Marked, 1, DashStyle.Dash);
    private static readonly IBrush SelectionFill = AppBrushes.SelectionFill;
    private static readonly IPen SelectionEdge = new Pen(AppBrushes.SelectionEdge, 2);
    private static readonly IPen PlayheadPen = new Pen(AppBrushes.Marked, 2);
    private static readonly IBrush LabelBrush = AppBrushes.TimelineLabel;
    private static readonly IBrush OnProjectBrush = Brushes.White;
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    private Point? _pressAt;
    private bool _dragging;

    public TimelineControl()
    {
        Height = TrackTop + TrackHeight + LabelHeight + 2;
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
    }

    public IReadOnlyList<TimelineSpan> Spans { get; private set; } = [];
    public IReadOnlyList<ProjectLaneBlock> Projects { get; private set; } = [];
    public double ViewStart { get; private set; } = 8 * 3600;
    public double ViewEnd { get; private set; } = 17 * 3600;
    public (double Start, double End)? Selection { get; private set; }
    public double? Playhead { get; private set; }

    /// <summary>A click (no drag) at this time.</summary>
    public event Action<double>? Clicked;

    /// <summary>A drag in progress or finished: anchor time, current pointer time, and whether it ended.</summary>
    public event Action<double, double, bool>? Dragged;

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.Changed -= InvalidateVisual;
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
            context.DrawText(label, new Point(lx, track.Bottom + 2));
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressAt = e.GetPosition(this);
        _dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressAt is not { } start)
            return;
        var position = e.GetPosition(this);
        if (!_dragging && Math.Abs(position.X - start.X) < DragThreshold)
            return;
        _dragging = true;
        Dragged?.Invoke(Time(start.X), Time(position.X), false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressAt is not { } start)
            return;
        var position = e.GetPosition(this);
        if (_dragging)
            Dragged?.Invoke(Time(start.X), Time(position.X), true);
        else
            Clicked?.Invoke(Time(start.X));
        _pressAt = null;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressAt = null;
        _dragging = false;
    }

    private double LaneWidth => Math.Max(1, Bounds.Width - Gutter - 1);

    private double X(double seconds) => Gutter + (seconds - ViewStart) / (ViewEnd - ViewStart) * LaneWidth;

    private double Time(double x) => ViewStart + Math.Clamp((x - Gutter) / LaneWidth, 0, 1) * (ViewEnd - ViewStart);

    /// <summary>"9 AM" or "09:00", following the user's clock format.</summary>
    private static string HourLabel(int hour)
    {
        var time = DateTime.Today.AddHours(hour % 24);
        var culture = CultureInfo.CurrentCulture;
        return culture.DateTimeFormat.ShortTimePattern.Contains('H') ? time.ToString("HH:mm", culture) : time.ToString("h tt", culture);
    }
}
