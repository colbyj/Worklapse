using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Worklapse.App.ViewModels;

public enum MainView
{
    Overview,
    DayReview,
    Timesheet,
}

/// <summary>
/// The main window: which view is shown, recording state, and the way to Settings. Views are made when first shown
/// and live as long as the window; closing the window lets them go (the app keeps recording in the tray).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Func<DayReviewViewModel> _createDayReview;
    private readonly Func<Task> _openSettings;

    public MainViewModel(RecordingViewModel recording, Func<DayReviewViewModel> createDayReview, Func<Task> openSettings, MainView first)
    {
        Recording = recording;
        _createDayReview = createDayReview;
        _openSettings = openSettings;
        View = first;
        if (first == MainView.DayReview)
            EnsureDayReview();
    }

    public RecordingViewModel Recording { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsDayReview), nameof(IsTimesheet), nameof(Title))]
    private MainView _view;

    public bool IsOverview => View == MainView.Overview;
    public bool IsDayReview => View == MainView.DayReview;
    public bool IsTimesheet => View == MainView.Timesheet;

    /// <summary>Made the first time Day review is shown; null until then (and after the data folder changes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private DayReviewViewModel? _dayReview;

    public string Title => View == MainView.DayReview && DayReview is { } day ? day.Title : "Worklapse";

    [RelayCommand]
    private void ShowOverview() => View = MainView.Overview;

    [RelayCommand]
    private void ShowDayReview()
    {
        EnsureDayReview();
        View = MainView.DayReview;
    }

    [RelayCommand]
    private void ShowTimesheet() => View = MainView.Timesheet;

    [RelayCommand]
    private Task OpenSettings() => _openSettings();

    /// <summary>Makes Day review again, reading the history from where the settings now say (after a folder change).</summary>
    public void ReopenDayReview()
    {
        if (DayReview is null)
            return;
        CloseDayReview();
        if (View == MainView.DayReview)
            EnsureDayReview();
    }

    public void Dispose() => CloseDayReview();

    private void EnsureDayReview()
    {
        if (DayReview is not null)
            return;
        var day = _createDayReview();
        day.PropertyChanged += OnDayReviewChanged;
        DayReview = day;
    }

    private void CloseDayReview()
    {
        if (DayReview is not { } day)
            return;
        day.PropertyChanged -= OnDayReviewChanged;
        DayReview = null;
        day.Dispose();
    }

    private void OnDayReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DayReviewViewModel.Title))
            OnPropertyChanged(nameof(Title));
    }
}
