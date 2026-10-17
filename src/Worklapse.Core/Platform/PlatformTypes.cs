namespace Worklapse.Core.Platform;

/// <summary>A rectangle in physical desktop pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new PixelRect(x, y, right - x, bottom - y) : default;
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, bool IsPrimary);

/// <summary>The window that had focus at capture time.</summary>
/// <param name="Site">
/// For a browser, the site it shows (<see cref="SiteDomain"/>); null for other apps, browser pages, private
/// windows, or when the address couldn't be read in time.
/// </param>
public sealed record ForegroundWindow(string ProcessName, string? ProcessPath, string Title, PixelRect Bounds, string? Site = null);

/// <summary>
/// One monitor's pixels as a packed I420 frame (see <see cref="Imaging.I420"/>). The memory belongs to the
/// screen source and is reused for every capture: it stays valid only until the source's next capture of
/// any monitor. The recorder may modify it in place (to black out areas) before passing it on.
/// </summary>
public sealed class ScreenCapture
{
    public required MonitorInfo Monitor { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Memory<byte> Frame { get; init; }

    /// <summary>False when the pixels are identical to the source's previous capture of this monitor.</summary>
    public required bool Changed { get; init; }
}
