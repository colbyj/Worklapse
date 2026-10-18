using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PixelRect = Worklapse.Core.Platform.PixelRect;

namespace Worklapse.App.Controls;

/// <summary>One monitor's place in the view and what to show there (null bitmap: nothing recorded).</summary>
public sealed record FrameTile(PixelRect Bounds, IImage? Image, string? Label);

/// <summary>
/// Shows recorded monitors laid out as they were arranged, or only a part of the desktop (the focus, e.g. the
/// active window), fitted to the view. Scroll to zoom at the pointer; drag with the left or middle button to
/// pan while the picture is larger than the view. A zoom set by the user keeps its magnification when the
/// content changes size, so stepping between frames doesn't change the size of what's on screen.
/// </summary>
public sealed class FrameView : Control
{
    /// <summary>Largest magnification: 16 view pixels per recorded pixel.</summary>
    public const double MaxMagnification = 16;
    private const double ContentMargin = 6;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#202020"));
    private static readonly IBrush EmptyTile = new SolidColorBrush(Color.Parse("#2C2C2C"));
    private static readonly IBrush TileBorder = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush OverlayBand = new SolidColorBrush(Color.FromArgb(190, 32, 32, 32));
    private static readonly IBrush OverlayText = Brushes.White;
    private static readonly IBrush TileText = new SolidColorBrush(Color.Parse("#8D8D8D"));
    private static readonly Typeface Face = new(FontFamily.Default);

    private IReadOnlyList<FrameTile> _tiles = [];
    private string? _overlay;
    private PixelRect _content;       // the desktop area shown: the tiles, or the focus within them
    private double? _magnification;   // null: fitted to the view
    private Vector _offset;           // scroll position, in view pixels from the top left of the extent
    private Point? _panFrom;
    private Vector _offsetAtPress;

    public FrameView()
    {
        ClipToBounds = true;
        Focusable = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public bool IsFitted => _magnification is null;

    /// <summary>View pixels per recorded screen pixel: 1 shows the recording at its actual size.</summary>
    public double Magnification => _magnification ?? FitScale();

    /// <summary>The size of the whole picture at the current magnification, margins included.</summary>
    public Size Extent => _content.IsEmpty
        ? default
        : new Size(_content.Width * Magnification + 2 * ContentMargin, _content.Height * Magnification + 2 * ContentMargin);

    /// <summary>The part of the extent scrolled past, in view pixels; zero along an axis where the picture fits.</summary>
    public Vector Offset => ClampOffset(_offset);

    /// <summary>
    /// The largest <see cref="Offset"/>: how far the picture is larger than the view. Zero when fitted, and for less than a
    /// pixel (rounding).
    /// </summary>
    public Vector MaxOffset
    {
        get
        {
            if (IsFitted)
                return default;
            var extent = Extent;
            return new Vector(Overflow(extent.Width, Bounds.Width), Overflow(extent.Height, Bounds.Height));

            static double Overflow(double extent, double view) => extent - view >= 1 ? extent - view : 0;
        }
    }

    /// <summary>Raised when the magnification, the scroll position or the size of the picture changes.</summary>
    public event Action? ViewChanged;

    /// <param name="focus">
    /// The desktop area to show instead of all the tiles (only its part on the tiles is shown); null for all.
    /// </param>
    public void SetContent(IReadOnlyList<FrameTile> tiles, string? overlay, PixelRect? focus = null)
    {
        var content = ContentOf(tiles, focus);
        if (content != _content)
        {
            // Zoomed in: keep the same part of the picture (proportionally) in the middle of the view.
            var keep = _magnification is not null && !_content.IsEmpty && !content.IsEmpty;
            var middle = keep ? ToDesktop(new Point(Bounds.Width / 2, Bounds.Height / 2)) : default;
            var relative = new Point((middle.X - _content.X) / _content.Width, (middle.Y - _content.Y) / _content.Height);
            _content = content;
            if (keep)
                ScrollToPlace(new Point(content.X + relative.X * content.Width, content.Y + relative.Y * content.Height),
                    new Point(Bounds.Width / 2, Bounds.Height / 2));
            else
                _offset = default;
            ViewChanged?.Invoke();
        }
        _tiles = tiles;
        _overlay = overlay;
        InvalidateVisual();
    }

    public void Fit()
    {
        _magnification = null;
        _offset = default;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomBy(double factor) => ZoomAt(Magnification * factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>Scrolls to <paramref name="offset"/> (see <see cref="Offset"/>), within the picture.</summary>
    public void ScrollTo(Vector offset)
    {
        offset = ClampOffset(offset);
        if (offset == Offset)
            return;
        _offset = offset;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));

        if (_tiles.Count > 0 && !_content.IsEmpty)
        {
            var (origin, scale) = Placement();
            var clip = new Rect(_content.X, _content.Y, _content.Width, _content.Height);
            var border = new Pen(TileBorder, 1 / scale);
            // Drawn in desktop pixels.
            using (context.PushTransform(Matrix.CreateTranslation(-_content.X, -_content.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin)))
            using (context.PushClip(clip))
            {
                foreach (var tile in _tiles)
                {
                    var dest = new Rect(tile.Bounds.X, tile.Bounds.Y, tile.Bounds.Width, tile.Bounds.Height);
                    if (!dest.Intersects(clip))
                        continue;
                    if (tile.Image is { } image)
                    {
                        context.DrawImage(image, new Rect(image.Size), dest);
                    }
                    else
                    {
                        context.FillRectangle(EmptyTile, dest);
                        if (tile.Label is { } label)
                        {
                            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12 / scale, TileText);
                            var middle = dest.Intersect(clip).Center;
                            context.DrawText(text, middle - new Point(text.Width / 2, text.Height / 2));
                        }
                    }
                    context.DrawRectangle(null, border, dest);
                }
            }
        }

        if (_overlay is { } overlay)
        {
            var text = new FormattedText(overlay, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 15, OverlayText);
            var band = new Rect(0, Bounds.Height / 2 - text.Height, Bounds.Width, text.Height * 2);
            context.FillRectangle(OverlayBand, band);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, band.Y + text.Height / 2));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _offset = ClampOffset(_offset);
        ViewChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(Magnification * (e.Delta.Y > 0 ? 1.25 : 0.8), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (MaxOffset == default || !(properties.IsLeftButtonPressed || properties.IsMiddleButtonPressed))
            return;
        _panFrom = e.GetPosition(this);
        _offsetAtPress = Offset;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panFrom is not { } from)
            return;
        ScrollTo(_offsetAtPress - (e.GetPosition(this) - from));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndPan(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;
        Cursor = Cursor.Default;
    }

