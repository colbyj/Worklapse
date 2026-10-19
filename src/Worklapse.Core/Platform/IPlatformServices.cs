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

    /// <summary>Why nothing can be recorded here (a sentence for the user); null when recording works.</summary>
    public string? CannotRecord { get; init; }

    /// <summary>
    /// A platform that records nothing, for when the real one can't start: the app still opens, so the history can be
    /// reviewed and the settings changed, and the recorder reports <paramref name="reason"/> instead of capturing.
    /// </summary>
    public static PlatformServices Unavailable(string reason) =>
        new(new NoScreens(), new NoForeground(), new NoIdle(), new NeverLocked()) { CannotRecord = reason };

    public void Dispose()
    {
        Screen.Dispose();
        Session.Dispose();
        (Idle as IDisposable)?.Dispose();
        (Foreground as IDisposable)?.Dispose();
    }

    private sealed class NoScreens : IScreenSource
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public ScreenCapture? Capture(MonitorInfo monitor) => null;
        public void Dispose() { }
    }

    private sealed class NoForeground : IForegroundWindowProbe
    {
        public ForegroundWindow? GetForegroundWindow() => null;
    }

    private sealed class NoIdle : IIdleDetector
    {
        public TimeSpan GetIdleTime() => TimeSpan.Zero;
    }

    private sealed class NeverLocked : ISessionStateMonitor
    {
        public bool IsLocked => false;
        public void Dispose() { }
    }
}
