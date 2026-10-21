using System.Globalization;
using SkiaSharp;
using Worklapse.Core.Export;
using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Video;
using PixelRect = Worklapse.Core.Platform.PixelRect;

namespace Worklapse.App.Export;

/// <summary>
/// Draws the frames of a timelapse: a capture's pictures laid out and fitted into the video as Day review shows them,
/// with bars where the shape differs, and the time of day if asked. Each frame comes out as limited-range I420 for the
/// encoder. Not thread-safe; one per export.
/// </summary>
public sealed class TimelapseComposer : IDisposable
{
    private static readonly SKColor Bars = SKColors.Black;
    private static readonly SKColor EmptyTile = new(0x2C, 0x2C, 0x2C); // a monitor with nothing recorded, as in Day review
    private static readonly SKColor ClockBand = new(0, 0, 0, 160);
    // Pictures are first shrunk to at most twice the size they are drawn at (I420.Reduce), so bilinear filtering is
    // enough; Skia's mipmaps would do the same, but cost several times as much, built anew for every frame.
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly int _width;
    private readonly int _height;
    private readonly SKBitmap _frame;
    private readonly SKCanvas _canvas;
    private readonly Dictionary<long, SKBitmap> _pictures = []; // each monitor's picture as BGRA, reused while its size stays
    private byte[] _reduced = [];
    private readonly SKFont? _clockFont;
    private readonly SKPaint _fill = new() { IsAntialias = true };

    public TimelapseComposer(int width, int height, bool clock)
    {
        _width = width;
        _height = height;
        _frame = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        _canvas = new SKCanvas(_frame);
        if (clock)
            _clockFont = new SKFont(SKTypeface.Default, Math.Max(12, height / 28f)) { Subpixel = true };
    }

    /// <summary>Draws <paramref name="frame"/> from its decoded pictures (by monitor id) into <paramref name="i420"/>.</summary>
    public void Compose(TimelapseFrame frame, IReadOnlyDictionary<long, DecodedFrame> pictures, Span<byte> i420)
    {
        _canvas.Clear(Bars);

        // A picture imported from TimeSnapper is a window, each its own size, shown at that size.
        var tiles = frame.Tiles.Select(t => (t.Monitor, Picture: t.Frame is null ? null : pictures.GetValueOrDefault(t.Monitor.Id),
            Bounds: t.Monitor.IsTimeSnapper && t.Frame is not null && pictures.TryGetValue(t.Monitor.Id, out var p)
                ? t.Monitor.Bounds with { Width = p.Width, Height = p.Height }
                : t.Monitor.Bounds)).ToList();
        var content = CapturePictures.Content(tiles.Select(t => t.Bounds), frame.Focus);
        if (!content.IsEmpty)
        {
            var scale = Math.Min((float)_width / content.Width, (float)_height / content.Height);
            _canvas.Save();
            _canvas.Translate((_width - content.Width * scale) / 2, (_height - content.Height * scale) / 2);
            _canvas.Scale(scale);
            _canvas.Translate(-content.X, -content.Y);
            _canvas.ClipRect(Rect(content));
            foreach (var (monitor, picture, bounds) in tiles)
            {
                if (picture is null)
                {
                    _fill.Color = EmptyTile;
                    _canvas.DrawRect(Rect(bounds), _fill);
                    continue;
                }
                // On a scaled desktop a frame has more pixels than its monitor's place; it fills the place.
                var bitmap = Bitmap(monitor.Id, picture, (int)(1 / (scale * bounds.Width / picture.Width)));
                using var pixels = bitmap.PeekPixels();
                using var image = SKImage.FromPixels(pixels);
                _canvas.DrawImage(image, new SKRect(0, 0, bitmap.Width, bitmap.Height), Rect(bounds), Smooth);
            }
            _canvas.Restore();
        }

        if (_clockFont is not null)
            DrawClock(frame.Time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));

        _canvas.Flush();
        I420.FromBgraRows(_frame.GetPixelSpan(), _frame.RowBytes, _width, _height, 0, _height, i420);
        I420.ToLimitedRange(i420, _width, _height);
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _frame.Dispose();
        foreach (var bitmap in _pictures.Values)
            bitmap.Dispose();
        _pictures.Clear();
        _clockFont?.Dispose();
        _fill.Dispose();
    }

    /// <summary>The time, white on a dark band in the bottom right corner.</summary>
    private void DrawClock(string text)
    {
        var font = _clockFont!;
        var width = font.MeasureText(text);
        var metrics = font.Metrics;
        var textHeight = metrics.Descent - metrics.Ascent;
        var padding = font.Size * 0.35f;
        var margin = font.Size * 0.5f;
        var band = new SKRect(_width - margin - width - 2 * padding, _height - margin - textHeight - 2 * padding, _width - margin, _height - margin);
        _fill.Color = ClockBand;
        _canvas.DrawRoundRect(band, padding, padding, _fill);
        _fill.Color = SKColors.White;
        _canvas.DrawText(text, band.Left + padding, band.Top + padding - metrics.Ascent, SKTextAlign.Left, font, _fill);
    }

    /// <summary>
    /// A monitor's picture shrunk by <paramref name="factor"/> (if more than 1) and converted to BGRA, in a bitmap kept
    /// for that monitor.
    /// </summary>
    private SKBitmap Bitmap(long monitorId, DecodedFrame picture, int factor)
    {
        factor = Math.Max(1, factor);
        var (width, height) = I420.ReducedSize(picture.Width, picture.Height, factor);
        if (!_pictures.TryGetValue(monitorId, out var bitmap) || bitmap.Width != width || bitmap.Height != height)
        {
            bitmap?.Dispose();
            _pictures[monitorId] = bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        }
        if (factor == 1)
        {
            picture.CopyBgra(bitmap.GetPixels(), bitmap.RowBytes);
            return bitmap;
        }
        var size = I420.Size(width, height);
        if (_reduced.Length < size)
            _reduced = new byte[size];
        I420.Reduce(picture.I420, picture.Width, picture.Height, factor, _reduced);
        unsafe
        {
            ColorConversion.I420ToBgra(_reduced, width, height, new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount), bitmap.RowBytes);
        }
        return bitmap;
    }

    private static SKRect Rect(PixelRect rect) => new(rect.X, rect.Y, rect.Right, rect.Bottom);
}
