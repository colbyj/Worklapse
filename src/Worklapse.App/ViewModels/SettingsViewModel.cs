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

    public SettingsViewModel(SettingsStore store, IReadOnlyList<MonitorInfo> monitors)
    {
        _store = store;
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

        Load(store.Current);
        PropertyChanged += OnAnyPropertyChanged;

        Programs = new ProgramsViewModel(TimeSpan.FromSeconds(store.Current.IntervalSeconds));
        Programs.Changed += MarkDirty;
        _ = Programs.LoadAsync(store.Current.DataFolder);
    }

    public ObservableCollection<MonitorOption> Monitors { get; } = [];

    /// <summary>The Programs page; its marks are kept in the history in the data folder.</summary>
    public ProgramsViewModel Programs { get; }

    [ObservableProperty] private bool _isActiveApplication;
    [ObservableProperty] private bool _isPrimaryMonitor;
    [ObservableProperty] private bool _isSelectedMonitors;
    [ObservableProperty] private bool _isAllMonitors;
    [ObservableProperty] private decimal? _intervalSeconds;
    [ObservableProperty] private decimal? _idleMinutes;
    [ObservableProperty] private bool _pauseWhenLocked;

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
    [NotifyPropertyChangedFor(nameof(HasDataFolder), nameof(CanApply), nameof(DataFolderExists))]
    private string _dataFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _isDirty;

    public bool HasDataFolder => !string.IsNullOrWhiteSpace(DataFolder);

    /// <summary>The folder in the box can be opened (a new one is only made once recording starts there).</summary>
    public bool DataFolderExists => HasDataFolder && Directory.Exists(DataFolder.Trim());
    public bool CanApply => IsDirty && HasDataFolder;

    public void UseDefaultDataFolder() => DataFolder = WorklapseSettings.DefaultDataFolder;

    public bool QualityWarning => IsVideo && Quality >= QualityWarningThreshold;
    public string QualityText => ((int)Quality).ToString();
    public string JpegQualityText => ((int)JpegQuality).ToString();
    public string WebPQualityText => ((int)WebPQuality).ToString();

    // Settings the dialog doesn't show (set in the settings file) are kept as they are.
    public void RestoreDefaults() => Load(new WorklapseSettings { UseGpuRendering = _store.Current.UseGpuRendering });

    public void Apply()
    {
        // The marks go to the history they were read from, before a change of folder takes effect.
        var folder = _store.Current.DataFolder;
        Programs.Apply(folder);
        _store.Save(ToSettings());
        IsDirty = false;
        if (!string.Equals(_store.Current.DataFolder, folder, StringComparison.OrdinalIgnoreCase))
            _ = Programs.LoadAsync(_store.Current.DataFolder);
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
        Format = IsJpeg ? FrameFormat.Jpeg : IsWebP ? FrameFormat.WebP : FrameFormat.Video,
        Quality = (int)Quality,
        JpegQuality = (int)JpegQuality,
        WebPQuality = (int)WebPQuality,
        DataFolder = DataFolder.Trim(),
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
        _loading = false;
        // Defaults don't touch the Programs page (its marks are history, not settings); its changes still count.
        IsDirty = !settings.Equals(_store.Current) || !settings.SelectedMonitors.SequenceEqual(_store.Current.SelectedMonitors)
            || Programs is { IsDirty: true };
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsDirty))
            MarkDirty();
    }

    private void MarkDirty()
    {
        if (!_loading)
            IsDirty = true;
    }
}
