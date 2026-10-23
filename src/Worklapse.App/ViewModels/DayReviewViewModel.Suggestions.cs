using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.App.Controls;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.App.ViewModels;

/// <summary>A project's open stretches on the Suggestions tab, with Accept and Reject for all of them.</summary>
public sealed class SuggestionGroupRow(string name, IBrush brush, IBrush tint, string countText, string lengthText, bool isFirst,
    IReadOnlyList<StoredSuggestion> stretches, Action<IReadOnlyList<StoredSuggestion>> accept, Action<IReadOnlyList<StoredSuggestion>> reject)
{
    public string Name { get; } = name;
    public IBrush Brush { get; } = brush;
    public IBrush Tint { get; } = tint;
    public string CountText { get; } = countText;
    public string LengthText { get; } = lengthText;
    public bool IsFirst { get; } = isFirst;
    public IReadOnlyList<StoredSuggestion> Stretches { get; } = stretches;
    public IRelayCommand Accept { get; } = new RelayCommand(() => accept(stretches));
    public IRelayCommand Reject { get; } = new RelayCommand(() => reject(stretches));
}

/// <summary>
/// A suggested stretch on the Suggestions tab: its project (which can be changed before accepting), its time, how sure the
/// classifier is, and the words it rests on (shown under it while it's the one being checked, otherwise as its tip).
/// </summary>
public sealed class SuggestionRow : ObservableObject
{
    // How wide the certainty bar is when the classifier is sure.
    private const double CertaintyBarWidth = 16;

    private readonly Action<SuggestionRow, ProjectOption> _chooseProject;
    private ProjectOption _project;
    private bool _isCurrent;

    public SuggestionRow(StoredSuggestion suggestion, ProjectOption project, string rangeText, string lengthText, string evidenceText,
        string screenText, Action<SuggestionRow, ProjectOption> chooseProject, Action<StoredSuggestion> accept, Action<StoredSuggestion> reject)
    {
        Suggestion = suggestion;
        _project = project;
        RangeText = rangeText;
        LengthText = lengthText;
        EvidenceText = evidenceText;
        ScreenText = screenText;
        _chooseProject = chooseProject;
        Accept = new RelayCommand(() => accept(suggestion));
        Reject = new RelayCommand(() => reject(suggestion));
    }

    public StoredSuggestion Suggestion { get; }
    public string RangeText { get; }
    public string LengthText { get; }
    public bool IsUnsure => Suggestion.IsUnsure;

    /// <summary>The classifier's certainty; none once the developer chose the project.</summary>
    public string CertaintyText => Suggestion.State == SuggestionState.Chosen ? "" : $"{Suggestion.Confidence:P0}";
    public bool ShowsCertainty => CertaintyText.Length > 0;
    public double CertaintyWidth => Math.Round(Suggestion.Confidence * CertaintyBarWidth);
    public string EvidenceText { get; }
    public string ScreenText { get; }
    public bool HasEvidenceText => EvidenceText.Length > 0;
    public bool HasScreenText => ScreenText.Length > 0;
    public string? Tip => HasEvidenceText || HasScreenText ? string.Join(Environment.NewLine, new[] { EvidenceText, ScreenText }.Where(t => t.Length > 0)) : null;
    public IRelayCommand Accept { get; }
    public IRelayCommand Reject { get; }

    /// <summary>The unsure stretch being checked: its words show under it.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(ShowsEvidence));
                OnPropertyChanged(nameof(ShowsScreen));
            }
        }
    }

    public bool ShowsEvidence => IsCurrent && HasEvidenceText;
    public bool ShowsScreen => IsCurrent && HasScreenText;

    public ProjectOption Project
    {
        get => _project;
        set
        {
            // A list that loses its selection hands back null, and None isn't a project to give the stretch.
            if (value is null || value.IsNone || value == _project)
                return;
            _project = value;
            OnPropertyChanged();
            _chooseProject(this, value);
        }
    }
}

/// <summary>The heading of the breaks on the Suggestions tab.</summary>
public sealed record SuggestionBreaksHeader(string CountText);

