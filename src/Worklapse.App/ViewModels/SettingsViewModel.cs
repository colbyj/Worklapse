using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Worklapse.Core.Platform;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

public sealed partial class MonitorOption : ObservableObject
{
    public required string StableId { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>From this quantizer up, small text was seen garbled or showing stale content.</summary>
    public const int QualityWarningThreshold = 48;

    private readonly SettingsStore _store;
    private bool _loading;
    private IReadOnlyList<string> _unpluggedSelected = []; // selected monitors not attached now: kept as they are

    /// <param name="sampler">Captures the primary monitor for the recording preview, which takes it over; null for none.</param>
    public SettingsViewModel(SettingsStore store, IReadOnlyList<MonitorInfo> monitors, PrimaryScreenSampler? sampler = null)
    {
        _store = store;
        // Captured before the dialog shows, so it isn't in the picture.
        if (sampler?.Capture() is { } screen)
            Preview = new RecordingPreviewViewModel(sampler, screen);
        else
            sampler?.Dispose();
        var number = 1;
        foreach (var monitor in monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X))
        {
            var option = new MonitorOption
            {
                StableId = monitor.StableId,
                Label = $"{number++} — {monitor.Bounds.Width} × {monitor.Bounds.Height}{(monitor.IsPrimary ? " (primary)" : "")}",
            };
            option.PropertyChanged += (_, _) => MarkDirty();
            Monitors.Add(option);
        }

        Projects = new ProjectsViewModel();
        Programs = new ProgramsViewModel(TimeSpan.FromSeconds(store.Current.IntervalSeconds)) { ProjectChoices = Projects.ProjectChoices };
        Programs.Changed += () =>
        {
            MarkDirty();
            Projects.RefreshLinked();
        };
        Projects.Changed += () =>
        {
            MarkDirty();
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanConfirm));
        };
        Projects.ProjectDeleted += Programs.Unlink;
        Projects.LinkedNames = Programs.LinkedTo;

        Load(store.Current);
        PropertyChanged += OnAnyPropertyChanged;
        _ = LoadHistoryAsync(store.Current.DataFolder);
    }

    // Tabs, in order.
    public const int GeneralTab = 0, ApplicationsTab = 1, ProjectsTab = 2, AppearanceTab = 3;

    public ObservableCollection<MonitorOption> Monitors { get; } = [];

    /// <summary>The Applications page; its marks and links are kept in the history in the data folder.</summary>
    public ProgramsViewModel Programs { get; }

    /// <summary>The Projects page; kept in the history in the data folder.</summary>
    public ProjectsViewModel Projects { get; }

    /// <summary>The tab shown; Defaults only applies to (and only shows on) General and Appearance.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaults))]
    private int _selectedTab;

    public bool HasDefaults => SelectedTab is GeneralTab or AppearanceTab;

    /// <summary>The chosen format and quality applied to the screen; null when the screen couldn't be captured.</summary>
    public RecordingPreviewViewModel? Preview { get; }

    public bool HasPreview => Preview is not null;

    /// <summary>The format and quality chosen in the dialog, applied or not.</summary>
    public (FrameFormat Format, int Quality) ChosenStorage =>
        IsJpeg ? (FrameFormat.Jpeg, (int)JpegQuality) : IsWebP ? (FrameFormat.WebP, (int)WebPQuality) : (FrameFormat.Video, (int)Quality);

    /// <summary>The capture interval chosen in the dialog, applied or not.</summary>
    public TimeSpan ChosenInterval => TimeSpan.FromSeconds(Math.Clamp((int)(IntervalSeconds ?? 10), 2, 300));

    [ObservableProperty] private bool _isActiveApplication;
    [ObservableProperty] private bool _isPrimaryMonitor;
    [ObservableProperty] private bool _isSelectedMonitors;
    [ObservableProperty] private bool _isAllMonitors;
    [ObservableProperty] private decimal? _intervalSeconds;
    [ObservableProperty] private decimal? _idleMinutes;
    [ObservableProperty] private bool _pauseWhenLocked;

    // The Appearance page: indexes into its lists, in the order of ThemeStyle and ThemeMode.
    [ObservableProperty] private int _themeStyleIndex;
    [ObservableProperty] private int _themeModeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityWarning))]
    private bool _isVideo;

    [ObservableProperty] private bool _isJpeg;
    [ObservableProperty] private bool _isWebP;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityWarning), nameof(QualityText))]
    private double _quality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JpegQualityText))]
    private double _jpegQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebPQualityText))]
    private double _webPQuality;

    // An empty folder used to mean the default one silently; now it has to be chosen (Browse or Default).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataFolder), nameof(CanApply), nameof(CanConfirm), nameof(DataFolderExists))]
    private string _dataFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _isDirty;

    public bool HasDataFolder => !string.IsNullOrWhiteSpace(DataFolder);

    /// <summary>The folder in the box can be opened (a new one is only made once recording starts there).</summary>
    public bool DataFolderExists => HasDataFolder && Directory.Exists(DataFolder.Trim());
    public bool CanApply => IsDirty && CanConfirm;

    /// <summary>OK can be pressed: there's a history folder, and every project and category has a name.</summary>
    public bool CanConfirm => HasDataFolder && Projects.IsValid;

    public void UseDefaultDataFolder() => DataFolder = WorklapseSettings.DefaultDataFolder;

    public bool QualityWarning => IsVideo && Quality >= QualityWarningThreshold;
    public string QualityText => ((int)Quality).ToString();
    public string JpegQualityText => ((int)JpegQuality).ToString();
    public string WebPQualityText => ((int)WebPQuality).ToString();

    /// <summary>
    /// Puts the shown tab's settings back to their defaults: everything on General (the look kept), or the look on
    /// Appearance. Settings the dialog doesn't show (set in the settings file) are kept as they are.
    /// </summary>
    public void RestoreDefaults()
    {
        var defaults = new WorklapseSettings { UseGpuRendering = _store.Current.UseGpuRendering };
        if (SelectedTab == AppearanceTab)
        {
            ThemeStyleIndex = (int)defaults.ThemeStyle;
            ThemeModeIndex = (int)defaults.ThemeMode;
        }
        else if (SelectedTab == GeneralTab)
        {
            Load(defaults with { ThemeStyle = (ThemeStyle)ThemeStyleIndex, ThemeMode = (ThemeMode)ThemeModeIndex });
        }
    }

    public void Apply()
    {
        // Projects and marks go to the history they were read from, before a change of folder takes effect. Projects
        // first, so links to projects made in the dialog have ids to point at.
        var folder = _store.Current.DataFolder;
        Projects.Apply(folder);
        Programs.Apply(folder);
        _store.Save(ToSettings());
        IsDirty = false;
        if (!string.Equals(_store.Current.DataFolder, folder, StringComparison.OrdinalIgnoreCase))
            _ = LoadHistoryAsync(_store.Current.DataFolder);
    }

    /// <summary>Reads the Projects page, then the Applications page (whose links point at its projects).</summary>
    private async Task LoadHistoryAsync(string dataFolder)
    {
        await Projects.LoadAsync(dataFolder);
        await Programs.LoadAsync(dataFolder, Projects.Resolve);
        Projects.RefreshLinked();
    }

    private WorklapseSettings ToSettings() => _store.Current with
    {
        CaptureMode = IsActiveApplication ? CaptureMode.ActiveApplication
            : IsPrimaryMonitor ? CaptureMode.PrimaryMonitor
            : IsSelectedMonitors ? CaptureMode.SelectedMonitors
            : CaptureMode.AllMonitors,
        SelectedMonitors = [.. Monitors.Where(m => m.IsChecked).Select(m => m.StableId), .. _unpluggedSelected],
        IntervalSeconds = (int)(IntervalSeconds ?? 10),
        IdleMinutes = (int)(IdleMinutes ?? 5),
        PauseWhenLocked = PauseWhenLocked,
        Format = ChosenStorage.Format,
        Quality = (int)Quality,
        JpegQuality = (int)JpegQuality,
        WebPQuality = (int)WebPQuality,
        DataFolder = DataFolder.Trim(),
        ThemeStyle = (ThemeStyle)ThemeStyleIndex,
        ThemeMode = (ThemeMode)ThemeModeIndex,
    };

    private void Load(WorklapseSettings settings)
    {
        _loading = true;
        IsActiveApplication = settings.CaptureMode == CaptureMode.ActiveApplication;
        IsPrimaryMonitor = settings.CaptureMode == CaptureMode.PrimaryMonitor;
        IsSelectedMonitors = settings.CaptureMode == CaptureMode.SelectedMonitors;
        IsAllMonitors = settings.CaptureMode == CaptureMode.AllMonitors;
        foreach (var monitor in Monitors)
            monitor.IsChecked = settings.SelectedMonitors.Contains(monitor.StableId, StringComparer.OrdinalIgnoreCase);
        _unpluggedSelected = settings.SelectedMonitors
            .Where(id => !Monitors.Any(m => string.Equals(m.StableId, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        IntervalSeconds = settings.IntervalSeconds;
        IdleMinutes = settings.IdleMinutes;
        PauseWhenLocked = settings.PauseWhenLocked;
        IsVideo = settings.Format == FrameFormat.Video;
        IsJpeg = settings.Format == FrameFormat.Jpeg;
        IsWebP = settings.Format == FrameFormat.WebP;
        Quality = settings.Quality;
        JpegQuality = settings.JpegQuality;
        WebPQuality = settings.WebPQuality;
        DataFolder = settings.DataFolder;
        ThemeStyleIndex = (int)settings.ThemeStyle;
        ThemeModeIndex = (int)settings.ThemeMode;
        _loading = false;
        // Defaults don't touch the Applications and Projects pages (they're history, not settings); their changes still count.
        IsDirty = !settings.Equals(_store.Current) || !settings.SelectedMonitors.SequenceEqual(_store.Current.SelectedMonitors)
            || Programs is { IsDirty: true } || Projects is { IsDirty: true };
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // CanApply follows IsDirty: counting it as a change kept the dialog dirty right after Apply.
        if (e.PropertyName is not (nameof(IsDirty) or nameof(CanApply) or nameof(CanConfirm) or nameof(SelectedTab) or nameof(HasDefaults)))
            MarkDirty();
        if (e.PropertyName is nameof(IsVideo) or nameof(IsJpeg) or nameof(IsWebP) or nameof(Quality) or nameof(JpegQuality) or nameof(WebPQuality)
            or nameof(IntervalSeconds))
            Preview?.Request(ChosenStorage.Format, ChosenStorage.Quality, ChosenInterval);
    }

    private void MarkDirty()
    {
        if (!_loading)
            IsDirty = true;
    }
}
