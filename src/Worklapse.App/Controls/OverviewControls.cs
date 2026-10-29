using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Worklapse.App.Theming;

namespace Worklapse.App.Controls;

/// <summary>
/// A day in the Overview: its projects lane above a thin activity lane, drawn on a time axis the week's days share,
/// with the hours as faint lines and, on today, a line at the time it was read. Times are seconds since midnight, as
/// in <see cref="TimelineControl"/>, and the colours are the same.
/// </summary>
public sealed class DayStrip : ThemedControl
{
    private const double ProjectHeight = 14;
    private const double LaneGap = 3;
    private const double ActivityHeight = 6;

    private static readonly IPen HourLine = new Pen(AppBrushes.TimelineHourLine);
    private static readonly IPen Frame = new Pen(AppBrushes.TimelineFrame);
    private static readonly IPen NowPen = new Pen(AppBrushes.Marked, 2);

    public static readonly StyledProperty<double> AxisStartProperty = AvaloniaProperty.Register<DayStrip, double>(nameof(AxisStart));
    public static readonly StyledProperty<double> AxisEndProperty = AvaloniaProperty.Register<DayStrip, double>(nameof(AxisEnd), 24 * 3600);
    public static readonly StyledProperty<IReadOnlyList<ProjectLaneBlock>> BlocksProperty =
        AvaloniaProperty.Register<DayStrip, IReadOnlyList<ProjectLaneBlock>>(nameof(Blocks), []);
    public static readonly StyledProperty<IReadOnlyList<TimelineSpan>> ActivityProperty =
        AvaloniaProperty.Register<DayStrip, IReadOnlyList<TimelineSpan>>(nameof(Activity), []);
    public static readonly StyledProperty<double?> NowProperty = AvaloniaProperty.Register<DayStrip, double?>(nameof(Now));

    static DayStrip() => AffectsRender<DayStrip>(AxisStartProperty, AxisEndProperty, BlocksProperty, ActivityProperty, NowProperty);

    // The now line reaches a little above and below the lanes (drawing outside the bounds is allowed by default).
    public DayStrip() => Height = ProjectHeight + LaneGap + ActivityHeight + 1;

    public double AxisStart { get => GetValue(AxisStartProperty); set => SetValue(AxisStartProperty, value); }
    public double AxisEnd { get => GetValue(AxisEndProperty); set => SetValue(AxisEndProperty, value); }
    public IReadOnlyList<ProjectLaneBlock> Blocks { get => GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }
    public IReadOnlyList<TimelineSpan> Activity { get => GetValue(ActivityProperty); set => SetValue(ActivityProperty, value); }
    public double? Now { get => GetValue(NowProperty); set => SetValue(NowProperty, value); }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(1, Bounds.Width - 1);
        var lane = new Rect(0.5, 0.5, width, ProjectHeight);
        var track = new Rect(0.5, ProjectHeight + LaneGap + 0.5, width, ActivityHeight);
        context.FillRectangle(AppBrushes.TimelineTrack, lane);
        context.FillRectangle(AppBrushes.TimelineTrack, track);

        for (var hour = (int)Math.Ceiling(AxisStart / 3600); hour * 3600 <= AxisEnd; hour++)
        {
            var x = X(hour * 3600.0, width);
            context.DrawLine(HourLine, new Point(x, lane.Top), new Point(x, lane.Bottom));
        }

        // Anything outside the axis is cut off at the lanes' ends rather than squeezed onto them.
        using (context.PushClip(lane))
            foreach (var block in Blocks)
            {
                var x0 = X(block.Start, width);
                var x1 = Math.Max(X(block.End, width), x0 + 1);
                context.FillRectangle(block.Brush, new Rect(x0, lane.Top + 1, x1 - x0, ProjectHeight - 2), 2);
            }
        using (context.PushClip(track))
            foreach (var span in Activity)
            {
                var x0 = X(span.Start, width);
                var x1 = Math.Max(X(span.End, width), x0 + 1);
                var brush = span.Kind switch
                {
                    TimelineKind.OnTask => AppBrushes.OnTask,
                    TimelineKind.OffTask => AppBrushes.OffTask,
                    _ => AppBrushes.Idle,
                };
                context.FillRectangle(brush, new Rect(x0, track.Top, x1 - x0, ActivityHeight));
            }
        context.DrawRectangle(null, Frame, lane);
        context.DrawRectangle(null, Frame, track);

