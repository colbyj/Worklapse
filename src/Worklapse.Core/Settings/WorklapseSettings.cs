namespace Worklapse.Core.Settings;

public enum CaptureMode
{
    ActiveApplication,
    PrimaryMonitor,
    SelectedMonitors,
    AllMonitors,
}

public sealed record WorklapseSettings
{
    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Worklapse");

    public CaptureMode CaptureMode { get; init; } = CaptureMode.AllMonitors;

    /// <summary>
    /// The monitors captured in <see cref="CaptureMode.SelectedMonitors"/> mode, by
    /// <see cref="Platform.MonitorInfo.StableId"/> (device names change when monitors are plugged in or out).
    /// </summary>
    public IReadOnlyList<string> SelectedMonitors { get; init; } = [];

    public int IntervalSeconds { get; init; } = 10;

    /// <summary>Capturing stops after this long without keyboard or mouse input.</summary>
    public int IdleMinutes { get; init; } = 5;

    public bool PauseWhenLocked { get; init; } = true;

    /// <summary>AV1 quantizer (libaom cq-level, 0–63). Lower is sharper and larger.</summary>
    public int Quality { get; init; } = 40;

    public string DataFolder { get; init; } = DefaultDataFolder;

    /// <summary>
    /// Draw the windows with the GPU instead of the CPU. Set in the settings file only; takes effect at the
    /// next start, and only on Windows so far. Off by default: GPU rendering measured ~80 MB more while
    /// recording and ~450 MB more while reviewing four monitors, for no CPU saving in a normal-sized window.
    /// It may be smoother with Day review maximized on a very large or high-DPI screen.
    /// </summary>
    public bool UseGpuRendering { get; init; }

    public WorklapseSettings Normalized() => this with
    {
        SelectedMonitors = SelectedMonitors ?? [], // "null" in a hand-edited file
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 300),
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120),
        Quality = Math.Clamp(Quality, 0, 63),
        DataFolder = string.IsNullOrWhiteSpace(DataFolder) ? DefaultDataFolder : DataFolder,
    };
}
