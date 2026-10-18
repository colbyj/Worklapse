using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;

namespace Worklapse.App.ViewModels;

public sealed partial class StatusViewModel : ObservableObject
{
    private static readonly IBrush RecordingBrush = new SolidColorBrush(Color.Parse("#DA4453"));
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.Parse("#A0A4A8"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#DA4453"));

    private readonly Recorder _recorder;
    private readonly SettingsStore _settings;
    private readonly Func<Task> _openSettings;
    private readonly Action _openReview;

    public StatusViewModel(Recorder recorder, SettingsStore settings, Func<Task> openSettings, Action openReview)
    {
        _recorder = recorder;
        _settings = settings;
        _openSettings = openSettings;
        _openReview = openReview;
        Update(recorder.Status);
    }

    [RelayCommand]
    private void OpenReview() => _openReview();

    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private IBrush _dotBrush = InactiveBrush;
    [ObservableProperty] private string _pauseText = "Pause";
    [ObservableProperty] private bool _hasError;

    /// <summary>Why video or images of deleted captures are still on disk, if they are; see BatchEncoder.DeletedContentLeft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedContentText), nameof(HasDeletedContentLeft))]
    private string? _deletedContentLeft;

    public bool HasDeletedContentLeft => DeletedContentLeft is not null;
    public string DeletedContentText => $"Some deleted recordings are still on disk: {DeletedContentLeft}.";

    public void Update(RecorderStatus status)
    {
        var settings = _settings.Current;
        ModeText = DescribeMode(settings);
        PauseText = _recorder.IsPaused ? "Resume" : "Pause";
        HasError = status.State == RecorderState.Error;

        (StateText, DotBrush, DetailText) = status.State switch
        {
            RecorderState.Starting => ("Starting", InactiveBrush, "Waiting for the first capture"),
            RecorderState.Recording => ("Recording", RecordingBrush, LastCapture(status)),
            RecorderState.Paused => ("Paused", InactiveBrush, LastCapture(status)),
            RecorderState.Idle => ("Idle", InactiveBrush, $"No input for {settings.IdleMinutes}+ minutes"),
            RecorderState.Locked => ("Screen locked", InactiveBrush, LastCapture(status)),
            RecorderState.Error => ("Not recording", ErrorBrush, status.Error ?? "Unknown error"),
            _ => (status.State.ToString(), InactiveBrush, ""),
        };
    }

    [RelayCommand]
    private void TogglePause()
    {
        _recorder.IsPaused = !_recorder.IsPaused;
        if (!_recorder.IsPaused)
        {
            // The recorder picks this up at its next tick; show that it's coming.
            StateText = "Resuming";
            PauseText = "Pause";
        }
    }

    [RelayCommand]
    private Task OpenSettings() => _openSettings();

    private static string LastCapture(RecorderStatus status) =>
        status.LastCapture is { } at ? $"Last capture {at.LocalDateTime:T}" : "No captures yet";

    private static string DescribeMode(WorklapseSettings settings)
    {
        var what = settings.CaptureMode switch
        {
            CaptureMode.ActiveApplication => "Active application",
            CaptureMode.PrimaryMonitor => "Primary monitor",
            CaptureMode.SelectedMonitors => settings.SelectedMonitors.Count == 1 ? "1 selected monitor" : $"{settings.SelectedMonitors.Count} selected monitors",
            CaptureMode.AllMonitors => "All monitors",
            _ => settings.CaptureMode.ToString(),
        };
        return $"{what} · every {settings.IntervalSeconds} seconds";
    }
}