        if (Now is { } now && now >= AxisStart && now <= AxisEnd)
        {
            var x = X(now, width);
            context.DrawLine(NowPen, new Point(x, lane.Top - 3), new Point(x, track.Bottom + 3));
        }
    }

    private double X(double seconds, double width) => 0.5 + (seconds - AxisStart) / Math.Max(60, AxisEnd - AxisStart) * width;
}

/// <summary>The hour labels over a column of <see cref="DayStrip"/>s of the same width and axis.</summary>
public sealed class HourScale : ThemedControl
{
    private static readonly Typeface LabelFace = new(FontFamily.Default);

    public static readonly StyledProperty<double> AxisStartProperty = AvaloniaProperty.Register<HourScale, double>(nameof(AxisStart));
    public static readonly StyledProperty<double> AxisEndProperty = AvaloniaProperty.Register<HourScale, double>(nameof(AxisEnd), 24 * 3600);

    static HourScale() => AffectsRender<HourScale>(AxisStartProperty, AxisEndProperty);

    public HourScale() => Height = 16;

    public double AxisStart { get => GetValue(AxisStartProperty); set => SetValue(AxisStartProperty, value); }
    public double AxisEnd { get => GetValue(AxisEndProperty); set => SetValue(AxisEndProperty, value); }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(1, Bounds.Width - 1);
        var span = Math.Max(60, AxisEnd - AxisStart);
        var firstHour = (int)Math.Ceiling(AxisStart / 3600);
        var labels = new List<(double X, FormattedText Text)>();
        for (var hour = firstHour; hour * 3600 <= AxisEnd; hour++)
            labels.Add((0.5 + (hour * 3600 - AxisStart) / span * width, new FormattedText(TimelineControl.HourLabel(hour),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, AppBrushes.TimelineLabel)));
        if (labels.Count == 0)
            return;

        // Every hour if the widest label fits between two, else every second or third.
        var widest = labels.Max(l => l.Text.Width) + 8;
        var every = Math.Max(1, (int)Math.Ceiling(widest / (width * 3600 / span)));
        foreach (var (x, text) in labels.Where((_, i) => (firstHour + i) % every == 0))
            context.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width)), 1));
    }
}

/// <summary>A part of a <see cref="ShareBar"/>: its size, and its colour or (for time without a project) a hatch.</summary>
public sealed record BarSegment(double Value, IBrush Brush, bool Hatched = false);

/// <summary>A day's time split by category, as a thin bar the width of its cell (a day of the Overview's month).</summary>
public sealed class ShareBar : ThemedControl
{
    private static readonly IPen HatchPen = new Pen(AppBrushes.NotRecorded, 1.5);

    public static readonly StyledProperty<IReadOnlyList<BarSegment>> SegmentsProperty =
        AvaloniaProperty.Register<ShareBar, IReadOnlyList<BarSegment>>(nameof(Segments), []);

    static ShareBar() => AffectsRender<ShareBar>(SegmentsProperty);

    public ShareBar()
    {
        Height = 8;
        ClipToBounds = true;
    }

    public IReadOnlyList<BarSegment> Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public override void Render(DrawingContext context)
    {
        var total = Segments.Sum(s => s.Value);
        if (total <= 0)
            return;
        const double gap = 1;
        var usable = Math.Max(0, Bounds.Width - gap * (Segments.Count - 1));
        var x = 0.0;
        foreach (var segment in Segments)
        {
            // At least a pixel each, with the ones after moved along, so a sliver still shows and none overrun.
            var width = Math.Max(1, segment.Value / total * usable);
            var rect = new Rect(x, 0, Math.Min(width, Math.Max(0, Bounds.Width - x)), Bounds.Height);
            if (segment.Hatched)
            {
                context.FillRectangle(AppBrushes.TimelineTrack, rect);
                using (context.PushClip(rect))
                    for (var hx = rect.Left - rect.Height; hx < rect.Right; hx += 4)
                        context.DrawLine(HatchPen, new Point(hx, rect.Bottom), new Point(hx + rect.Height, rect.Top));
            }
            else
            {
                context.FillRectangle(segment.Brush, rect);
            }
            x += width + gap;
        }
    }
}
