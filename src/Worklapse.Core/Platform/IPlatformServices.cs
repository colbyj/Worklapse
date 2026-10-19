namespace Worklapse.Core.Platform;

public interface IScreenSource : IDisposable
{
    /// <summary>The monitors currently attached. The list can change when displays are plugged in or out.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Captures a monitor. Returns null when the screen can't be read right now (for example while the
    /// secure desktop is shown); the caller simply skips that monitor for this capture.
    /// </summary>
    ScreenCapture? Capture(MonitorInfo monitor);
}

public interface IForegroundWindowProbe
{
    ForegroundWindow? GetForegroundWindow();
}

public interface IIdleDetector
{
    /// <summary>Time since the last keyboard or mouse input in this session.</summary>
    TimeSpan GetIdleTime();
}

public interface ISessionStateMonitor : IDisposable
{
    bool IsLocked { get; }
}

public sealed record PlatformServices(
    IScreenSource Screen,
    IForegroundWindowProbe Foreground,
    IIdleDetector Idle,
    ISessionStateMonitor Session) : IDisposable
{
    /// <summary>
    /// Makes a further screen source, for a second user next to the recorder (the Settings preview): a source reuses
    /// its buffer for every capture. Null when the platform can't.
    /// </summary>
    public Func<IScreenSource>? CreateScreenSource { get; init; }

    public void Dispose()
    {
        Screen.Dispose();
        Session.Dispose();
        (Idle as IDisposable)?.Dispose();
        (Foreground as IDisposable)?.Dispose();
    }
}
