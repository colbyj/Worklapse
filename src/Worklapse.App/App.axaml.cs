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
    private StatusViewModel? _status;
    private StatusWindow? _statusWindow;
    private DayReviewWindow? _reviewWindow;
    private NativeMenuItem? _pauseItem;
    private bool _quitting;

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
        _platform = CreatePlatform();
        _recorder = new Recorder(_settings, _platform, CreateSink);

        var status = _status = new StatusViewModel(_recorder, _settings, ShowSettingsAsync, ShowDayReview);
        _statusWindow = new StatusWindow { DataContext = status };
        _statusWindow.Closing += (_, e) =>
        {
            if (_quitting)
                return;
            e.Cancel = true;
            _statusWindow.Hide();
        };

        _recorder.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            status.Update(s);
            if (_pauseItem is not null)
                _pauseItem.Header = _recorder.IsPaused ? "Resume Recording" : "Pause Recording";
        });
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => status.Update(_recorder.Status));

        CreateTrayIcon(desktop);
        _recorder.Start();
        _statusWindow.Show();
    }

    /// <summary>
    /// The recorder's sink, made on the recorder's thread (again after an error or a change of data folder). Its
    /// batch encoder also removes deleted captures' video and images; the status window says when it can't.
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
                _status!.DeletedContentLeft = encoder.DeletedContentLeft;
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
        _pauseItem.Click += (_, _) => _recorder!.IsPaused = !_recorder.IsPaused;

        var showItem = new NativeMenuItem("Show Status");
        showItem.Click += (_, _) => ShowStatusWindow();

        var reviewItem = new NativeMenuItem("Review Day…");
        reviewItem.Click += (_, _) => ShowDayReview();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += async (_, _) => await ShowSettingsAsync();

        var quitItem = new NativeMenuItem("Quit Worklapse");
        quitItem.Click += async (_, _) => await QuitAsync(desktop);

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Worklapse/Assets/worklapse.ico"))),
            ToolTipText = "Worklapse",
            Menu = new NativeMenu { Items = { showItem, reviewItem, _pauseItem, settingsItem, new NativeMenuItemSeparator(), quitItem } },
        };
        tray.Clicked += (_, _) => ShowStatusWindow();
        TrayIcon.SetIcons(this, [tray]);
    }

    private void ShowStatusWindow()
    {
        _statusWindow!.Show();
        _statusWindow.Activate();
    }

    /// <summary>Opens the Day review, or brings it to the front if it's already open.</summary>
    private void ShowDayReview()
    {
        if (_reviewWindow is { } open)
        {
            open.Activate();
            return;
        }

        var settings = _settings!.Current;
        var history = new HistoryStore(WorklapseDatabase.Open(settings.DataFolder));
        // The recorder's batch encoder (open even while paused or locked) removes the deleted video and images from
        // disk. Frames in the segments still being recorded go once those are closed and encoded.
        var viewModel = new DayReviewViewModel(history, _settings, TimeSpan.FromSeconds(settings.IntervalSeconds), result =>
        {
            _sink?.Encoder.RequestReclaim();
            if (result.StagedSegmentsAffected)
                _recorder!.CloseSegmentsSoon();
        });
        _reviewWindow = new DayReviewWindow(viewModel, settings.DataFolder);
        _reviewWindow.Closed += (_, _) =>
        {
            _reviewWindow = null;
            // Reviewing leaves a large heap behind (the window, frame buffers).
            ReleaseMemory();
        };
        _reviewWindow.Show();
    }

    private async Task ShowSettingsAsync()
    {
        var monitors = _platform!.Screen.GetMonitors();
        // The recording preview captures the primary monitor with a source of its own.
        var sampler = OperatingSystem.IsWindows() ? new PrimaryScreenSampler(new GdiScreenSource()) : null;
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(_settings!, monitors, sampler) };
        ShowStatusWindow();
        await dialog.ShowDialog(_statusWindow!);
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

    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _quitting = true;
        _reviewWindow?.Close();
        if (_recorder is not null)
            await _recorder.DisposeAsync();
        _platform?.Dispose();
        desktop.Shutdown();
    }
}
