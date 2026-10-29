using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Worklapse.App.Theming;

namespace Worklapse.App.Controls;

public enum TimelineKind
{
    OnTask,
    OffTask,
    Idle,
}

/// <summary>A run of time drawn in one style. Times are seconds since the day's local midnight.</summary>
public readonly record struct TimelineSpan(double Start, double End, TimelineKind Kind, bool Marked);

/// <summary>How a block on the projects lane is drawn.</summary>
public enum LaneStyle
{
    /// <summary>Time marked with the project: filled in its colour.</summary>
    Filled,

    /// <summary>Time a suggestion would give the project: outlined in its colour, faintly tinted.</summary>
    Suggested,

    /// <summary>A suggestion the classifier is unsure of: outlined in its colour and hatched.</summary>
    Unsure,

    /// <summary>A break between project time, not decided yet: a dashed outline with a question mark.</summary>
    Break,
}

/// <summary>A block of time marked with a project, in its colour. Times as in <see cref="TimelineSpan"/>.</summary>
public readonly record struct ProjectLaneBlock(double Start, double End, IBrush Brush, string Label, LaneStyle Style = LaneStyle.Filled);

/// <summary>
/// The day as two lanes: projects above (suggestions outlined), activity (coloured runs) below, with hour labels, a selection,
/// marked-for-deletion ranges and the playhead across both, and the time a control in the side panel reaches. Click
/// to move the playhead (the view model clears a selection clicked outside); drag to select; drag a selection's edge
/// to move it; Shift+click to move its nearer edge; double-click to select a block or a run. Scroll to zoom in at the
/// pointer; Shift+scroll (or scrolling sideways) moves along the day while zoomed in, and so does dragging past either
/// end. The playhead is kept in view when it moves.
/// </summary>
public sealed class TimelineControl : ThemedControl
{
    public const double Gutter = 58;           // lane names on the left
    private const double ProjectHeight = 20;
    private const double LaneGap = 3;
    private const double TrackHeight = 30;     // activity
    private const double ReachGap = 2;         // between the activity and the bars under the time reached
    private const double ReachHeight = 3;
    private const double LabelHeight = 16;
    private const double DragThreshold = 3;
    private const double EdgeGrab = 5;         // how near a selection's edge a press takes the edge
    private const double TrackTop = ProjectHeight + LaneGap;
    private const double MinShown = 5 * 60;    // zoomed in furthest: five minutes across
    private const double ZoomStep = 1.25;      // per notch of the wheel
    private const double PanStep = 0.1;        // of the time shown, per notch of the wheel
    private const double FollowMargin = 0.05;  // of the time shown, between a playhead brought into view and the end
    private const double LabelSpacing = 10;    // between time labels
    private const double DragScrollReach = 100;   // how far past an end a drag scrolls fastest
    private const double DragScrollStep = 0.05;   // of the time shown, per tick at the fastest

