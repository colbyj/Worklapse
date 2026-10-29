using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using Worklapse.Core.Platform;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;
using Worklapse.Video;

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
    private readonly BackgroundScreenReading? _pastReading;
    private readonly Action? _showPastReading;
    private bool _loading;
    private IReadOnlyList<string> _unpluggedSelected = []; // selected monitors not attached now: kept as they are
    private bool _startAtLoginApplied; // as the OS has it

    /// <param name="sampler">Captures the primary monitor for the recording preview, which takes it over; null for none.</param>
    /// <param name="knowsPrimaryMonitor">
    /// Whether the desktop says which monitor is primary (KDE Plasma doesn't tell Worklapse); without it there's no
    /// "Primary monitor" choice.
    /// </param>
    /// <param name="pastReading">Read Past Days; null where the platform can't read screens.</param>
    /// <param name="showPastReading">Shows Read Past Days' progress window.</param>
    public SettingsViewModel(SettingsStore store, IReadOnlyList<MonitorInfo> monitors, PrimaryScreenSampler? sampler = null,
        bool knowsPrimaryMonitor = true, BackgroundScreenReading? pastReading = null, Action? showPastReading = null)
    {
        _store = store;
        _pastReading = pastReading;
        _showPastReading = showPastReading;
        if (pastReading is not null)
        {
            pastReading.Changed += OnPastReadingChanged;
            OnPastReadingChanged(null, EventArgs.Empty);
        }
        // Still shown if it's the setting (from another desktop's settings file), so the dialog shows what is set.
        ShowsPrimaryMonitor = knowsPrimaryMonitor || store.Current.CaptureMode == CaptureMode.PrimaryMonitor;
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

        Projects = new ProjectsViewModel(store.Current.MaxCaptureLength);
        Programs = new ProgramsViewModel(store.Current.MaxCaptureLength) { ProjectChoices = Projects.ProjectChoices };
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

        // Held by the OS, not the settings file: read once, written on Apply.
        StartAtLogin = _startAtLoginApplied = LoginStartup.IsSupported && LoginStartup.IsEnabled;
        Load(store.Current);
        PropertyChanged += OnAnyPropertyChanged;
        _ = LoadHistoryAsync(store.Current.DataFolder);
    }

    // Tabs, in order.
    public const int RecordingTab = 0, GeneralTab = 1, ApplicationsTab = 2, ProjectsTab = 3, SuggestionsTab = 4;

    public ObservableCollection<MonitorOption> Monitors { get; } = [];

    /// <summary>The Applications page; its marks and links are kept in the history in the data folder.</summary>
    public ProgramsViewModel Programs { get; }

    /// <summary>The Projects page; kept in the history in the data folder.</summary>
    public ProjectsViewModel Projects { get; }

    /// <summary>The tab shown; Defaults only applies to (and only shows on) Recording and General.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaults))]
    private int _selectedTab;

    public bool HasDefaults => SelectedTab is RecordingTab or GeneralTab;

    /// <summary>
    /// Starts reading the screens of every past capture without words yet (what training learns from first), in the
    /// background (it goes on after the dialog closes), and shows its progress; while it runs, shows the progress.
    /// </summary>
    [RelayCommand]
    private void ReadPastDays()
    {
        if (_pastReading is null)
            return;
        if (!IsReadingPastDays)
            _pastReading.Start();
        _showPastReading?.Invoke();
    }

    /// <summary>Stops following Read Past Days (the dialog is closing); reading carries on.</summary>
    public void Detach()
    {
        if (_pastReading is not null)
            _pastReading.Changed -= OnPastReadingChanged;
    }

    private void OnPastReadingChanged(object? sender, EventArgs e)
    {
        var state = _pastReading!.State;
        IsReadingPastDays = state == PastReadingState.Running;
        PastDaysText = state switch
        {
            PastReadingState.Running => "Reading in the background",
            PastReadingState.Done => "Done",
            PastReadingState.Stopped => "Stopped; what was read is kept",
            PastReadingState.Failed => "Couldn't read the history: " + _pastReading.Problem,
            _ => "",
        };
    }

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

    public bool ShowsPrimaryMonitor { get; }

    [ObservableProperty] private bool _isSelectedMonitors;
    [ObservableProperty] private bool _isAllMonitors;
    [ObservableProperty] private decimal? _intervalSeconds;
    [ObservableProperty] private decimal? _idleMinutes;
    [ObservableProperty] private bool _pauseWhenLocked;
    [ObservableProperty] private bool _recordAtStartup;
    [ObservableProperty] private bool _startAtLogin;

    /// <summary>Hidden where Worklapse can't be started at login (see <see cref="LoginStartup.IsSupported"/>).</summary>
    public bool CanStartAtLogin { get; } = LoginStartup.IsSupported;

    public string StartAtLoginText { get; } = OperatingSystem.IsWindows() ? "Start with Windows" : "Start when I log in";
    [ObservableProperty] private bool _notifyWhenRecordingStarts;
    [ObservableProperty] private bool _notifyWhenInTray;

    // The Suggestions page.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReadPastDays))]
    private bool _suggestProjects;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReadPastDays))]
    private bool _readScreenText;

    [ObservableProperty] private decimal? _glanceMinutes;
    [ObservableProperty] private decimal? _splitMinutes;
    [ObservableProperty] private decimal? _minimumCertainty;

    /// <summary>What suggestions learn from: the time set by hand in the history.</summary>
    [ObservableProperty] private string _learnsFromText = "";

    /// <summary>The screens of past days can be read here (Windows' OCR; none on Linux yet).</summary>
    public bool CanReadScreens => _pastReading is not null;

    /// <summary>Starting needs reading on, applied as well as ticked; a run under way can always be shown.</summary>
    public bool CanReadPastDays => CanReadScreens && (IsReadingPastDays || (SuggestProjects && ReadScreenText && _store.Current.ReadsScreen));

    public string ReadPastDaysText => IsReadingPastDays ? "Show Progress" : "Read Past Days";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReadPastDays), nameof(ReadPastDaysText))]
    private bool _isReadingPastDays;

    [ObservableProperty] private string _pastDaysText = "";

    // The General page's day start, typed as a 24-hour time.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayStartIsValid), nameof(CanApply), nameof(CanConfirm))]
    private string _dayStartText = "";

    /// <summary>The day start typed, if it reads as a time.</summary>
    private TimeOnly? ChosenDayStart => ProjectTimes.ParseClock(DayStartText);

    public bool DayStartIsValid => ChosenDayStart is not null;

    // The General page's theme: indexes into its lists, in the order of ThemeStyle and ThemeMode.
    [ObservableProperty] private int _themeStyleIndex;
    [ObservableProperty] private int _themeModeIndex;

    /// <summary>Read once at startup (Program.BuildAvaloniaApp), so a change applies at the next start.</summary>
    [ObservableProperty] private bool _useGpuRendering;

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

    /// <summary>OK can be pressed: there's a history folder, the day start reads, and every project and category has a name.</summary>
    public bool CanConfirm => HasDataFolder && DayStartIsValid && Projects.IsValid;

    public void UseDefaultDataFolder() => DataFolder = WorklapseSettings.DefaultDataFolder;

    public bool QualityWarning => IsVideo && Quality >= QualityWarningThreshold;
    public string QualityText => ((int)Quality).ToString();
    public string JpegQualityText => ((int)JpegQuality).ToString();
    public string WebPQualityText => ((int)WebPQuality).ToString();

    /// <summary>
    /// Puts the shown tab's settings back to their defaults: what is recorded and how on Recording; startup,
    /// notifications, the day start, the look and GPU drawing on General. Settings the dialog doesn't show (set in the
    /// settings file) are kept.
    /// </summary>
    public void RestoreDefaults()
    {
        var defaults = new WorklapseSettings();
        if (SelectedTab == GeneralTab)
        {
            UseGpuRendering = defaults.UseGpuRendering;
            StartAtLogin = false;
            RecordAtStartup = defaults.RecordAtStartup;
            NotifyWhenRecordingStarts = defaults.NotifyWhenRecordingStarts;
            NotifyWhenInTray = defaults.NotifyWhenInTray;
            DayStartText = ClockText(defaults.DayStart);
            ThemeStyleIndex = (int)defaults.ThemeStyle;
            ThemeModeIndex = (int)defaults.ThemeMode;
        }
        else if (SelectedTab == RecordingTab)
        {
            Load(defaults with
            {
                RecordAtStartup = RecordAtStartup,
                NotifyWhenRecordingStarts = NotifyWhenRecordingStarts,
                NotifyWhenInTray = NotifyWhenInTray,
                DayStart = ChosenDayStart ?? _store.Current.DayStart,
                ThemeStyle = (ThemeStyle)ThemeStyleIndex,
                ThemeMode = (ThemeMode)ThemeModeIndex,
                UseGpuRendering = UseGpuRendering,
                SuggestProjects = SuggestProjects,
                ReadScreenText = ReadScreenText,
                GlanceMinutes = (int)(GlanceMinutes ?? 2),
                SplitMinutes = (int)(SplitMinutes ?? 30),
                MinimumCertainty = (int)(MinimumCertainty ?? 51),
            });
        }
    }

    public void Apply()
    {
        // Projects and marks go to the history they were read from, before a change of folder takes effect. Projects
        // first, so links to projects made in the dialog have ids to point at.
        var (folder, dayStart) = (_store.Current.DataFolder, _store.Current.DayStart);
        Projects.Apply(folder);
        Programs.Apply(folder);
        if (StartAtLogin != _startAtLoginApplied)
        {
            if (StartAtLogin)
                LoginStartup.Enable();
            else
                LoginStartup.Disable();
            _startAtLoginApplied = StartAtLogin;
        }
        _store.Save(ToSettings());
        IsDirty = false;
        OnPropertyChanged(nameof(CanReadPastDays)); // it follows the applied reading setting too
        if (!string.Equals(_store.Current.DataFolder, folder, StringComparison.OrdinalIgnoreCase))
            _ = LoadHistoryAsync(_store.Current.DataFolder);
    }

    /// <summary>The settings in use, whatever the dialog shows and hasn't applied (the history folder in its box).</summary>
    public WorklapseSettings AppliedSettings => _store.Current;

    /// <summary>On Windows with TimeSnapper installed: its database can only be read with its own engine.</summary>
    public bool CanImportTimeSnapper { get; } =
        OperatingSystem.IsWindows() && Directory.Exists(Worklapse.Core.Import.TimeSnapperSource.DefaultProgramFolder);

    /// <summary>
    /// TimeSnapper's history was imported: the Applications page lists its apps and sites and the on-task marks it
    /// brought, unless it has changes of its own not applied yet (they're kept; it shows the rest at the next opening).
    /// </summary>
    public async Task HistoryImportedAsync()
    {
        if (Programs.IsDirty)
            return;
        await Programs.LoadAsync(_store.Current.DataFolder, Projects.Resolve);
        Projects.RefreshLinked();
    }

    /// <summary>Reads the Projects page, then the Applications page (whose links point at its projects).</summary>
    private async Task LoadHistoryAsync(string dataFolder)
    {
        await Projects.LoadAsync(dataFolder);
        await Programs.LoadAsync(dataFolder, Projects.Resolve);
        Projects.RefreshLinked();
        try
        {
            LearnsFromText = await Task.Run(() => LearnsFrom(dataFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LearnsFromText = "";
        }
    }

    /// <summary>"Time you set by hand: 164 blocks over 18 days".</summary>
    private static string LearnsFrom(string dataFolder)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        var (blocks, days) = db.Connection.QuerySingle<(long Blocks, long Days)>("""
            SELECT count(*), count(DISTINCT date(start_ms / 1000, 'unixepoch', 'localtime')) FROM project_spans WHERE project_id IS NOT NULL;
            """);
        return blocks == 0 ? "Nothing yet: it learns from time you set to a project by hand"
            : $"Time you set by hand: {blocks:N0} {(blocks == 1 ? "block" : "blocks")} over {days:N0} {(days == 1 ? "day" : "days")}";
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
        RecordAtStartup = RecordAtStartup,
        NotifyWhenRecordingStarts = NotifyWhenRecordingStarts,
        NotifyWhenInTray = NotifyWhenInTray,
        Format = ChosenStorage.Format,
        Quality = (int)Quality,
        JpegQuality = (int)JpegQuality,
        WebPQuality = (int)WebPQuality,
        DataFolder = DataFolder.Trim(),
        DayStart = ChosenDayStart ?? _store.Current.DayStart,
        ThemeStyle = (ThemeStyle)ThemeStyleIndex,
        ThemeMode = (ThemeMode)ThemeModeIndex,
        UseGpuRendering = UseGpuRendering,
        SuggestProjects = SuggestProjects,
        ReadScreenText = ReadScreenText,
        GlanceMinutes = (int)(GlanceMinutes ?? 2),
        SplitMinutes = (int)(SplitMinutes ?? 30),
        MinimumCertainty = (int)(MinimumCertainty ?? 51),
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
        RecordAtStartup = settings.RecordAtStartup;
        NotifyWhenRecordingStarts = settings.NotifyWhenRecordingStarts;
        NotifyWhenInTray = settings.NotifyWhenInTray;
        IsVideo = settings.Format == FrameFormat.Video;
        IsJpeg = settings.Format == FrameFormat.Jpeg;
        IsWebP = settings.Format == FrameFormat.WebP;
        Quality = settings.Quality;
        JpegQuality = settings.JpegQuality;
        WebPQuality = settings.WebPQuality;
        DataFolder = settings.DataFolder;
        DayStartText = ClockText(settings.DayStart);
        ThemeStyleIndex = (int)settings.ThemeStyle;
        ThemeModeIndex = (int)settings.ThemeMode;
        UseGpuRendering = settings.UseGpuRendering;
        SuggestProjects = settings.SuggestProjects;
        ReadScreenText = settings.ReadScreenText;
        GlanceMinutes = settings.GlanceMinutes;
        SplitMinutes = settings.SplitMinutes;
        MinimumCertainty = settings.MinimumCertainty;
        _loading = false;
        // Defaults don't touch the Applications and Projects pages (they're history, not settings); their changes still count.
        IsDirty = !settings.Equals(_store.Current) || !settings.SelectedMonitors.SequenceEqual(_store.Current.SelectedMonitors)
            || Programs is { IsDirty: true } || Projects is { IsDirty: true } || StartAtLogin != _startAtLoginApplied;
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // CanApply follows IsDirty: counting it as a change kept the dialog dirty right after Apply.
        if (e.PropertyName is not (nameof(IsDirty) or nameof(CanApply) or nameof(CanConfirm) or nameof(SelectedTab) or nameof(HasDefaults)
            or nameof(LearnsFromText) or nameof(IsReadingPastDays) or nameof(PastDaysText) or nameof(CanReadPastDays)
            or nameof(ReadPastDaysText)))
            MarkDirty();
        if (e.PropertyName is nameof(IsVideo) or nameof(IsJpeg) or nameof(IsWebP) or nameof(Quality) or nameof(JpegQuality) or nameof(WebPQuality)
            or nameof(IntervalSeconds))
            Preview?.Request(ChosenStorage.Format, ChosenStorage.Quality, ChosenInterval);
    }

    private static string ClockText(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private void MarkDirty()
    {
        if (!_loading)
            IsDirty = true;
    }
}
