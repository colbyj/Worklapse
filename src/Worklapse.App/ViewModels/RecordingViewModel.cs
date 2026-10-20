using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.App.Theming;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;

namespace Worklapse.App.ViewModels;

/// <summary>
/// Whether Worklapse is recording, for every view's status bar, the message bars under the tool bars, the rail's
/// recording button and the tray icon. Recording state has to be obvious wherever the developer looks.
/// </summary>
public sealed partial class RecordingViewModel : ObservableObject
{
    private readonly Recorder _recorder;
    private readonly SettingsStore _settings;

    public RecordingViewModel(Recorder recorder, SettingsStore settings)
    {
        _recorder = recorder;
        _settings = settings;
        Update(recorder.Status);
    }

    /// <summary>"Recording", "Paused", "Idle"… for the status bar.</summary>
    [ObservableProperty] private string _stateText = "";

    /// <summary>What is captured, or since when nothing has been, next to the state.</summary>
    [ObservableProperty] private string _detailText = "";

    [ObservableProperty] private IBrush _dotBrush = AppBrushes.Inactive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDot), nameof(PauseText), nameof(RailTip))]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDot), nameof(RailTip))]
    private bool _hasError;

    /// <summary>The recording error, for the red message bar.</summary>
    [ObservableProperty] private string _errorText = "";

    /// <summary>"Nothing has been captured since 14:20.", for the paused message bar.</summary>
    [ObservableProperty] private string _pausedText = "";

    /// <summary>Why video or images of deleted captures are still on disk, if they are; see BatchEncoder.DeletedContentLeft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedContentText), nameof(HasDeletedContentLeft))]
    private string? _deletedContentLeft;

    public bool HasDeletedContentLeft => DeletedContentLeft is not null;
    public string DeletedContentText => $"Some deleted recordings are still on disk: {DeletedContentLeft}.";

    /// <summary>The last recording that couldn't be encoded and why, if one couldn't; see BatchEncoder.EncodingProblem.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncodingProblemText), nameof(HasEncodingProblem))]
    private string? _encodingProblem;

    public bool HasEncodingProblem => EncodingProblem is not null;
    public string EncodingProblemText => $"A recording couldn't be encoded. It's kept and tried again when Worklapse next starts. {EncodingProblem}";

    /// <summary>The status bar shows a dot, except while paused (a pause sign instead).</summary>
    public bool ShowsDot => !IsPaused;

    public string PauseText => IsPaused ? "Resume Recording" : "Pause Recording";
    public string RailTip => IsPaused ? "Paused — click to resume" : HasError ? "Not recording — click to pause" : $"{StateText} — click to pause";

    /// <summary>For the tray icon's tooltip.</summary>
    public string TrayText => $"Worklapse — {StateText}";

    public void Update(RecorderStatus status)
    {
        var settings = _settings.Current;
        IsPaused = _recorder.IsPaused;
        HasError = status.State == RecorderState.Error;
        ErrorText = status.Error ?? "Unknown error";
        PausedText = status.LastCapture is { } last
            ? $"Nothing has been captured since {last.LocalDateTime:t}."
            : "Nothing has been captured yet.";

        (StateText, DotBrush, DetailText) = status.State switch
        {
            RecorderState.Starting => ("Starting", (IBrush)AppBrushes.Inactive, DescribeMode(settings)),
            RecorderState.Recording => ("Recording", AppBrushes.Recording, DescribeMode(settings)),
            RecorderState.Paused => ("Paused", AppBrushes.Paused, status.LastCapture is { } at ? $"since {at.LocalDateTime:t}" : ""),
            RecorderState.Idle => ("Idle", AppBrushes.Inactive, $"no input for {settings.IdleMinutes}+ minutes"),
            RecorderState.Locked => ("Screen locked", AppBrushes.Inactive, ""),
            RecorderState.Excluded => ("Excluded", AppBrushes.Inactive, "an excluded app or site has the focus"),
            RecorderState.Error => ("Not recording", AppBrushes.Error, ""),
            _ => (status.State.ToString(), AppBrushes.Inactive, ""),
        };
        OnPropertyChanged(nameof(RailTip));
        OnPropertyChanged(nameof(TrayText));
    }

    /// <summary>Raised when the developer pauses or resumes recording.</summary>
    public event Action? PauseToggled;

    [RelayCommand]
    private void TogglePause()
    {
        _recorder.IsPaused = !_recorder.IsPaused;
        IsPaused = _recorder.IsPaused;
        PauseToggled?.Invoke();
        if (!IsPaused)
        {
            // The recorder picks this up at its next tick; show that it's coming.
            StateText = "Resuming";
            DotBrush = AppBrushes.Inactive;
            DetailText = "";
        }
        else
        {
            StateText = "Paused";
            DotBrush = AppBrushes.Paused;
        }
        OnPropertyChanged(nameof(RailTip));
        OnPropertyChanged(nameof(TrayText));
    }

    private static string DescribeMode(WorklapseSettings settings) => settings.CaptureMode switch
    {
        CaptureMode.ActiveApplication => "Active application",
        CaptureMode.PrimaryMonitor => "Primary monitor",
        CaptureMode.SelectedMonitors => settings.SelectedMonitors.Count == 1 ? "1 selected monitor" : $"{settings.SelectedMonitors.Count} selected monitors",
        CaptureMode.AllMonitors => "All monitors",
        _ => settings.CaptureMode.ToString(),
    };
}