    // Time between labelled grid lines: the shortest that leaves the labels room.
    private static readonly int[] TickSteps = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600];

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
    public const double SuggestedTint = 0.14;       // how strongly a suggestion's colour fills it
    private const double SuggestedOutline = 1.5;
    private const double HatchSpacing = 5;          // between an unsure suggestion's hatch lines
    private const double HatchOpacity = 0.45;
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    private Point? _pressAt;
    private double _pressTime;                 // the time pressed at, kept while a drag scrolls the timeline
    private double _pointerX;                  // where the pointer is while dragging
    private bool _dragging;
    private int? _edge;                        // the selection's edge being dragged: 0 its start, 1 its end
    private bool _doubleClicked;
    private (double Start, double End)? _zoom; // the time shown while zoomed in; null shows all of it
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };

    public TimelineControl()
    {
        Height = TrackTop + TrackHeight + ReachGap + ReachHeight + LabelHeight + 2;
        Cursor = SelectCursor;
        ClipToBounds = true;
        _dragScroll.Tick += (_, _) => ScrollWhileDragging();
    }

    public IReadOnlyList<TimelineSpan> Spans { get; private set; } = [];
    public IReadOnlyList<ProjectLaneBlock> Projects { get; private set; } = [];

    /// <summary>The time the timeline covers: all of it shows unless zoomed in.</summary>
    public double ViewStart { get; private set; } = 8 * 3600;
    public double ViewEnd { get; private set; } = 17 * 3600;

    /// <summary>The time shown: all the timeline covers, or less while zoomed in.</summary>
    public double ShownStart => _zoom?.Start ?? ViewStart;
    public double ShownEnd => _zoom?.End ?? ViewEnd;

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

    /// <summary>The time shown changed: zoomed, scrolled, or moved by a change of the time covered.</summary>
    public event Action? ShownChanged;

    /// <summary>Where the last right-click was: its time, and whether on the projects lane (for the menu that opens).</summary>
    public (double Seconds, bool ProjectLane)? ContextPoint { get; private set; }

    /// <summary>The time covered and the activity in it. A zoom is kept, moved inside the new time if it has to be.</summary>
    public void SetData(IReadOnlyList<TimelineSpan> spans, double viewStart, double viewEnd)
    {
        Spans = spans;
        ViewStart = viewStart;
        ViewEnd = Math.Max(viewEnd, viewStart + 60);
        Show(_zoom);
        InvalidateVisual();
    }

    public void SetProjects(IReadOnlyList<ProjectLaneBlock> projects)
    {
        Projects = projects;
        InvalidateVisual();
    }

    /// <summary>
    /// The selection and the playhead. A playhead moved out of view (playing, stepping, Go To) is brought back in, near
    /// the end it left by, or to the middle when it went far; not during a press here, whose drag scrolls by itself.
    /// </summary>
    public void SetMarkers((double Start, double End)? selection, double? playhead)
    {
        var moved = playhead != Playhead;
        Selection = selection;
        Playhead = playhead;
        InvalidateVisual();
        if (!moved || _pressAt is not null || playhead is not { } at || _zoom is not { } zoom || (at >= zoom.Start && at <= zoom.End))
            return;
        var length = zoom.End - zoom.Start;
        var start = at < zoom.Start - length / 2 || at > zoom.End + length / 2 ? at - length / 2
            : at < zoom.Start ? at - length * FollowMargin
            : at + length * FollowMargin - length;
        Show((start, start + length));
    }

    public void SetReach(IReadOnlyList<(double Start, double End)>? reach)
    {
        Reach = reach;
        InvalidateVisual();
    }

    /// <summary>Zooms all the way out.</summary>
    public void ShowAll() => Show(null);

    /// <summary>While zoomed in, shows the same length of time from <paramref name="start"/>, as near as the day allows.</summary>
    public void ScrollTo(double start)
    {
        if (_zoom is { } zoom)
            Show((start, start + zoom.End - zoom.Start));
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

        // Grid and time labels: whole hours, or finer while zoomed in.
        var (shownStart, shownEnd) = (ShownStart, ShownEnd);
        var pixelsPerSecond = laneWidth / (shownEnd - shownStart);
        var labelStep = TickSteps.FirstOrDefault(step => step * pixelsPerSecond >= TickLabelWidth(step) + LabelSpacing, TickSteps[^1]);
        var gridStep = Math.Min(labelStep, 3600);
        for (var tick = Math.Ceiling(shownStart / gridStep) * gridStep; tick <= shownEnd; tick += gridStep)
        {
            var x = X(tick);
            context.DrawLine(HourLine, new Point(x, lane.Top), new Point(x, lane.Bottom));
            context.DrawLine(HourLine, new Point(x, track.Top), new Point(x, track.Bottom));
            if (tick % labelStep != 0)
                continue;
            var label = Text(TickLabel(tick), LabelBrush);
            var lx = Math.Clamp(x - label.Width / 2, Gutter, Math.Max(Gutter, width - label.Width));
            context.DrawText(label, new Point(lx, track.Bottom + ReachGap + ReachHeight + 2));
        }

        // Zoomed in, what's on the lanes stops at their ends, clear of the lane names.
        using var lanesOnly = context.PushClip(new Rect(Gutter, 0, laneWidth + 1, Bounds.Height));

        // Projects: each block in its colour, named when the part shown has room; suggestions outlined, their breaks dashed.
        foreach (var block in Projects)
        {
            var x0 = X(block.Start);
            var x1 = Math.Max(X(block.End), x0 + 1);
            var rect = new Rect(x0, lane.Top + 2, x1 - x0, ProjectHeight - 3);
            string label;
            IBrush labelBrush;
            switch (block.Style)
            {
                case LaneStyle.Suggested:
                case LaneStyle.Unsure:
                    if (block.Style == LaneStyle.Unsure)
                        DrawHatch(context, rect, block.Brush);
                    else
                        using (context.PushOpacity(SuggestedTint))
                            context.FillRectangle(block.Brush, rect, 2);
                    var outline = rect.Deflate(SuggestedOutline / 2);
                    context.DrawRectangle(null, new Pen(block.Brush, SuggestedOutline), outline.Width > 0 ? outline : rect, 2, 2);
                    (label, labelBrush) = (block.Label, block.Brush);
                    break;
                case LaneStyle.Break:
                    context.DrawRectangle(null, new Pen(block.Brush, 1, DashStyle.Dash), rect.Deflate(new Thickness(0.5, 3)), 2, 2);
                    (label, labelBrush) = ("?", block.Brush);
                    break;
                default:
                    context.FillRectangle(block.Brush, rect, 2);
                    (label, labelBrush) = (block.Label, OnProjectBrush);
                    break;
            }
            var shown = rect.Intersect(lane);
            var name = Text(label, labelBrush);
            if (name.Width + 8 <= shown.Width)
                using (context.PushClip(shown))
                    context.DrawText(name, new Point(block.Style == LaneStyle.Break ? shown.Center.X - name.Width / 2 : shown.Left + 4,
                        rect.Top + (rect.Height - name.Height) / 2));
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
                TimelineKind.OnTask => AppBrushes.OnTask,
                TimelineKind.OffTask => AppBrushes.OffTask,
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
            var from = shownStart;
            foreach (var (start, end) in reach.Where(r => r.End > shownStart && r.Start < shownEnd).OrderBy(r => r.Start))
            {
                if (start > from)
                    context.FillRectangle(ReachVeil, new Rect(X(from), lane.Top, X(start) - X(from), track.Bottom - lane.Top));
                var x0 = X(Math.Max(start, shownStart));
                context.FillRectangle(ReachBar, new Rect(x0, track.Bottom + ReachGap, Math.Max(2, X(Math.Min(end, shownEnd)) - x0), ReachHeight), 1);
                from = Math.Max(from, end);
            }
            if (from < shownEnd)
                context.FillRectangle(ReachVeil, new Rect(X(from), lane.Top, X(shownEnd) - X(from), track.Bottom - lane.Top));
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
            var name = Text(text, LabelBrush);
            context.DrawText(name, new Point(0, beside.Top + (beside.Height - name.Height) / 2));
        }
    }

    /// <summary>Diagonal lines in a block's colour, as an unsure suggestion is filled.</summary>
    private static void DrawHatch(DrawingContext context, Rect rect, IBrush brush)
    {
        using var clip = context.PushClip(rect);
        using var faint = context.PushOpacity(HatchOpacity);
        var pen = new Pen(brush, 1);
        for (var x = rect.Left - rect.Height; x < rect.Right; x += HatchSpacing)
            context.DrawLine(pen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
    }

    private static FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, brush);

    /// <summary>How wide the labels are, every <paramref name="step"/> seconds: hours only, or with minutes.</summary>
    private static double TickLabelWidth(int step) =>
        Text(step % 3600 == 0 ? HourLabel(12) : TickLabel(12 * 3600 + 45 * 60), LabelBrush).Width;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
            ContextPoint = (Time(point.Position.X), point.Position.Y < ProjectHeight + LaneGap / 2);
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
        _pressTime = Time(point.Position.X);
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
        _pointerX = position.X;
        ReportDrag(finished: false);
        // Held past either end while zoomed in, the timeline scrolls that way.
        if (_zoom is not null && (position.X < Gutter || position.X > Gutter + LaneWidth))
            _dragScroll.Start();
        else
            _dragScroll.Stop();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_doubleClicked)
        {
            _doubleClicked = false;
            return;
        }
        if (_pressAt is null)
            return;
        _dragScroll.Stop();
        if (!_dragging)
        {
            Clicked?.Invoke(_pressTime, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else
        {
            _pointerX = e.GetPosition(this).X;
            ReportDrag(finished: true);
        }
        _pressAt = null;
        _dragging = false;
        _edge = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragScroll.Stop();
        _pressAt = null;
        _dragging = false;
        _edge = null;
    }

    /// <summary>
    /// The wheel zooms in or out at the pointer; with Shift, or sideways, it scrolls. A drag under way follows, its
    /// pointer being over another time now.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        var sideways = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) : e.Delta.X;
        var (start, end) = (ShownStart, ShownEnd);
        if (sideways != 0)
        {
            ScrollTo(start - sideways * PanStep * (end - start));
        }
        else if (e.Delta.Y != 0)
        {
            // Keep the time under the pointer where it is.
            var x = e.GetPosition(this).X;
            var at = Time(x);
            var length = (end - start) / Math.Pow(ZoomStep, e.Delta.Y);
            var newStart = at - Math.Clamp((x - Gutter) / LaneWidth, 0, 1) * length;
            Show((newStart, newStart + length));
        }
        if (_dragging)
            ReportDrag(finished: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _dragScroll.Stop();
    }

    /// <summary>A drag to the view model: its anchor (the time pressed at) and the time under the pointer now.</summary>
    private void ReportDrag(bool finished)
    {
        if (_edge is { } edge)
            EdgeDragged?.Invoke(edge, Time(_pointerX), finished);
        else
            Dragged?.Invoke(_pressTime, Time(_pointerX), finished);
    }

    /// <summary>A drag held past an end: scrolls that way, faster the further past, and carries the drag along.</summary>
    private void ScrollWhileDragging()
    {
        if (!_dragging || _zoom is not { } zoom)
        {
            _dragScroll.Stop();
            return;
        }
        var past = _pointerX < Gutter ? _pointerX - Gutter : Math.Max(0, _pointerX - Gutter - LaneWidth);
        var length = zoom.End - zoom.Start;
        ScrollTo(zoom.Start + Math.Clamp(past / DragScrollReach, -1, 1) * DragScrollStep * length);
        ReportDrag(finished: false);
    }

    /// <summary>
    /// Shows this time, at least <see cref="MinShown"/> long and moved inside the time covered; all of it when
    /// <paramref name="zoom"/> is null or reaches as far.
    /// </summary>
    private void Show((double Start, double End)? zoom)
    {
        var all = ViewEnd - ViewStart;
        if (zoom is { } z)
        {
            var length = Math.Max(z.End - z.Start, Math.Min(MinShown, all));
            var start = Math.Clamp(z.Start, ViewStart, Math.Max(ViewStart, ViewEnd - length));
            zoom = length >= all ? null : (start, start + length);
        }
        if (zoom == _zoom)
            return;
        _zoom = zoom;
        InvalidateVisual();
        ShownChanged?.Invoke();
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

    private double X(double seconds) => Gutter + (seconds - ShownStart) / (ShownEnd - ShownStart) * LaneWidth;

    private double Time(double x) => ShownStart + Math.Clamp((x - Gutter) / LaneWidth, 0, 1) * (ShownEnd - ShownStart);

    /// <summary>"9 AM" or "09:00", following the user's clock format.</summary>
    internal static string HourLabel(int hour)
    {
        var time = DateTime.Today.AddHours(hour % 24);
        var culture = CultureInfo.CurrentCulture;
        return culture.DateTimeFormat.ShortTimePattern.Contains('H') ? time.ToString("HH:mm", culture) : time.ToString("h tt", culture);
    }

    /// <summary>A grid line's label: the hour as <see cref="HourLabel"/>, or the time with its minutes in the user's format.</summary>
    private static string TickLabel(double seconds) => seconds % 3600 == 0
        ? HourLabel((int)Math.Floor(seconds / 3600))
        : DateTime.Today.AddSeconds(seconds).ToString("t", CultureInfo.CurrentCulture);
}
