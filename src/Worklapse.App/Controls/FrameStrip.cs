using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Worklapse.App.Theming;

namespace Worklapse.App.Controls;

/// <summary>
/// A strip from the first frame's time to the last with a tick per frame, so gaps show where nothing changed or
/// nothing was recorded; hours are marked above it. Clicking or dragging picks the frame nearest that moment.
/// </summary>
public sealed class FrameStrip : ThemedControl
{
    private const double LabelHeight = 14, TrackHeight = 20, Padding = 6;

    private IReadOnlyList<DateTimeOffset> _times = [];
    private int _current = -1;

    public FrameStrip()
    {
        Height = LabelHeight + TrackHeight + 4;
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    /// <summary>Raised with the frame picked by clicking or dragging.</summary>
    public event Action<int>? Picked;

    /// <summary>The frames' times, in order.</summary>
    public void SetFrames(IReadOnlyList<DateTimeOffset> times)
    {
        _times = times;
        _current = -1;
        InvalidateVisual();
    }

    public int Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            InvalidateVisual();
        }
    }

    private double Left => Padding;
    private double TrackWidth => Math.Max(1, Bounds.Width - 2 * Padding);
    private long Start => _times[0].ToUnixTimeMilliseconds();
    private double Span => Math.Max(1, _times[^1].ToUnixTimeMilliseconds() - Start);

    private double X(DateTimeOffset time) => Left + (time.ToUnixTimeMilliseconds() - Start) / Span * TrackWidth;

    public override void Render(DrawingContext context)
    {
        var track = new Rect(Left, LabelHeight + 2, TrackWidth, TrackHeight);
        context.FillRectangle(AppBrushes.TimelineTrack, track, 3);
        context.DrawRectangle(null, new Pen(AppBrushes.TimelineFrame, 1), track.Deflate(0.5), 3, 3);
        if (_times.Count == 0)
            return;

        DrawHours(context, track);

        // A tick per frame; frames closer than a pixel share one.
        var tick = new Pen(AppBrushes.NotRecorded, 1);
        var lastX = double.NaN;
        foreach (var time in _times)
        {
            var x = Math.Round(X(time)) + 0.5;
            if (x == lastX)
                continue;
            lastX = x;
            context.DrawLine(tick, new Point(x, track.Top + 4), new Point(x, track.Bottom - 4));
        }

        if (_current >= 0 && _current < _times.Count)
        {
            var x = Math.Round(X(_times[_current])) + 0.5;
            context.DrawLine(new Pen(AppBrushes.SelectionEdge, 3), new Point(x, track.Top - 2), new Point(x, track.Bottom + 2));
        }
    }

    /// <summary>Hour lines with their labels, thinned out so labels don't run into each other.</summary>
    private void DrawHours(DrawingContext context, Rect track)
    {
        var first = _times[0];
        var hourWidth = 3_600_000 / Span * TrackWidth;
        var step = new[] { 1, 2, 3, 6, 12, 24 }.FirstOrDefault(h => h * hourWidth >= 44, 0);
        if (step == 0)
            return;

        var hour = new DateTimeOffset(first.Year, first.Month, first.Day, first.Hour, 0, 0, first.Offset).AddHours(1);
        var line = new Pen(AppBrushes.TimelineHourLine, 1);
        var typeface = new Typeface(FontFamily.Default);
        for (; hour <= _times[^1]; hour = hour.AddHours(1))
        {
            if (hour.Hour % step != 0)
                continue;
            var x = Math.Round(X(hour)) + 0.5;
            context.DrawLine(line, new Point(x, track.Top + 1), new Point(x, track.Bottom - 1));
            var text = hour.Hour == 0 ? hour.ToString("ddd d", CultureInfo.CurrentCulture) : hour.ToString("HH:mm", CultureInfo.CurrentCulture);
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, AppBrushes.TimelineLabel);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Bounds.Width - label.Width), 0));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Pick(e.GetPosition(this).X);
    }

    private void Pick(double x)
    {
        if (_times.Count == 0)
            return;
        var ms = Start + (Math.Clamp(x, Left, Left + TrackWidth) - Left) / TrackWidth * Span;
        var index = Nearest(_times, (long)ms);
        if (index != _current)
            Picked?.Invoke(index);
    }

    private static int Nearest(IReadOnlyList<DateTimeOffset> times, long ms)
    {
        int lo = 0, hi = times.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (times[mid].ToUnixTimeMilliseconds() < ms)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo > 0 && ms - times[lo - 1].ToUnixTimeMilliseconds() < times[lo].ToUnixTimeMilliseconds() - ms)
            lo--;
        return lo;
    }
}
