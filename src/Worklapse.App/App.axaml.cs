using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Worklapse.App.Theming;
using Worklapse.App.ViewModels;
using Worklapse.App.Views;
using Worklapse.Capture.Windows;
using Worklapse.Core.History;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.App;

public partial class App : Application
{
    private SettingsStore? _settings;
    private PlatformServices? _platform;
    private Recorder? _recorder;
    private volatile FrameSink? _sink; // the recorder's current one, created on its thread
    private RecordingViewModel? _recording;
    private MainWindow? _mainWindow;
    private NativeMenuItem? _pauseItem;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The desktop's own UI font size: 9 pt Segoe UI on Windows (the resources' 12 px), 10 pt Noto Sans on KDE.
        if (!OperatingSystem.IsWindows())
            Resources["ControlContentThemeFontSize"] = 13.0;
        ThemeManager.Start(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing windows hides them; the app keeps recording until Quit is chosen from the tray.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            StartOrReportSettingsError(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Starts recording, unless the settings file can't be read: then the developer chooses between fixing
    /// it by hand (quit) and starting from defaults, rather than Worklapse quietly recording somewhere else.
    /// </summary>
    private void StartOrReportSettingsError(IClassicDesktopStyleApplicationLifetime desktop)
    {
        SettingsStore settings;
        try
        {
            settings = new SettingsStore();
        }
        catch (SettingsFileException ex)
        {
            var dialog = new SettingsErrorWindow(ex);
            dialog.Closed += (_, _) =>
            {
                if (dialog.StartWithDefaults)
                    StartOrReportSettingsError(desktop);
                else
                    desktop.Shutdown(1);
            };
            dialog.Show();
            return;
        }
        Start(desktop, settings);
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop, SettingsStore settingsStore)
    {
        _settings = settingsStore;
        ThemeManager.Use(_settings.Current);
        _settings.Changed += (_, settings) => Dispatcher.UIThread.Post(() => ThemeManager.Use(settings));
        _platform = CreatePlatform();
        _recorder = new Recorder(_settings, _platform, CreateSink);

        _desktop = desktop;
        var recording = _recording = new RecordingViewModel(_recorder, _settings);
        recording.PropertyChanged += (_, _) => UpdateTray();

        _recorder.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => recording.Update(s));
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => recording.Update(_recorder.Status));

        CreateTrayIcon(desktop);
        _recorder.Start();
        ShowMainWindow(MainView.Overview);
    }

    /// <summary>
    /// The recorder's sink, made on the recorder's thread (again after an error or a change of data folder). Its
    /// batch encoder also removes deleted captures' video and images; the main window says when it can't.
    /// </summary>
    private FrameSink CreateSink(RecordingStore store)
    {
        var settings = _settings!;
        var sink = new FrameSink(store, () => settings.Current.FrameStorage());
        var encoder = sink.Encoder;
        // Reads the encoder's current state rather than taking it from the event, so subscribing after the
        // encoder has started (it begins working in its constructor) loses nothing.
        void Report() => Dispatcher.UIThread.Post(() =>
        {
            if (_sink == sink)
                _recording!.DeletedContentLeft = encoder.DeletedContentLeft;
        });
        encoder.DeletedContentLeftChanged += (_, _) => Report();
        _sink = sink;
        Report();
        return sink;
    }

    private static PlatformServices CreatePlatform()
    {
        if (OperatingSystem.IsWindows())
            return WindowsPlatform.Create();
        throw new PlatformNotSupportedException("Screen capture is only implemented for Windows so far.");
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _pauseItem = new NativeMenuItem("Pause Recording");
        _pauseItem.Click += (_, _) => _recording!.TogglePauseCommand.Execute(null);

        var showItem = new NativeMenuItem("Show Worklapse");
        showItem.Click += (_, _) => ShowMainWindow();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += async (_, _) => await ShowSettingsAsync();

        var quitItem = new NativeMenuItem("Quit Worklapse");
        quitItem.Click += async (_, _) => await QuitAsync();

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Worklapse/Assets/worklapse.ico"))),
            ToolTipText = "Worklapse",
            Menu = new NativeMenu { Items = { showItem, _pauseItem, settingsItem, new NativeMenuItemSeparator(), quitItem } },
        };
        _tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, [_tray]);
        UpdateTray();
    }

    /// <summary>The tray says whether Worklapse is recording, and offers to pause or resume.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _recording is null)
            return;
        _tray.ToolTipText = _recording.TrayText;
        _pauseItem!.Header = _recording.PauseText;
    }

    /// <summary>
    /// Shows the main window, making it if it was closed (closing it lets its views and their memory go; recording
    /// carries on).
    /// </summary>
    private MainWindow ShowMainWindow(MainView? view = null)
    {
        if (_mainWindow is null)
        {
            var viewModel = new MainViewModel(_recording!, CreateDayReview, ShowSettingsAsync, _settings!, view ?? MainView.Overview);
            _mainWindow = new MainWindow(viewModel, _settings!, QuitAsync);
            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow = null;
                // Reviewing leaves a large heap behind (the views, frame buffers).
                ReleaseMemory();
            };
            _mainWindow.Show();
        }
        else if (view is { } shown)
        {
            ((MainViewModel)_mainWindow.DataContext!).View = shown;
        }
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        return _mainWindow;
    }

    /// <summary>Day review on a day (or the latest recorded), reading the history in the data folder the settings name now.</summary>
    private DayReviewViewModel CreateDayReview(DateOnly? day)
    {
        var settings = _settings!.Current;
        var history = new HistoryStore(WorklapseDatabase.Open(settings.DataFolder));
        // The recorder's batch encoder (open even while paused or locked) removes the deleted video and images from
        // disk. Frames in the segments still being recorded go once those are closed and encoded.
        return new DayReviewViewModel(history, _settings, result =>
        {
            _sink?.Encoder.RequestReclaim();
            if (result.StagedSegmentsAffected)
                _recorder!.CloseSegmentsSoon();
        }, day);
    }

    private async Task ShowSettingsAsync()
    {
        var monitors = _platform!.Screen.GetMonitors();
        // The recording preview captures the primary monitor with a source of its own.
        var sampler = OperatingSystem.IsWindows() ? new PrimaryScreenSampler(new GdiScreenSource()) : null;
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(_settings!, monitors, sampler) };
        var owner = ShowMainWindow();
        await dialog.ShowDialog(owner);
        // Projects and links may have changed (they live in the history, not the settings file).
        owner.RefreshView();
        // The recording preview held a few screens of pixels.
        ReleaseMemory();
    }

    /// <summary>
    /// Collects once a window is torn down and gives the memory back to the OS. Otherwise the GC keeps what the window
    /// left behind committed for a long time, since recording allocates too little to trigger collections.
    /// </summary>
    private static void ReleaseMemory() => Dispatcher.UIThread.Post(
        () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true),
        DispatcherPriority.Background);

    private async Task QuitAsync()
    {
        _mainWindow?.Close();
        if (_recorder is not null)
            await _recorder.DisposeAsync();
        _platform?.Dispose();
        _desktop?.Shutdown();
    }
}