/// <summary>
/// A break on the Suggestions tab: what was there, its time, and the project time on each side. Counting it (toward the
/// project around it, or either of two) or leaving it out takes effect at once.
/// </summary>
public sealed class BreakRow(StoredBreak suggestedBreak, string whatText, string rangeText, string lengthText, string betweenText,
    IBrush brush, string beforeName, string afterName, Action<StoredBreak, long> count, Action<StoredBreak> leaveOut)
{
    public StoredBreak Break { get; } = suggestedBreak;
    public string WhatText { get; } = whatText;
    public string RangeText { get; } = rangeText;
    public string LengthText { get; } = lengthText;
    public string BetweenText { get; } = betweenText;

    /// <summary>The project's colour for a break inside one project's time; neutral between two.</summary>
    public IBrush Brush { get; } = brush;
    public bool IsInside => Break.Inside is not null;
    public bool IsBetween => !IsInside;
    public string CountTip { get; } = $"Count it toward {beforeName}";
    public string CountBeforeText { get; } = $"Count toward {beforeName}";
    public string CountAfterText { get; } = $"Count toward {afterName}";
    public IRelayCommand CountBefore { get; } = new RelayCommand(() => count(suggestedBreak, suggestedBreak.Before));
    public IRelayCommand CountAfter { get; } = new RelayCommand(() => count(suggestedBreak, suggestedBreak.After));
    public IRelayCommand LeaveOut { get; } = new RelayCommand(() => leaveOut(suggestedBreak));
}

public sealed partial class DayReviewViewModel
{
    // Words a suggestion shows as its evidence, at most, of each kind.
    private const int EvidenceShown = 6;

