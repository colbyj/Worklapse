using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.Core.Settings;

namespace Worklapse.App.ViewModels;

/// <summary>
/// A tip shown in a bar under a view's tool bar, saying what the view is for and what to do there, until it's closed
/// (remembered in the settings, <see cref="WorklapseSettings.DismissedHints"/>).
/// </summary>
public sealed partial class Hint : ObservableObject
{
    private readonly SettingsStore _settings;

    internal Hint(SettingsStore settings, string key, string heading, string text)
    {
        _settings = settings;
        Key = key;
        Heading = heading;
        Text = text;
        Update(settings.Current);
    }

    public string Key { get; }

    public string Heading { get; }

    public string Text { get; }

    [ObservableProperty]
    private bool _isShown;

    internal void Update(WorklapseSettings settings) => IsShown = !settings.DismissedHints.Contains(Key);

    [RelayCommand]
    private void Dismiss()
    {
        IsShown = false;
        var current = _settings.Current;
        if (!current.DismissedHints.Contains(Key))
            _settings.Save(current with { DismissedHints = [.. current.DismissedHints, Key] });
    }
}

/// <summary>The views' tips, one each, made once for the app (the views come and go with the main window).</summary>
public sealed class Hints
{
    private readonly SettingsStore _settings;

    public Hints(SettingsStore settings)
    {
        _settings = settings;
        Overview = new Hint(settings, "overview", "Your week at a glance.",
            "Each day shows its projects above its activity. Click a day to review it: Day review is where time is given to projects.");
        DayReview = new Hint(settings, "dayReview", "Play back the day and give its time to projects.",
            "Drag across the timeline to select time, then Set Project, or Mark for Deletion to take it out of your totals. " +
            "Suggestions offers projects for time without one; linking an app or site to a project on Applications does it for all its time.");
        Timesheet = new Hint(settings, "timesheet", "One line per project per day.",
            "Type the hours to bill in Logged (4:45, 4.75 or 4h45), then Export CSV for a spreadsheet or invoice.");
        settings.Changed += (_, current) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var hint in All)
                hint.Update(current);
        });
    }

    public Hint Overview { get; }

    public Hint DayReview { get; }

    public Hint Timesheet { get; }

    private IEnumerable<Hint> All => [Overview, DayReview, Timesheet];

    /// <summary>Some tip has been closed, so there's one to bring back.</summary>
    public bool AnyDismissed => _settings.Current.DismissedHints.Count > 0;

    /// <summary>Shows every view's tip again.</summary>
    public void ShowAll()
    {
        _settings.Save(_settings.Current with { DismissedHints = [] });
        foreach (var hint in All)
            hint.IsShown = true;
    }
}
