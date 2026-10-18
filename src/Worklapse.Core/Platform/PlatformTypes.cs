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

/// <param name="DeviceName">
/// The system's name for the display output (Windows: \\.\DISPLAY1). Not stable: Windows hands the names out
/// again when monitors are plugged in or out.
/// </param>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, bool IsPrimary)
{
    /// <summary>
    /// Identifies the physical monitor across replugging, rearranging and restarts (Windows: its device
    /// interface path, made of the model from its EDID and the connector it's on). The device name when the
    /// platform has nothing better.
    /// </summary>
    public string StableId { get; init; } = DeviceName;
}

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

    /// <summary>
    /// A hash of the pixels: equal hashes mean an unchanged screen. The recorder compares it with the frame it
    /// would reuse, not with whatever the source captured last under the same device name, which after
    /// replugging can be another monitor.
    /// </summary>
    public required ulong Hash { get; init; }
}
