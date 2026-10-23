using System.Text.Json.Serialization;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Settings;

public enum CaptureMode
{
    ActiveApplication,
    PrimaryMonitor,
    SelectedMonitors,
    AllMonitors,
}

/// <summary>Which desktop's look the windows take.</summary>
public enum ThemeStyle
{
    /// <summary>The desktop the app runs on (Windows; KDE's colour scheme; Adwaita on GTK desktops; Breeze elsewhere).</summary>
    Automatic,
    Windows,
    Kde,
    Gtk,
}

public enum ThemeMode
{
    /// <summary>Light or dark as the desktop is set.</summary>
    Automatic,
    Light,
    Dark,
}

public sealed record WorklapseSettings
{
    public static string DefaultDataFolder { get; } =
        // DoNotVerify: on Linux the folder (~/.local/share) may not exist yet, and without it the path would be empty,
        // leaving a relative "Worklapse" (in whatever folder Worklapse was started from).
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Worklapse");

    public CaptureMode CaptureMode { get; init; } = CaptureMode.AllMonitors;

    /// <summary>
    /// The monitors captured in <see cref="CaptureMode.SelectedMonitors"/> mode, by
    /// <see cref="Platform.MonitorInfo.StableId"/> (device names change when monitors are plugged in or out).
    /// </summary>
    public IReadOnlyList<string> SelectedMonitors { get; init; } = [];

    public int IntervalSeconds { get; init; } = 10;

    /// <summary>
    /// How long a capture counts for when totalling time: until the next one, at most one and a half intervals (so a
    /// gap, such as a pause, isn't counted as recorded).
    /// </summary>
    [JsonIgnore]
    public TimeSpan MaxCaptureLength => TimeSpan.FromSeconds(IntervalSeconds * 1.5);

    /// <summary>Capturing stops after this long without keyboard or mouse input.</summary>
    public int IdleMinutes { get; init; } = 5;

    public bool PauseWhenLocked { get; init; } = true;

    /// <summary>Record as soon as Worklapse starts; otherwise it starts paused.</summary>
    public bool RecordAtStartup { get; init; } = true;

    /// <summary>A desktop notification when recording begins after Worklapse starts.</summary>
    public bool NotifyWhenRecordingStarts { get; init; } = true;

    /// <summary>A desktop notification, once per run, that Worklapse keeps running in the tray when its window closes.</summary>
    public bool NotifyWhenInTray { get; init; } = true;

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
    /// When each day begins, to the minute (<see cref="History.LocalDay"/>): later than midnight puts the early hours of a
    /// late night in the day before, in every view.
    /// </summary>
    public TimeOnly DayStart { get; init; }

    /// <summary>
    /// Draw the windows with the GPU instead of the CPU. Set in Settings (General); takes effect at the
    /// next start. Off by default: GPU rendering measured ~80 MB more while
    /// recording and ~450 MB more while reviewing four monitors, for no CPU saving in a normal-sized window.
    /// It may be smoother with Day review maximized on a very large or high-DPI screen.
    /// </summary>
    public bool UseGpuRendering { get; init; }

    /// <summary>
    /// Day review shows only the window that had focus at each capture (where it was recorded), centred, instead of
    /// the monitors as they were arranged. Changed from Day review, which remembers it here.
    /// </summary>
    public bool ReviewFocusOnApplication { get; init; }

    /// <summary>The width of Day review's side panel, as last dragged (device-independent pixels).</summary>
    public double ReviewPanelWidth { get; init; } = DefaultReviewPanelWidth;

    public const double DefaultReviewPanelWidth = 440, MinReviewPanelWidth = 400, MaxReviewPanelWidth = 720;

    // Export Timelapse's choices, as last used.

    /// <summary>Captures shown per second of an exported timelapse.</summary>
    public int TimelapseFramesPerSecond { get; init; } = 30;

