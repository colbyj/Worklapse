using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Worklapse.App.Tray;

/// <summary>
/// The app icon with a badge dot in its lower right corner, cut out of the icon so it reads on light and dark task bars
/// alike. Drawn from the .ico's own picture for the size (16 to 48 pixels), so small icons stay sharp.
/// </summary>
public static class TrayIconImages
{
    private static readonly Uri IconUri = new("avares://Worklapse/Assets/worklapse.ico");

    // Fixed rather than the theme's: the task bar's colours needn't match the windows'.
    private static readonly IBrush RecordingDot = new SolidColorBrush(Color.Parse("#DA4453"));
    private static readonly IBrush PausedDot = new SolidColorBrush(Color.Parse("#F0A30A"));
    private static readonly IBrush ErrorDot = new SolidColorBrush(Color.Parse("#C42B1C"));

    /// <summary>The badge's diameter and the gap cut around it, as fractions of the icon's size.</summary>
    private const double DotSize = 0.5, CutOut = 0.09;

    public static Bitmap Render(TrayBadge badge, int size)
    {
        using var icon = LoadIcon(size);
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = bitmap.CreateDrawingContext())
        {
            var whole = new Rect(0, 0, size, size);
            var dot = new Rect(size * (1 - DotSize), size * (1 - DotSize), size * DotSize, size * DotSize);
            var source = new Rect(icon.Size);
            if (DotBrush(badge) is not { } brush)
            {
                context.DrawImage(icon, source, whole);
            }
            else
            {
                var gap = size * CutOut;
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(whole),
                    new EllipseGeometry(dot.Inflate(gap)));
                using (context.PushGeometryClip(outside))
                    context.DrawImage(icon, source, whole);
                context.DrawEllipse(brush, null, dot);
                if (badge == TrayBadge.Error)
                {
                    // A cross, as on the "Not recording" message bar.
                    var pen = new Pen(Brushes.White, Math.Max(1, size / 16.0), lineCap: PenLineCap.Round);
                    var arm = dot.Width * 0.2;
                    var centre = dot.Center;
                    context.DrawLine(pen, centre + new Point(-arm, -arm), centre + new Point(arm, arm));
                    context.DrawLine(pen, centre + new Point(arm, -arm), centre + new Point(-arm, arm));
                }
            }
        }
        return bitmap;
    }

    private static IBrush? DotBrush(TrayBadge badge) => badge switch
    {
        TrayBadge.Recording => RecordingDot,
        TrayBadge.Paused => PausedDot,
        TrayBadge.Error => ErrorDot,
        _ => null,
    };

    /// <summary>
    /// The .ico's picture closest to <paramref name="size"/> from above. An entry stored as a bitmap rather than a PNG
    /// can't be decoded alone; then the whole .ico is (its largest picture, scaled when drawn).
    /// </summary>
    private static Bitmap LoadIcon(int size)
    {
        using var stream = AssetLoader.Open(IconUri);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return PictureForSize(buffer.ToArray(), size);
    }

    internal static Bitmap PictureForSize(byte[] ico, int size)
    {
        // ICONDIR (6 bytes), then a 16-byte entry per picture: width, height (0 = 256), …, byte count, offset.
        int count = BitConverter.ToUInt16(ico, 4), best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var width = ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i];
            var better = best < 0 || (width >= size ? bestSize < size || width < bestSize : width > bestSize);
            if (better)
                (best, bestSize) = (i, width);
        }
        var length = BitConverter.ToInt32(ico, 6 + 16 * best + 8);
        var offset = BitConverter.ToInt32(ico, 6 + 16 * best + 12);
        ReadOnlySpan<byte> pngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        return ico.AsSpan(offset, length).StartsWith(pngSignature)
            ? new Bitmap(new MemoryStream(ico, offset, length, writable: false))
            : new Bitmap(new MemoryStream(ico, writable: false));
    }
}
