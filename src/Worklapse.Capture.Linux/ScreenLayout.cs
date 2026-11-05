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
/// Each screen keeps its own pixel size and is placed right after the screens it touches on its left and above, so
/// screens at different scales still meet edge to edge, as they do on the desk. If that would make two overlap (an
/// unusual layout), every screen goes at its logical position times the largest scale instead: gaps, but no overlap.
/// Within one screen a window's position is exact: it is converted with the scale of the screen showing most of it.
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

        var all = report.Screens;
        var sizes = all.ToDictionary(s => s, s => capturedSize(s) ?? PixelSize(s));
        var spread = all.Max(s => s.Scale);

        // Each screen starts where the screens touching it on the left (or above) end, in pixels.
        var lefts = Place(all, s => s.X, s => s.Width, s => s.Y, s => s.Height, s => sizes[s].Width, spread);
        var tops = Place(all, s => s.Y, s => s.Height, s => s.X, s => s.Width, s => sizes[s].Height, spread);
        var bounds = all.ToDictionary(s => s, s => new PixelRect(lefts[s], tops[s], sizes[s].Width, sizes[s].Height));

        // Placing by neighbours can't overlap side by side or stacked layouts, but an unusual one (screens offset
        // diagonally, say) might. Then every screen at its logical position times the largest scale, which never does.
        if (AnyOverlap(bounds.Values.ToList()))
            bounds = all.ToDictionary(s => s, s => new PixelRect(
                (int)Math.Round(s.X * spread), (int)Math.Round(s.Y * spread), sizes[s].Width, sizes[s].Height));

        var screens = new List<(KWinScreen, MonitorInfo)>();
        foreach (var screen in all)
        {
            // None is primary: KWin doesn't tell scripts which one is (see PlatformServices.KnowsPrimaryMonitor).
            screens.Add((screen, new MonitorInfo(screen.Name, bounds[screen], IsPrimary: false)
            {
                StableId = StableId(screen),
            }));
        }
        return new ScreenLayout(screens.OrderBy(s => s.Item2.Bounds.X).ThenBy(s => s.Item2.Bounds.Y).ToList());
    }

    /// <summary>
    /// Each screen's pixel position along one axis: right after the pixel end of the nearest screens before it on that
    /// axis that share some of its extent on the other (the screens it touches, in a usual layout), plus any logical
    /// gap to them at the largest scale. A screen with none before it keeps its logical position at that scale.
    /// </summary>
    private static Dictionary<KWinScreen, int> Place(IReadOnlyList<KWinScreen> screens,
        Func<KWinScreen, double> start, Func<KWinScreen, double> length,
        Func<KWinScreen, double> crossStart, Func<KWinScreen, double> crossLength,
        Func<KWinScreen, int> pixelLength, double spread)
    {
        var placed = new Dictionary<KWinScreen, int>();
        foreach (var screen in screens.OrderBy(start))
        {
            var before = placed.Keys
                .Where(other => start(other) + length(other) <= start(screen)
                    && crossStart(other) < crossStart(screen) + crossLength(screen)
                    && crossStart(screen) < crossStart(other) + crossLength(other))
                .ToList();
            if (before.Count == 0)
            {
                placed[screen] = (int)Math.Round(start(screen) * spread);
                continue;
            }
            // The nearest of them: those further away have these between, already counted in their pixel ends.
            var end = before.Max(other => start(other) + length(other));
            var gap = (int)Math.Round((start(screen) - end) * spread);
            placed[screen] = before
                .Where(other => start(other) + length(other) == end)
                .Max(other => placed[other] + pixelLength(other)) + gap;
        }
        return placed;
    }

    private static bool AnyOverlap(IReadOnlyList<PixelRect> rects)
    {
        for (var i = 0; i < rects.Count; i++)
            for (var j = i + 1; j < rects.Count; j++)
                if (!rects[i].Intersect(rects[j]).IsEmpty)
                    return true;
        return false;
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
