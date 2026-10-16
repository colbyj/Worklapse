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
/// Shows recorded monitors laid out as they were arranged, fitted to the view. Scroll to zoom at the
/// pointer, drag to pan while zoomed in.
/// </summary>
public sealed class FrameView : Control
{
    public const double MaxZoom = 16;
    private const double ContentMargin = 6;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#202020"));
    private static readonly IBrush EmptyTile = new SolidColorBrush(Color.Parse("#2C2C2C"));
    private static readonly IPen TileBorder = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")));
    private static readonly IBrush OverlayBand = new SolidColorBrush(Color.FromArgb(190, 32, 32, 32));
    private static readonly IBrush OverlayText = Brushes.White;
    private static readonly IBrush TileText = new SolidColorBrush(Color.Parse("#8D8D8D"));
    private static readonly Typeface Face = new(FontFamily.Default);

    private IReadOnlyList<FrameTile> _tiles = [];
    private string? _overlay;
    private Point? _panFrom;
    private Vector _panAtPress;

    public FrameView()
    {
        ClipToBounds = true;
        Focusable = false;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    /// <summary>1 = fitted to the view.</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>View pixels per recorded screen pixel: 1 shows the recording at its actual size.</summary>
    public double Magnification => _tiles.Count == 0 ? Zoom : Layout().Scale * Zoom;

    /// <summary>Offset of the zoomed content, in view pixels.</summary>
    public Vector Pan { get; private set; }

    public event Action? ZoomChanged;

    public void SetContent(IReadOnlyList<FrameTile> tiles, string? overlay)
    {
        var layoutChanged = tiles.Count != _tiles.Count || tiles.Where((t, i) => t.Bounds != _tiles[i].Bounds).Any();
        _tiles = tiles;
        _overlay = overlay;
        if (layoutChanged)
            Fit();
        InvalidateVisual();
    }

    public void Fit()
    {
        Zoom = 1;
        Pan = default;
        InvalidateVisual();
        ZoomChanged?.Invoke();
    }

    public void ZoomBy(double factor) => ZoomAt(Zoom * factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (_tiles.Count == 0)
            return;

        var (origin, scale) = Layout();
        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(Pan)))
        {
            foreach (var tile in _tiles)
            {
                var dest = new Rect(origin.X + tile.Bounds.X * scale, origin.Y + tile.Bounds.Y * scale, tile.Bounds.Width * scale, tile.Bounds.Height * scale);
                if (tile.Image is { } image)
                {
                    context.DrawImage(image, new Rect(image.Size), dest);
                }
                else
                {
                    context.FillRectangle(EmptyTile, dest);
                    if (tile.Label is { } label)
                    {
                        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12 / Zoom, TileText);
                        context.DrawText(text, dest.Center - new Point(text.Width / 2, text.Height / 2));
                    }
                }
                context.DrawRectangle(null, TileBorder, dest);
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

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(Zoom * (e.Delta.Y > 0 ? 1.25 : 0.8), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Zoom <= 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _panFrom = e.GetPosition(this);
        _panAtPress = Pan;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panFrom is not { } from)
            return;
        Pan = Clamp(Zoom, _panAtPress + (e.GetPosition(this) - from));
        InvalidateVisual();
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

    private void ZoomAt(double zoom, Point at)
    {
        zoom = Math.Clamp(zoom, 1, MaxZoom);
        // Keep the content under the pointer where it is.
        var k = zoom / Zoom;
        var pan = new Vector(at.X - (at.X - Pan.X) * k, at.Y - (at.Y - Pan.Y) * k);
        Zoom = zoom;
        Pan = zoom == 1 ? default : Clamp(zoom, pan);
        InvalidateVisual();
        ZoomChanged?.Invoke();
    }

    /// <summary>Keeps zoomed content covering the view (no panning past its edges).</summary>
    private Vector Clamp(double zoom, Vector pan) => new(
        Math.Clamp(pan.X, (1 - zoom) * Bounds.Width, 0),
        Math.Clamp(pan.Y, (1 - zoom) * Bounds.Height, 0));

    /// <summary>Where desktop coordinate (0,0) lands at zoom 1, and view pixels per desktop pixel.</summary>
    private (Point Origin, double Scale) Layout()
    {
        var left = _tiles.Min(t => t.Bounds.X);
        var top = _tiles.Min(t => t.Bounds.Y);
        var right = _tiles.Max(t => t.Bounds.Right);
        var bottom = _tiles.Max(t => t.Bounds.Bottom);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var scale = Math.Min((Bounds.Width - 2 * ContentMargin) / width, (Bounds.Height - 2 * ContentMargin) / height);
        scale = Math.Max(scale, 0.001);
        var offsetX = (Bounds.Width - width * scale) / 2 - left * scale;
        var offsetY = (Bounds.Height - height * scale) / 2 - top * scale;
        return (new Point(offsetX, offsetY), scale);
    }
}
