using Worklapse.Core.Storage;

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

    /// <summary>How captures are stored: AV1 video, or an image file (JPEG or WebP) per changed capture.</summary>
    public FrameFormat Format { get; init; } = FrameFormat.Video;

    /// <summary>AV1 quantizer (libaom cq-level, 0–63). Lower is sharper and larger.</summary>
    public int Quality { get; init; } = 40;

    /// <summary>JPEG quality, 1–100. Higher is sharper and larger. TimeSnapper used 15.</summary>
    public int JpegQuality { get; init; } = 15;

    /// <summary>WebP quality, 0–100. Higher is sharper and larger. Text read more clearly at 10 than JPEG's at 15.</summary>
    public int WebPQuality { get; init; } = 20;

    public string DataFolder { get; init; } = DefaultDataFolder;

    /// <summary>
    /// Draw the windows with the GPU instead of the CPU. Set in the settings file only; takes effect at the
    /// next start, and only on Windows so far. Off by default: GPU rendering measured ~80 MB more while
    /// recording and ~450 MB more while reviewing four monitors, for no CPU saving in a normal-sized window.
    /// It may be smoother with Day review maximized on a very large or high-DPI screen.
    /// </summary>
    public bool UseGpuRendering { get; init; }

    /// <summary>
    /// Day review shows only the window that had focus at each capture (where it was recorded), centred, instead of
    /// the monitors as they were arranged. Changed from Day review, which remembers it here.
    /// </summary>
    public bool ReviewFocusOnApplication { get; init; }

    /// <summary>How new frames are stored: the format, and that format's quality setting.</summary>
    public (FrameFormat Format, int Quality) FrameStorage() => Format switch
    {
        FrameFormat.Jpeg => (FrameFormat.Jpeg, JpegQuality),
        FrameFormat.WebP => (FrameFormat.WebP, WebPQuality),
        _ => (FrameFormat.Video, Quality),
    };

    public WorklapseSettings Normalized() => this with
    {
        Format = Enum.IsDefined(Format) ? Format : FrameFormat.Video, // a number in a hand-edited file
        SelectedMonitors = SelectedMonitors ?? [], // "null" in a hand-edited file
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 300),
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120),
        Quality = Math.Clamp(Quality, 0, 63),
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        WebPQuality = Math.Clamp(WebPQuality, 0, 100),
        DataFolder = string.IsNullOrWhiteSpace(DataFolder) ? DefaultDataFolder : DataFolder,
    };
}
