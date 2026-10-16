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

    /// <summary>Device names of the monitors captured in <see cref="CaptureMode.SelectedMonitors"/> mode.</summary>
    public IReadOnlyList<string> SelectedMonitors { get; init; } = [];

    public int IntervalSeconds { get; init; } = 10;

    /// <summary>Capturing stops after this long without keyboard or mouse input.</summary>
    public int IdleMinutes { get; init; } = 5;

    public bool PauseWhenLocked { get; init; } = true;

    /// <summary>AV1 quantizer (libaom cq-level, 0–63). Lower is sharper and larger.</summary>
    public int Quality { get; init; } = 40;

    public string DataFolder { get; init; } = DefaultDataFolder;

    public WorklapseSettings Normalized() => this with
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 300),
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120),
        Quality = Math.Clamp(Quality, 0, 63),
        DataFolder = string.IsNullOrWhiteSpace(DataFolder) ? DefaultDataFolder : DataFolder,
    };
}
