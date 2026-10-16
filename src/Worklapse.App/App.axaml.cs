using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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
    private StatusWindow? _statusWindow;
    private DayReviewWindow? _reviewWindow;
    private NativeMenuItem? _pauseItem;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
        var settings = _settings;
        _recorder = new Recorder(settings, _platform, store => new StagedVideoSink(store, () => settings.Current.Quality));

        var status = new StatusViewModel(_recorder, _settings, ShowSettingsAsync, ShowDayReview);
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
        var viewModel = new DayReviewViewModel(history, TimeSpan.FromSeconds(settings.IntervalSeconds));
        _reviewWindow = new DayReviewWindow(viewModel, settings.DataFolder);
        _reviewWindow.Closed += (_, _) =>
        {
            _reviewWindow = null;
            // Reviewing leaves a large heap behind (the window, frame buffers) that the GC would otherwise
            // keep committed for a long time, since recording allocates too little to trigger collections.
            // Once the window is torn down, collect and give the memory back to the OS.
            Dispatcher.UIThread.Post(
                () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true),
                DispatcherPriority.Background);
        };
        _reviewWindow.Show();
    }

    private async Task ShowSettingsAsync()
    {
        var monitors = _platform!.Screen.GetMonitors();
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(_settings!, monitors) };
        ShowStatusWindow();
        await dialog.ShowDialog(_statusWindow!);
    }

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
