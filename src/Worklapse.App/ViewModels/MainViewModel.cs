using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.App.Updates;
using Worklapse.Core.Settings;
using Worklapse.Core.Suggestions;

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
    private readonly Func<DateOnly?, DayReviewViewModel> _createDayReview;
    private readonly Func<Task> _openSettings;
    private bool _dayReviewIsFresh; // just made or moved to a day: showing it needs no refresh

    /// <param name="createDayReview">Makes Day review on a day, or on the latest day recorded (null).</param>
    public MainViewModel(RecordingViewModel recording, AppUpdates updates, Func<DateOnly?, DayReviewViewModel> createDayReview,
        Func<Task> openSettings, SettingsStore settings, SuggestionEngine suggestionEngine, MainView first)
    {
        Recording = recording;
        Updates = updates;
        _createDayReview = createDayReview;
        _openSettings = openSettings;
        Overview = new OverviewViewModel(settings, suggestionEngine, OpenDay);
        Timesheet = new TimesheetViewModel(settings, OpenDay);
        View = first;
        if (first == MainView.DayReview)
            EnsureDayReview();
    }

    public RecordingViewModel Recording { get; }

    public AppUpdates Updates { get; }

    // These read their data folder and day start from the settings at each load, so they need nothing after a change of either.
    public OverviewViewModel Overview { get; }
    public TimesheetViewModel Timesheet { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsDayReview), nameof(IsTimesheet), nameof(Title))]
    private MainView _view;

    partial void OnViewChanged(MainView value)
    {
        // Hidden, Day review is kept as it was, but not playing: its decoders then close after a while. Its edits can't
        // be undone once another view is shown.
        if (value != MainView.DayReview)
        {
            DayReview?.StopPlaying();
            DayReview?.ClearUndo();
        }
    }

    public bool IsOverview => View == MainView.Overview;
    public bool IsDayReview => View == MainView.DayReview;
    public bool IsTimesheet => View == MainView.Timesheet;

    /// <summary>Made the first time Day review is shown; null until then (and after the data folder or the day start changes).</summary>
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
    private Task OpenSettings()
    {
        // Settings can delete projects and change links, which Day review's undo history would then put back wrongly.
        DayReview?.ClearUndo();
        return _openSettings();
    }

    /// <summary>Opens a day in Day review (from the Timesheet or the Overview), reading it once.</summary>
    public void OpenDay(DateOnly day)
    {
        if (DayReview is null)
        {
            EnsureDayReview(day);
        }
        else
        {
            DayReview.GoToDay(day);
            _dayReviewIsFresh = true;
        }
        View = MainView.DayReview;
    }

    /// <summary>
    /// Brings the view shown up to date: when it's shown, when the window comes back to the front
    /// (<paramref name="activated"/>) and after Settings closes. The views share the history: Day review changes the
    /// logged time and notes the others show, Settings their projects, and recording adds to today.
    /// </summary>
    public void RefreshView(bool activated = false)
    {
        switch (View)
        {
            case MainView.DayReview:
                // Day review that was just read needs no second read.
                if (_dayReviewIsFresh)
                    _dayReviewIsFresh = false;
                else
                    DayReview?.RefreshCommand.Execute(null);
                break;
            case MainView.Overview when activated:
                Overview.RefreshIfCurrent();
                break;
            case MainView.Overview:
                Overview.Refresh();
                break;
            case MainView.Timesheet when activated:
                Timesheet.RefreshIfCurrent();
                break;
            case MainView.Timesheet:
                Timesheet.Refresh();
                break;
        }
    }

    /// <summary>
    /// Makes Day review again, reading the history from where the settings now say with days starting when they say
    /// (after a change of either).
    /// </summary>
    /// <param name="keepDay">Open on the day shown (its date), rather than the latest recorded.</param>
    public void ReopenDayReview(bool keepDay = false)
    {
        if (DayReview is not { } old)
            return;
        var day = old.Day.Day;
        CloseDayReview();
        if (View == MainView.DayReview)
            EnsureDayReview(keepDay ? day : null);
    }

    public void Dispose()
    {
        CloseDayReview();
        Overview.Dispose();
        Timesheet.Dispose();
    }

    private void EnsureDayReview(DateOnly? onDay = null)
    {
        if (DayReview is not null)
            return;
        var day = _createDayReview(onDay);
        day.PropertyChanged += OnDayReviewChanged;
        _dayReviewIsFresh = true;
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
