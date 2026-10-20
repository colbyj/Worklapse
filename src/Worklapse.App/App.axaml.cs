using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Worklapse.App.Theming;
using Worklapse.App.Tray;
using Worklapse.App.ViewModels;
using Worklapse.App.Views;
using Worklapse.Capture.Linux;
using Worklapse.Capture.Windows;
using Worklapse.Core.Diagnostics;
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
    private IAppTray? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private ErrorWindow? _errorWindow;
    private string? _lastWindowError;
    private bool _announceRecording; // recording started by itself at startup, and nobody has been told yet
    private bool _toldInTray;        // the "still running in the tray" notification was shown in this run
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
        Dispatcher.UIThread.UnhandledException += OnWindowError;
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
            Log.Error("The settings file can't be read.", ex);
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
        if (!_settings.Current.RecordAtStartup)
            _recorder.IsPaused = true;
        _announceRecording = _settings.Current.RecordAtStartup;

        _desktop = desktop;
        var recording = _recording = new RecordingViewModel(_recorder, _settings);
        recording.PropertyChanged += (_, _) => UpdateTray();
        recording.PauseToggled += () => _announceRecording = false; // the developer's own doing needs no notice

        _recorder.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            recording.Update(s);
            AnnounceRecording(s);
        });
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => recording.Update(_recorder.Status));

        CreateTray();
        _recorder.Start();
        // Started at login, it stays in the tray (the "recording" notification says it's there).
        if (desktop.Args?.Contains(LoginStartup.Argument) != true)
            ShowMainWindow(MainView.Overview);
        ReportLastCrash();
    }

    /// <summary>The first time recording is under way after starting by itself, says so (if the settings want that).</summary>
    private void AnnounceRecording(RecorderStatus status)
    {
        if (!_announceRecording || status.State != RecorderState.Recording)
            return;
        _announceRecording = false;
        var settings = _settings!.Current;
        if (settings.NotifyWhenRecordingStarts)
            _tray?.Notify("Worklapse is recording", $"{_recording!.DetailText}, every {settings.IntervalSeconds} seconds. Pause it from the tray icon.");
    }

    /// <summary>Says so if the last run ended in a crash (the log has the details).</summary>
    private void ReportLastCrash()
    {
        if (Log.TakeCrashReport() is not { } crash)
            return;
        var recording = _settings!.Current.RecordAtStartup ? "Recording has started again." : "Worklapse started paused, as set.";
        ShowError("Worklapse closed unexpectedly", crash.Message, $"It stopped at {crash.At.LocalDateTime:g}. {recording}");
    }

    /// <summary>
    /// An exception in a window's code (a bug): logged and shown, and Worklapse carries on, since recording doesn't
    /// depend on the windows. One during startup ends the process instead (half started, it would run with no window
    /// or tray icon); the log keeps it and the next start reports it.
    /// </summary>
    private void OnWindowError(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_tray is null)
            return;
        e.Handled = true;
        var message = e.Exception.Message;
        // One that repeats (at every layout pass, say) is logged and shown once.
        if (_errorWindow is not null && message == _lastWindowError)
            return;
        _lastWindowError = message;
        Log.Error("Unexpected error in a window.", e.Exception);
        ShowError("Something went wrong", message, "Recording isn't affected.");
    }

    private void ShowError(string heading, string message, string explanation)
    {
        if (_errorWindow is not null)
            return; // logged; one error window at a time
        _errorWindow = new ErrorWindow(heading, message, explanation);
        _errorWindow.Closed += (_, _) => _errorWindow = null;
        _errorWindow.Show();
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
            if (_sink != sink)
                return;
            _recording!.DeletedContentLeft = encoder.DeletedContentLeft;
            _recording.EncodingProblem = encoder.EncodingProblem;
        });
        encoder.DeletedContentLeftChanged += (_, _) => Report();
        encoder.EncodingProblemChanged += (_, _) => Report();
        _sink = sink;
        Report();
        return sink;
    }

    /// <summary>
    /// The platform's capture services; when they can't start (a desktop Worklapse can't record on yet, KWin missing),
    /// one that records nothing and says why, so the rest of the app still works.
    /// </summary>
    private static PlatformServices CreatePlatform()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsPlatform.Create();
            if (OperatingSystem.IsLinux())
            {
                return KdePlatform.Create(LinuxIconPath());
            }
            return PlatformServices.Unavailable("Worklapse can't record on this operating system yet.");
        }
        catch (Exception ex)
        {
            Log.Error("Recording can't start on this desktop.", ex);
            return PlatformServices.Unavailable(ex.Message);
        }
    }

    private void CreateTray()
    {
        var commands = new TrayCommands(
            Show: () => ShowMainWindow(),
            TogglePause: () => _recording!.TogglePauseCommand.Execute(null),
            Settings: () => _ = ShowSettingsAsync(),
            Quit: () => _ = QuitAsync());
        _tray = OperatingSystem.IsWindows()
            ? new Win32Tray(commands)
            : new AvaloniaTray(this, commands, OperatingSystem.IsLinux() ? new DesktopNotifications(LinuxIconPath()) : null);
        _tray.Activated += (_, _) => ShowMainWindow();
        UpdateTray();
    }

    /// <summary>The tray says whether Worklapse is recording, and offers to pause or resume.</summary>
    private void UpdateTray()
    {
        if (_tray is null || _recording is null)
            return;
        var badge = _recording.HasError ? TrayBadge.Error
            : _recording.IsPaused ? TrayBadge.Paused
            : _recorder!.Status.State == RecorderState.Recording ? TrayBadge.Recording
            : TrayBadge.None;
        _tray.Update(badge, _recording.TrayText, _recording.PauseText);
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
                TellStillInTray();
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

    /// <summary>The picture Linux desktops show for Worklapse (its menu entry, notifications); null if it's missing.</summary>
    internal static string? LinuxIconPath()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "worklapse.png");
        return File.Exists(icon) ? icon : null;
    }

    /// <summary>
    /// Day review on a day (or the latest recorded), reading the history in the data folder the settings name now, with
    /// days starting when they say now.
    /// </summary>
    private DayReviewViewModel CreateDayReview(DateOnly? day)
    {
        var settings = _settings!.Current;
        var history = new HistoryStore(WorklapseDatabase.Open(settings.DataFolder), settings.DayStart);
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
        var sampler = _platform.CreateScreenSource is { } createSource ? new PrimaryScreenSampler(createSource()) : null;
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(_settings!, monitors, sampler, _platform.KnowsPrimaryMonitor, _platform.TextReader) };
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

    /// <summary>Once per run, when closing the main window leaves Worklapse running in the tray (if the settings want that).</summary>
    private void TellStillInTray()
    {
        if (_quitting || _toldInTray || !_settings!.Current.NotifyWhenInTray)
            return;
        _toldInTray = true;
        _tray?.Notify("Worklapse is still running",
            _recording!.IsPaused
                ? "Recording is paused. Open Worklapse or quit it from its tray icon."
                : "It keeps recording in the tray. Open Worklapse or quit it from its tray icon.");
    }

    private async Task QuitAsync()
    {
        if (_quitting)
            return;
        _quitting = true;
        _mainWindow?.Close();
        if (_recorder is not null)
            await _recorder.DisposeAsync();
        _platform?.Dispose();
        _tray?.Dispose();
        Log.Info("Worklapse quit.");
        _desktop?.Shutdown();
    }
}