    /// <summary>The height of the largest video an exported timelapse fits in (16:9).</summary>
    public int TimelapseHeight { get; init; } = 1080;

    /// <summary>An exported timelapse shows the time of day.</summary>
    public bool TimelapseClock { get; init; } = true;

    /// <summary>An exported timelapse keeps idle time.</summary>
    public bool TimelapseIncludeIdle { get; init; }

    /// <summary>
    /// The look of the windows: the desktop's own by default, or another desktop's (which also lets each be tried on
    /// one machine).
    /// </summary>
    public ThemeStyle ThemeStyle { get; init; }

    public ThemeMode ThemeMode { get; init; }

    /// <summary>Suggest projects for time without one (docs/SUGGESTIONS.md).</summary>
    public bool SuggestProjects { get; init; } = true;

    /// <summary>
    /// Read the text in the active window while recording, for suggestions (only the words are kept). Apps, sites and
    /// projects can each opt out.
    /// </summary>
    public bool ReadScreenText { get; init; } = true;

    /// <summary>Time away inside a suggested stretch shorter than this is part of it, no question asked.</summary>
    public int GlanceMinutes { get; init; } = 2;

    /// <summary>Time away longer than this isn't offered as a break.</summary>
    public int SplitMinutes { get; init; } = 30;

    /// <summary>How sure the classifier must be of a stretch, in percent, for it to be offered.</summary>
    public int MinimumCertainty { get; init; } = 51;

    /// <summary>Threads reading the screens of past days (Read Past Days), each a core's worth of CPU.</summary>
    public int PastReadingThreads { get; init; } = DefaultPastReadingThreads;

    public const int DefaultPastReadingThreads = 4;

    /// <summary>Read Past Days was started and hasn't finished or been stopped: it goes on when Worklapse starts.</summary>
    public bool PastReadingRunning { get; init; }

    /// <summary>
    /// What the last finished Read Past Days covered (up to when it started); afterwards, captures from then on that the
    /// recorder didn't get to read are caught up. Null until one finishes.
    /// </summary>
    public DateTimeOffset? PastReadingReadUpTo { get; init; }

    /// <summary>Whether the recorder reads the active window's text.</summary>
    [JsonIgnore]
    public bool ReadsScreen => SuggestProjects && ReadScreenText;

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
        ThemeStyle = Enum.IsDefined(ThemeStyle) ? ThemeStyle : ThemeStyle.Automatic,
        ThemeMode = Enum.IsDefined(ThemeMode) ? ThemeMode : ThemeMode.Automatic,
        SelectedMonitors = SelectedMonitors ?? [], // "null" in a hand-edited file
        IntervalSeconds = Math.Clamp(IntervalSeconds, 2, 300),
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120),
        Quality = Math.Clamp(Quality, 0, 63),
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        WebPQuality = Math.Clamp(WebPQuality, 0, 100),
        GlanceMinutes = Math.Clamp(GlanceMinutes, 0, 30),
        SplitMinutes = Math.Clamp(SplitMinutes, Math.Clamp(GlanceMinutes, 0, 30), 240),
        MinimumCertainty = Math.Clamp(MinimumCertainty, 0, 99),
        PastReadingThreads = Math.Clamp(PastReadingThreads, 1, Environment.ProcessorCount),
        ReviewPanelWidth = double.IsFinite(ReviewPanelWidth)
            ? Math.Clamp(ReviewPanelWidth, MinReviewPanelWidth, MaxReviewPanelWidth) : DefaultReviewPanelWidth,
        TimelapseFramesPerSecond = Math.Clamp(TimelapseFramesPerSecond, 1, 120),
        TimelapseHeight = Math.Clamp(TimelapseHeight, 240, 4320),
        DataFolder = string.IsNullOrWhiteSpace(DataFolder) ? DefaultDataFolder : DataFolder,
        DayStart = new TimeOnly(DayStart.Hour, DayStart.Minute), // seconds in a hand-edited file
    };
}
