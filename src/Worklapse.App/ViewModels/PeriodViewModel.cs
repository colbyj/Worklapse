using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

/// <summary>
/// A view of a week or a month (the Overview, the Timesheet): stepping through periods, and reading the one shown off
/// the UI thread with a connection of its own. A newer load wins; a load finishing after the window closed is
/// dropped. The data folder, capture interval and day start are read from the settings at each load, so a change of
/// any needs nothing more than a refresh.
/// </summary>
/// <typeparam name="TData">What a load reads, shown on the UI thread.</typeparam>
public abstract partial class PeriodViewModel<TData> : ObservableObject, IDisposable where TData : class
{
    private int _generation;

    protected PeriodViewModel(SettingsStore settings, Action<DateOnly> openDay)
    {
        Settings = settings;
        OpenDay = openDay;
        Period = Period.Containing(PeriodKind.Week, Today);
    }

    protected SettingsStore Settings { get; }

    /// <summary>Opens a day in Day review.</summary>
    protected Action<DateOnly> OpenDay { get; }

    protected bool IsDisposed { get; private set; }

    /// <summary>What the newest load read; null before it, and after an error.</summary>
    protected TData? Data { get; private set; }

    /// <summary>A capture lasts until the next one, at most this long (as in Day review).</summary>
    protected TimeSpan MaxCaptureLength => Settings.Current.MaxCaptureLength;

    /// <summary>The day it is now, as the settings bound days.</summary>
    protected DateOnly Today => LocalDay.Today(Settings.Current.DayStart);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeek), nameof(IsMonth), nameof(RangeText), nameof(Title), nameof(TodayText))]
    private Period _period;

    public string RangeText => Period.Text;

    /// <summary>"Week 42" or "October".</summary>
    public string Title => Period.Title;

    public string TodayText => Period.TodayText;

    // The Week and Month tool buttons (a radio pair: the one turned off by the other's click changes nothing).
    public bool IsWeek
    {
        get => Period.Kind == PeriodKind.Week;
        set
        {
            if (value)
                MoveTo(Period.As(PeriodKind.Week, Today));
        }
    }

    public bool IsMonth
    {
        get => Period.Kind == PeriodKind.Month;
        set
        {
            if (value)
                MoveTo(Period.As(PeriodKind.Month, Today));
        }
    }

    [RelayCommand]
    private void Previous() => MoveTo(Period.Previous);

    [RelayCommand]
    private void Next() => MoveTo(Period.Next);

    [RelayCommand]
    private void GoToToday() => MoveTo(Period.Containing(Period.Kind, Today));

    private void MoveTo(Period period)
    {
        if (period == Period)
            return;
        Period = period;
        Refresh();
    }

    /// <summary>
    /// When the window comes back to the front: only a period holding today can have changed meanwhile (it grows
    /// while recording). Day review and Settings change the others, and showing the view or closing Settings
    /// refreshes it.
    /// </summary>
    public void RefreshIfCurrent()
    {
        if (Period.Contains(Today))
            Refresh();
    }

    /// <summary>Reads the period again in the background; what's shown stays until it's ready.</summary>
    public async void Refresh()
    {
        if (IsDisposed)
            return;
        var generation = ++_generation;
        var (period, settings) = (Period, Settings.Current);
        TData data;
        try
        {
            data = await Task.Run(() => Load(settings.DataFolder, period, settings.MaxCaptureLength, settings.DayStart));
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            if (generation == _generation && !IsDisposed)
            {
                Data = null;
                ShowError("Couldn't read the history: " + ex.Message);
            }
            return;
        }
        // A newer load is on its way, or the window has closed.
        if (generation != _generation || IsDisposed)
            return;
        Data = data;
        Show(data);
    }

    public void Dispose() => IsDisposed = true;

    /// <summary>
    /// Why the history can't be read or written that isn't a bug: the file locked, missing or unreadable, or made by a
    /// newer Worklapse.
    /// </summary>
    protected static bool IsReadError(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException or DatabaseVersionException;

    /// <summary>Reads a period; runs on a background thread, so it opens its own connection.</summary>
    protected abstract TData Load(string dataFolder, Period period, TimeSpan maxCaptureLength, TimeOnly dayStart);

    protected abstract void Show(TData data);

    /// <summary>The history couldn't be read (the message says why).</summary>
    protected abstract void ShowError(string message);
}
