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

/// <summary>
/// The day's activity as coloured runs, with hour labels, a selection, marked-for-deletion ranges and the
/// playhead. Click to move the playhead; drag to select.
/// </summary>
public sealed class TimelineControl : Control
{
    private const double TrackHeight = 36;
    private const double LabelHeight = 16;
    private const double DragThreshold = 3;

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
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    private Point? _pressAt;
    private bool _dragging;

    public TimelineControl()
    {
        Height = TrackHeight + LabelHeight + 2;
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
    }

    public IReadOnlyList<TimelineSpan> Spans { get; private set; } = [];
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
        var track = new Rect(0.5, 0.5, Math.Max(0, width - 1), TrackHeight);
        context.FillRectangle(TrackBrush, track);

        // Hour grid and labels.
        var firstHour = (int)Math.Ceiling(ViewStart / 3600);
        var pixelsPerHour = width * 3600 / (ViewEnd - ViewStart);
        var labelEvery = pixelsPerHour < 34 ? 2 : 1;
        for (var hour = firstHour; hour * 3600 <= ViewEnd; hour++)
        {
            var x = X(hour * 3600.0);
            context.DrawLine(HourLine, new Point(x, track.Top), new Point(x, track.Bottom));
            if (hour % labelEvery != 0)
                continue;
            var label = new FormattedText(HourLabel(hour), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, LabelBrush);
            var lx = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, width - label.Width));
            context.DrawText(label, new Point(lx, TrackHeight + 2));
        }

        // Activity: active time fills most of the track, idle a thin band near the bottom.
        foreach (var span in Spans)
        {
            var x0 = X(span.Start);
            var x1 = Math.Max(X(span.End), x0 + 1);
            var rect = span.Kind == TimelineKind.Idle
                ? new Rect(x0, track.Bottom - 12, x1 - x0, 7)
                : new Rect(x0, track.Top + 4, x1 - x0, TrackHeight - 9);
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
            context.FillRectangle(SelectionFill, new Rect(x0, track.Top, x1 - x0, TrackHeight));
            context.DrawLine(SelectionEdge, new Point(x0, track.Top), new Point(x0, track.Bottom));
            context.DrawLine(SelectionEdge, new Point(x1, track.Top), new Point(x1, track.Bottom));
        }

        if (Playhead is { } playhead)
        {
            var x = X(playhead);
            context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, TrackHeight + 1));
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

    private double X(double seconds) => (seconds - ViewStart) / (ViewEnd - ViewStart) * Bounds.Width;

    private double Time(double x) => ViewStart + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * (ViewEnd - ViewStart);

    /// <summary>"9 AM" or "09:00", following the user's clock format.</summary>
    private static string HourLabel(int hour)
    {
        var time = DateTime.Today.AddHours(hour % 24);
        var culture = CultureInfo.CurrentCulture;
        return culture.DateTimeFormat.ShortTimePattern.Contains('H') ? time.ToString("HH:mm", culture) : time.ToString("h tt", culture);
    }
}
