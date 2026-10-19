using Worklapse.Core.Platform;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Places KWin's screens in one pixel coordinate space, as Worklapse expects (<see cref="MonitorInfo.Bounds"/>,
/// <see cref="ForegroundWindow.Bounds"/>).
/// </summary>
/// <remarks>
/// <para>
/// KWin lays screens out in logical coordinates: a 2560-pixel-wide screen at 150 % is 1707 units wide. Worklapse needs
/// each monitor's bounds to be its capture's size in pixels, with windows in the same coordinates, so the area of a
/// window can be cut out of a capture.
/// </para>
/// <para>
/// Each screen is placed at its logical position times the largest scale of any screen, and keeps its own pixel
/// size. With mixed scales that leaves gaps between screens, but they never overlap, and within one screen a
/// window's position is exact: it is converted with the scale of the screen showing most of it.
/// </para>
/// </remarks>
internal sealed class ScreenLayout
{
    private readonly IReadOnlyList<(KWinScreen Screen, MonitorInfo Monitor)> _screens;

    private ScreenLayout(IReadOnlyList<(KWinScreen, MonitorInfo)> screens) => _screens = screens;

    public static ScreenLayout Empty { get; } = new([]);

    public IReadOnlyList<MonitorInfo> Monitors => _screens.Select(s => s.Monitor).ToList();

    /// <param name="capturedSize">
    /// The size of a capture already taken of a screen (by name), which wins over KWin's figures: those can be a
    /// pixel off after scaling.
    /// </param>
    public static ScreenLayout From(KWinScreens report, Func<KWinScreen, (int Width, int Height)?> capturedSize)
    {
        if (report.Screens.Count == 0)
            return Empty;

        var spread = report.Screens.Max(s => s.Scale);

        var screens = new List<(KWinScreen, MonitorInfo)>();
        foreach (var screen in report.Screens)
        {
            var (width, height) = capturedSize(screen) ?? PixelSize(screen);
            var bounds = new PixelRect(
                (int)Math.Round(screen.X * spread),
                (int)Math.Round(screen.Y * spread),
                width,
                height);
            // None is primary: KWin doesn't tell scripts which one is (see PlatformServices.KnowsPrimaryMonitor).
            screens.Add((screen, new MonitorInfo(screen.Name, bounds, IsPrimary: false)
            {
                StableId = StableId(screen),
            }));
        }
        return new ScreenLayout(screens.OrderBy(s => s.Item2.Bounds.X).ThenBy(s => s.Item2.Bounds.Y).ToList());
    }

    /// <summary>
    /// The screen's size in pixels estimated from its logical size (KWin doesn't tell scripts its mode). The first
    /// capture gives the real size.
    /// </summary>
    internal static (int Width, int Height) PixelSize(KWinScreen screen) =>
        ((int)Math.Floor(screen.Width * screen.Scale), (int)Math.Floor(screen.Height * screen.Scale));

    /// <summary>
    /// The monitor across replugging and restarts: its make, model and serial number from the EDID, plus the connector,
    /// like the device interface path Windows uses. Just the connector when KWin knows nothing about the monitor.
    /// </summary>
    internal static string StableId(KWinScreen screen) =>
        screen.Manufacturer.Length == 0 && screen.Model.Length == 0 && screen.Serial.Length == 0
            ? screen.Name
            : $"{screen.Manufacturer}|{screen.Model}|{screen.Serial}|{screen.Name}";

    /// <summary>The screen (by name) for a monitor of this layout; null if it isn't one.</summary>
    public KWinScreen? ScreenOf(MonitorInfo monitor) =>
        _screens.FirstOrDefault(s => s.Monitor.DeviceName == monitor.DeviceName).Screen;

    /// <summary>A window's frame in this layout's pixels, converted with the scale of the screen showing most of it.</summary>
    public PixelRect ToPixels(double x, double y, double width, double height)
    {
        if (_screens.Count == 0)
            return new PixelRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(width), (int)Math.Round(height));

        var (screen, monitor) = _screens
            .Select(s => (Entry: s, Area: Overlap(s.Screen, x, y, width, height)))
            .OrderByDescending(s => s.Area)
            .ThenBy(s => Distance(s.Entry.Screen, x + width / 2, y + height / 2))
            .First()
            .Entry;

        var left = monitor.Bounds.X + (x - screen.X) * screen.Scale;
        var top = monitor.Bounds.Y + (y - screen.Y) * screen.Scale;
        var right = monitor.Bounds.X + (x + width - screen.X) * screen.Scale;
        var bottom = monitor.Bounds.Y + (y + height - screen.Y) * screen.Scale;
        var l = (int)Math.Round(left);
        var t = (int)Math.Round(top);
        return new PixelRect(l, t, (int)Math.Round(right) - l, (int)Math.Round(bottom) - t);
    }

    private static double Overlap(KWinScreen s, double x, double y, double width, double height)
    {
        var w = Math.Min(s.X + s.Width, x + width) - Math.Max(s.X, x);
        var h = Math.Min(s.Y + s.Height, y + height) - Math.Max(s.Y, y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static double Distance(KWinScreen s, double x, double y)
    {
        var dx = Math.Max(Math.Max(s.X - x, 0), x - (s.X + s.Width));
        var dy = Math.Max(Math.Max(s.Y - y, 0), y - (s.Y + s.Height));
        return dx * dx + dy * dy;
    }
}