    /// <summary>A break between two projects' time is drawn in this, belonging to neither.</summary>
    private static readonly IBrush BetweenBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));

    private int _suggestionVersion;
    private StoredDay _suggestions = StoredDay.None;

    /// <summary>
    /// The stretch being checked (stepped to, or its row clicked) and where it starts: once it's decided, stepping goes on
    /// from there.
    /// </summary>
    private (long Id, DateTimeOffset Start)? _checking;

    /// <summary>
    /// After an edit (an accept, a range set by hand), suggestions worked out again wait for <see cref="ShowNewSuggestions"/>
    /// instead of replacing the list being worked through; also when the day is reloaded meanwhile (the window activated).
    /// Until another day is opened, they're shown, or working them out again changes nothing.
    /// </summary>
    private bool _holdSuggestions;
    private WorkedOutDay? _newSuggestions;

    /// <summary>The Suggestions tab: each project with open stretches, then its stretches; then the breaks.</summary>
    public ObservableCollection<object> SuggestionRows { get; } = [];

    [ObservableProperty] private int _openSuggestionCount;
    [ObservableProperty] private string _suggestionSummary = "";
    [ObservableProperty] private bool _isWorkingOutSuggestions;
    [ObservableProperty] private bool _hasNewSuggestions;
    [ObservableProperty] private int _unsureCount;
    [ObservableProperty] private string _unsureText = "";

    public bool HasOpenSuggestions => OpenSuggestionCount > 0;
    public bool HasNoOpenSuggestions => OpenSuggestionCount == 0;
    public bool HasUnsure => UnsureCount > 0;
    public bool SuggestionsOff => !_settings.Current.SuggestProjects;

    /// <summary>"No suggestions for this day": not while they're being worked out, nor when they're off.</summary>
    public bool ShowsNoSuggestions => !IsWorkingOutSuggestions && !SuggestionsOff;

    partial void OnIsWorkingOutSuggestionsChanged(bool value) => OnPropertyChanged(nameof(ShowsNoSuggestions));

    partial void OnOpenSuggestionCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOpenSuggestions));
        OnPropertyChanged(nameof(HasNoOpenSuggestions));
        AcceptAllSuggestionsCommand.NotifyCanExecuteChanged();
        AcceptSureSuggestionsCommand.NotifyCanExecuteChanged();
    }

    partial void OnUnsureCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnsure));
        PreviousUnsureCommand.NotifyCanExecuteChanged();
        NextUnsureCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Shows the day's suggestions as stored, then works them out again in the background (the classifier may train on
    /// the history first) and shows those when they're ready, unless the day or its projects changed meanwhile. While
    /// held (<see cref="_holdSuggestions"/>), ones that change the suggestions wait to be asked for instead.
    /// </summary>
    private void RefreshSuggestions()
    {
        var version = Interlocked.Increment(ref _suggestionVersion);
        var settings = _settings.Current;
        OnPropertyChanged(nameof(SuggestionsOff));
        OnPropertyChanged(nameof(ShowsNoSuggestions));
        if (!settings.SuggestProjects)
        {
            // A run still under way when they were turned off ends unseen (its version is old).
            IsWorkingOutSuggestions = false;
            StopHoldingSuggestions();
            ShowSuggestions(StoredDay.None);
            return;
        }
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        ShowSuggestions(new SuggestionStore(_history.Database).Get(dayStart, dayEnd));

        var (day, folder, start) = (Day.Day, _history.Database.DataFolder, _history.DayStart);
        IsWorkingOutSuggestions = true;
        // Asked again by its turn: that later call will do it. New suggestions waiting meanwhile stay until it's done.
        _suggestionEngine.WorkOutAsync(folder, start, day, settings, () => Volatile.Read(ref _suggestionVersion) == version).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (version != _suggestionVersion || Day.Day != day)
                return;
            IsWorkingOutSuggestions = false;
            if (task.Exception is { } failed)
            {
                Log.Error("Working out the day's suggestions failed.", failed.GetBaseException());
                return;
            }
            // With none open there's no list being worked through to keep.
            if (task.Result is { } worked && _holdSuggestions && OpenSuggestionCount > 0
                && new SuggestionStore(_history.Database).Changes(worked.Start, worked.End, worked.Suggestions))
            {
                SetNewSuggestions(worked);
                return;
            }
            // Shown now; none means those stored, and shown, are up to date.
            StopHoldingSuggestions();
            if (task.Result is { } fresh)
                StoreSuggestions(fresh);
        }), TaskScheduler.Default);
    }

    /// <summary>Suggestions worked out again are shown as they come: after another day is opened, or nothing changed.</summary>
    private void StopHoldingSuggestions()
    {
        _holdSuggestions = false;
        SetNewSuggestions(null);
    }

    /// <summary>Shows the suggestions worked out again after an edit, keeping the decisions made since.</summary>
    [RelayCommand]
    private void ShowNewSuggestions()
    {
        if (_newSuggestions is not { } worked)
            return;
        StopHoldingSuggestions();
        // Another edit's suggestions still being worked out wait in turn.
        _holdSuggestions = IsWorkingOutSuggestions;
        StoreSuggestions(worked);
    }

    private void SetNewSuggestions(WorkedOutDay? worked)
    {
        _newSuggestions = worked;
        HasNewSuggestions = worked is not null;
    }

    private void StoreSuggestions(WorkedOutDay worked)
    {
        _suggestionEngine.Store(_history, worked);
        ShowStoredSuggestions();
    }

    /// <summary>The open stretches the classifier is unsure of, in time order: what Unsure steps through.</summary>
    private List<StoredSuggestion> UnsureStretches() => _suggestions.Stretches.Where(s => s.IsOpen && s.IsUnsure).ToList();

    /// <summary>Lists the open stretches, by project, and the open breaks, and draws them on the projects lane.</summary>
    private void ShowSuggestions(StoredDay suggestions)
    {
        _suggestions = suggestions;
        SuggestionRows.Clear();
        var open = suggestions.Stretches.Where(s => s.IsOpen).ToList();
        var adds = TimeSpan.Zero;
        // Projects by name, so they keep their places while their stretches are decided (by time to add, accepting
        // some could swap two projects under the pointer); each project's stretches in time order.
        foreach (var group in open.GroupBy(s => s.ProjectId).Select(g => (Project: g.Key, Stretches: g.ToList(), Adds: g.Sum(s => Adds(s).Ticks)))
                     .OrderBy(g => NameOf(g.Project), StringComparer.CurrentCultureIgnoreCase))
        {
            var groupAdds = TimeSpan.FromTicks(group.Adds);
            adds += groupAdds;
            SuggestionRows.Add(new SuggestionGroupRow(NameOf(group.Project), BrushOf(group.Project), TintOf(group.Project),
                group.Stretches.Count == 1 ? "1 stretch" : $"{group.Stretches.Count} stretches", ProjectTimes.Hm(groupAdds),
                SuggestionRows.Count == 0, group.Stretches, AcceptSuggestions, RejectSuggestions));
            foreach (var stretch in group.Stretches)
            {
                // The words pointed to the project computed; once another is chosen they'd only mislead.
                var (evidence, screen) = stretch.State == SuggestionState.Chosen ? ("", "") : EvidenceTexts(stretch.Evidence);
                SuggestionRows.Add(new SuggestionRow(stretch, OptionOf(stretch.ProjectId), RangeText(stretch.Start, stretch.End),
                    ProjectTimes.Hm(Adds(stretch)), evidence, screen, ChooseSuggestionProject, AcceptSuggestion, RejectSuggestion));
            }
        }

        var breaks = suggestions.Breaks.Where(b => b.IsOpen).ToList();
        if (breaks.Count > 0)
            SuggestionRows.Add(new SuggestionBreaksHeader(breaks.Count == 1 ? "1 to decide" : $"{breaks.Count} to decide"));
        foreach (var suggestedBreak in breaks)
            SuggestionRows.Add(BreakRowOf(suggestedBreak));

        OpenSuggestionCount = open.Count + breaks.Count;
        SuggestionSummary = OpenSuggestionCount == 0 ? ""
            : string.Join(" · ", new[]
            {
                open.Count == 0 ? "" : $"{open.Count} {(open.Count == 1 ? "stretch" : "stretches")} · adds {ProjectTimes.Hm(adds)}",
                breaks.Count == 0 ? "" : $"{breaks.Count} {(breaks.Count == 1 ? "break" : "breaks")}",
            }.Where(t => t.Length > 0));
        ShowUnsureStep();
        BuildProjectLane();
        TimelineChanged?.Invoke();
    }

    private BreakRow BreakRowOf(StoredBreak suggestedBreak)
    {
        var what = WhatWasThere(suggestedBreak.Start, suggestedBreak.End);
        var (before, after) = (NameOf(suggestedBreak.Before), NameOf(suggestedBreak.After));
        return new BreakRow(suggestedBreak, what is "Idle" or "Not recorded" ? what : $"Away: {what}",
            RangeText(suggestedBreak.Start, suggestedBreak.End), ProjectTimes.Hm(suggestedBreak.End - suggestedBreak.Start),
            suggestedBreak.Inside is not null ? $"Inside {before}" : $"Between {before} and {after}",
            suggestedBreak.Inside is { } inside ? BrushOf(inside) : BetweenBrush, before, after, CountBreak, LeaveBreakOut);
    }

    /// <summary>The step through the unsure stretches ("2 of 5" while checking one of them), and which row shows its words.</summary>
    private void ShowUnsureStep()
    {
        var unsure = UnsureStretches();
        UnsureCount = unsure.Count;
        var at = _checking is { } checking ? unsure.FindIndex(s => s.Id == checking.Id) : -1;
        UnsureText = unsure.Count == 0 ? "None" : at >= 0 ? $"{at + 1} of {unsure.Count}" : $"{unsure.Count}";
        foreach (var row in SuggestionRows.OfType<SuggestionRow>())
            row.IsCurrent = _checking is { } current && row.Suggestion.Id == current.Id;
    }

    /// <summary>The unsure stretch before the one being checked (the last, when none is).</summary>
    [RelayCommand(CanExecute = nameof(HasUnsure))]
    private void PreviousUnsure()
    {
        var unsure = UnsureStretches();
        if ((_checking is { } checking ? unsure.LastOrDefault(s => s.Start < checking.Start) : unsure.LastOrDefault()) is { } stretch)
            CheckStretch(stretch);
    }

    /// <summary>The unsure stretch after the one being checked (the first, when none is).</summary>
    [RelayCommand(CanExecute = nameof(HasUnsure))]
    private void NextUnsure()
    {
        var unsure = UnsureStretches();
        if ((_checking is { } checking ? unsure.FirstOrDefault(s => s.Start > checking.Start) : unsure.FirstOrDefault()) is { } stretch)
            CheckStretch(stretch);
    }

    /// <summary>
    /// Checks a stretch: selects it and shows the capture the classifier was least sure of, with its words on the
    /// Suggestions tab.
    /// </summary>
    public void CheckStretch(StoredSuggestion stretch)
    {
        _checking = (stretch.Id, stretch.Start);
        PanelTab = SuggestionsTab;
        SelectRange(stretch.Start, stretch.End);
        GoTo(stretch.LeastSure);
        ShowUnsureStep();
    }

    /// <summary>What filled a stretch: idle, not recorded, or the app or site used most.</summary>
    private string WhatWasThere(DateTimeOffset start, DateTimeOffset end)
    {
        var summary = SpanSummary.For(Captures, start, end, _maxCaptureLength);
        if (summary.Active + summary.Idle < TimeSpan.FromSeconds(1))
            return "Not recorded";
        if (summary.Idle >= summary.Active)
            return "Idle";
        // A site says more than its browser, when one site was most of it.
        var programs = SpanSummary.ByProgram(Captures, start, end, _maxCaptureLength);
        if (programs.FirstOrDefault(p => p.Program.Kind == ProgramKind.Site) is { Program.Name: { } site } top && top.Time * 2 >= summary.Active)
            return site;
        return programs.FirstOrDefault().Program.Name ?? "Other";
    }

    /// <summary>The time a stretch gives its project: its time without what has a project already.</summary>
    private TimeSpan Adds(StoredSuggestion suggestion) =>
        SuggestionStore.Subtract([(suggestion.Start, suggestion.End)], _projectTime.Blocks.Select(b => (b.Start, b.End)))
            .Aggregate(TimeSpan.Zero, (sum, r) => sum + (r.End - r.Start));

    /// <summary>The evidence words for the panel: from the title, app and site; and those read off the screen.</summary>
    private static (string Evidence, string Screen) EvidenceTexts(IReadOnlyList<string> words)
    {
        var shown = new List<string>();
        var screen = new List<string>();
        foreach (var word in words)
        {
            if (word.StartsWith(CaptureWords.ScreenPrefix, StringComparison.Ordinal))
                screen.Add(word[CaptureWords.ScreenPrefix.Length..]);
            else if (word.StartsWith(CaptureWords.TitlePrefix, StringComparison.Ordinal))
                shown.Add(word[CaptureWords.TitlePrefix.Length..]);
            else if (word.IndexOf(':') is var colon and > 0)
                shown.Add(word[(colon + 1)..]);
        }
        return (shown.Count > 0 ? "From: " + string.Join(", ", shown.Take(EvidenceShown)) : "",
            screen.Count > 0 ? "On screen: " + string.Join(", ", screen.Take(EvidenceShown)) : "");
    }

    /// <summary>
    /// The projects lane: time marked with a project filled; open stretches outlined over the time without one (hatched
    /// when the classifier is unsure), open breaks dashed.
    /// </summary>
    private void BuildProjectLane()
    {
        var lane = _projectTime.Blocks
            .Select(b => new ProjectLaneBlock(SecondsOf(b.Start), SecondsOf(b.End), BrushOf(b.ProjectId), NameOf(b.ProjectId)))
            .ToList();
        var taken = _projectTime.Blocks.Select(b => (b.Start, b.End)).ToList();
        foreach (var stretch in _suggestions.Stretches.Where(s => s.IsOpen))
            foreach (var (start, end) in SuggestionStore.Subtract([(stretch.Start, stretch.End)], taken))
                lane.Add(new ProjectLaneBlock(SecondsOf(start), SecondsOf(end), BrushOf(stretch.ProjectId), NameOf(stretch.ProjectId),
                    stretch.IsUnsure ? LaneStyle.Unsure : LaneStyle.Suggested));
        foreach (var suggestedBreak in _suggestions.Breaks.Where(b => b.IsOpen))
            foreach (var (start, end) in SuggestionStore.Subtract([(suggestedBreak.Start, suggestedBreak.End)], taken))
                lane.Add(new ProjectLaneBlock(SecondsOf(start), SecondsOf(end),
                    suggestedBreak.Inside is { } inside ? BrushOf(inside) : BetweenBrush, "", LaneStyle.Break));
        ProjectLane = lane;
    }

    /// <summary>A project's name, for menus.</summary>
    public string ProjectName(long projectId) => NameOf(projectId);

    /// <summary>The open stretch or break at a moment, for the timeline's right-click menu.</summary>
    public (StoredSuggestion? Stretch, StoredBreak? Break) SuggestionAt(double seconds)
    {
        var at = TimeAt(seconds);
        return (_suggestions.Stretches.FirstOrDefault(s => s.IsOpen && s.Start <= at && at < s.End),
            _suggestions.Breaks.FirstOrDefault(b => b.IsOpen && b.Start <= at && at < b.End));
    }

    public void AcceptSuggestion(StoredSuggestion suggestion) => AcceptSuggestions([suggestion]);

    /// <summary>Accepts a stretch as another project than suggested.</summary>
    public void AcceptSuggestionAs(StoredSuggestion suggestion, long projectId)
    {
        new SuggestionStore(_history.Database).SetProject(suggestion.Id, projectId);
        AcceptSuggestions([suggestion]);
    }

    /// <summary>
    /// Accepts stretches as they're stored now: the panel's rows may differ by a click made since they were read. One worked
    /// out again meanwhile into something else isn't accepted; what's stored is shown instead, to be decided afresh.
    /// </summary>
    private void AcceptSuggestions(IReadOnlyList<StoredSuggestion> stretches)
    {
        var store = new SuggestionStore(_history.Database);
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        var stored = store.Get(dayStart, dayEnd).Stretches.Where(s => s.IsOpen).ToDictionary(s => s.Id);
        // The day as stored now, as the suggestions were worked out from: on today, newer than the captures shown.
        var captures = _history.LoadDay(Day.Day).Captures;
        var accepted = false;
        foreach (var stretch in stretches)
        {
            if (!stored.TryGetValue(stretch.Id, out var current))
                continue;
            // Each one accepted changes what the next leaves alone.
            store.Accept(current, captures, Projects.GetSpans(dayStart, dayEnd), _maxCaptureLength);
            accepted = true;
        }
        if (accepted)
            RebuildProjects();
        else
            ShowStoredSuggestions();
    }

    /// <summary>Gives an open stretch another project, as the Suggestions tab's project list does.</summary>
    public void ChangeSuggestionProject(StoredSuggestion suggestion, long projectId)
    {
        new SuggestionStore(_history.Database).SetProject(suggestion.Id, projectId);
        ShowStoredSuggestions();
    }

    public void RejectSuggestion(StoredSuggestion suggestion) => RejectSuggestions([suggestion]);

    private void RejectSuggestions(IReadOnlyList<StoredSuggestion> stretches)
    {
        var store = new SuggestionStore(_history.Database);
        foreach (var stretch in stretches)
            store.Reject(stretch.Id);
        ShowStoredSuggestions();
    }

    /// <summary>Counts a break toward a project: its time is set to it at once.</summary>
    public void CountBreak(StoredBreak suggestedBreak, long projectId)
    {
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        new SuggestionStore(_history.Database).Count(suggestedBreak, projectId, _history.LoadDay(Day.Day).Captures,
            Projects.GetSpans(dayStart, dayEnd), _maxCaptureLength);
        RebuildProjects();
    }

    /// <summary>Leaves a break out: it stays without a project, and isn't offered again.</summary>
    public void LeaveBreakOut(StoredBreak suggestedBreak)
    {
        new SuggestionStore(_history.Database).LeaveOut(suggestedBreak.Id);
        ShowStoredSuggestions();
    }

    /// <summary>Accepts the stretches the classifier is sure of, leaving the unsure ones and the breaks to decide.</summary>
    [RelayCommand(CanExecute = nameof(HasOpenSuggestions))]
    private void AcceptSureSuggestions() => AcceptSuggestions(_suggestions.Stretches.Where(s => s.IsOpen && !s.IsUnsure).ToList());

    /// <summary>Accepts every open stretch and leaves out the breaks not counted: the day is decided.</summary>
    [RelayCommand(CanExecute = nameof(HasOpenSuggestions))]
    private void AcceptAllSuggestions()
    {
        var store = new SuggestionStore(_history.Database);
        foreach (var suggestedBreak in _suggestions.Breaks.Where(b => b.IsOpen))
            store.LeaveOut(suggestedBreak.Id);
        AcceptSuggestions(_suggestions.Stretches.Where(s => s.IsOpen).ToList());
    }

    private void ChooseSuggestionProject(SuggestionRow row, ProjectOption project) => Dispatcher.UIThread.Post(() =>
    {
        if (project.Id is not { } id)
            return;
        new SuggestionStore(_history.Database).SetProject(row.Suggestion.Id, id);
        ShowStoredSuggestions();
    });

    /// <summary>Shows the day's suggestions as stored, after a decision (nothing to work out again).</summary>
    private void ShowStoredSuggestions()
    {
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        ShowSuggestions(new SuggestionStore(_history.Database).Get(dayStart, dayEnd));
    }
}