    private void EndPan(IPointer pointer)
    {
        if (_panFrom is null)
            return;
        _panFrom = null;
        Cursor = Cursor.Default;
        pointer.Capture(null);
    }

    private void ZoomAt(double magnification, Point at)
    {
        if (_content.IsEmpty)
            return;
        var current = Magnification;
        var fit = FitScale();
        // Zooming out stops at the fitted size. A picture kept smaller than that (from a larger one before) can
        // still be zoomed in.
        if (magnification < current && magnification <= fit)
        {
            if (current > fit)
                Fit();
            return;
        }
        magnification = Math.Min(magnification, Math.Max(MaxMagnification, fit));
        if (magnification == current)
            return;

        // Keep the content under the pointer where it is.
        var under = ToDesktop(at);
        _magnification = magnification;
        ScrollToPlace(under, at);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Scrolls so that desktop point <paramref name="desktop"/> is at <paramref name="view"/>, as near as the picture allows.</summary>
    private void ScrollToPlace(Point desktop, Point view)
    {
        var scale = Magnification;
        _offset = ClampOffset(new Vector(
            ContentMargin + (desktop.X - _content.X) * scale - view.X,
            ContentMargin + (desktop.Y - _content.Y) * scale - view.Y));
    }

    private Vector ClampOffset(Vector offset)
    {
        var max = MaxOffset;
        return new Vector(Math.Clamp(offset.X, 0, max.X), Math.Clamp(offset.Y, 0, max.Y));
    }

    /// <summary>
    /// Where the content's top left corner is in the view, and view pixels per desktop pixel. Along an axis where
    /// the picture fits, it is centred; otherwise scrolled.
    /// </summary>
    private (Point Origin, double Scale) Placement()
    {
        var scale = Magnification;
        var max = MaxOffset;
        var offset = Offset;
        var x = max.X > 0 ? ContentMargin - offset.X : (Bounds.Width - _content.Width * scale) / 2;
        var y = max.Y > 0 ? ContentMargin - offset.Y : (Bounds.Height - _content.Height * scale) / 2;
        return (new Point(x, y), scale);
    }

    private Point ToDesktop(Point view)
    {
        var (origin, scale) = Placement();
        return new Point(_content.X + (view.X - origin.X) / scale, _content.Y + (view.Y - origin.Y) / scale);
    }

    /// <summary>View pixels per desktop pixel that fit the content in the view.</summary>
    private double FitScale()
    {
        if (_content.IsEmpty)
            return 1;
        var scale = Math.Min((Bounds.Width - 2 * ContentMargin) / _content.Width, (Bounds.Height - 2 * ContentMargin) / _content.Height);
        return Math.Max(scale, 0.001);
    }

    /// <summary>The area the tiles cover, cut to <paramref name="focus"/> unless that misses them all.</summary>
    private static PixelRect ContentOf(IReadOnlyList<FrameTile> tiles, PixelRect? focus)
    {
        if (tiles.Count == 0)
            return default;
        var left = tiles.Min(t => t.Bounds.X);
        var top = tiles.Min(t => t.Bounds.Y);
        var all = new PixelRect(left, top, tiles.Max(t => t.Bounds.Right) - left, tiles.Max(t => t.Bounds.Bottom) - top);
        return focus is { } area && !all.Intersect(area).IsEmpty ? all.Intersect(area) : all;
    }
}
