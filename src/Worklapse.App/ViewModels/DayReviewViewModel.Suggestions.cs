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

/// <summary>
/// A suggested block on the Suggestions tab: its project (which can be changed before accepting), its time, what it adds,
/// and the words it rests on. Its breaks and switches follow it as <see cref="SuggestionPartRow"/>s.
/// </summary>
public sealed class SuggestionHeadRow : ObservableObject
{
    private readonly Action<SuggestionHeadRow, ProjectOption> _chooseProject;
    private ProjectOption _project;

    public SuggestionHeadRow(StoredSuggestion suggestion, ProjectOption project, string rangeText, string lengthText, string addsText,
        string evidenceText, string screenText, bool isFirst, Action<SuggestionHeadRow, ProjectOption> chooseProject,
        Action<StoredSuggestion> accept, Action<StoredSuggestion> reject)
    {
        Suggestion = suggestion;
        _project = project;
        RangeText = rangeText;
        LengthText = lengthText;
        AddsText = addsText;
        EvidenceText = evidenceText;
        ScreenText = screenText;
        IsFirst = isFirst;
        _chooseProject = chooseProject;
        Accept = new RelayCommand(() => accept(suggestion));
        Reject = new RelayCommand(() => reject(suggestion));
    }

    public StoredSuggestion Suggestion { get; }
    public string RangeText { get; }
    public string LengthText { get; }
    public string AddsText { get; }
    public bool IsUnsure => Suggestion.IsUnsure;
    public string EvidenceText { get; }
    public bool HasEvidenceText => EvidenceText.Length > 0;
    public string ScreenText { get; }
    public bool HasScreenText => ScreenText.Length > 0;
    public bool IsFirst { get; }
    public IRelayCommand Accept { get; }
    public IRelayCommand Reject { get; }

    public ProjectOption Project
    {
        get => _project;
        set
        {
            // A list that loses its selection hands back null, and None isn't a project to give the block.
            if (value is null || value.IsNone || value == _project)
                return;
            _project = value;
            OnPropertyChanged();
            _chooseProject(this, value);
        }
    }
}

/// <summary>A break (counted toward the block or not) or a switch (split out as its project, or kept in) of a suggestion.</summary>
public sealed class SuggestionPartRow : ObservableObject
{
    private readonly Action<SuggestionPartRow, bool> _decide;
    private bool _decided;

    public SuggestionPartRow(StoredSuggestion suggestion, StoredPart part, string whatText, string rangeText, string lengthText,
        IBrush brush, string tip, Action<SuggestionPartRow, bool> decide)
    {
        Suggestion = suggestion;
        Part = part;
        WhatText = whatText;
        RangeText = rangeText;
        LengthText = lengthText;
        Brush = brush;
        Tip = tip;
        _decided = part.Decided;
        _decide = decide;
    }

    public StoredSuggestion Suggestion { get; }
    public StoredPart Part { get; }
    public string WhatText { get; }
    public string RangeText { get; }
    public string LengthText { get; }

    /// <summary>The block's project's colour for a break; for a switch, its own project's.</summary>
    public IBrush Brush { get; }
    public bool IsSwitch => Part.Kind == SuggestionPartKind.Switch;
    public bool IsBreak => !IsSwitch;
    public string DecideText => IsSwitch ? "Keep" : "Count";
    public string Tip { get; }

    /// <summary>A break: counted toward the block. A switch: kept in the block instead of split out.</summary>
    public bool Decided
    {
        get => _decided;
        set
        {
            if (!SetProperty(ref _decided, value))
                return;
            _decide(this, value);
        }
    }
}

/// <summary>
/// A piece of an open suggestion on the projects lane, in the project it would go to: a stretch between its breaks and
/// switches, or one of those. Drawn around the time inside it that has a project already.
/// </summary>
public sealed record SuggestionPiece(StoredSuggestion Suggestion, DateTimeOffset Start, DateTimeOffset End, long ProjectId, LaneStyle Style)
{
    /// <summary>An undecided break goes to no project, so it can't be accepted on its own.</summary>
    public bool CanAccept => Style != LaneStyle.Break;
}

public sealed partial class DayReviewViewModel
{
    // Words a suggestion shows as its evidence, at most, of each kind.
    private const int EvidenceShown = 6;

    private int _suggestionVersion;
    private List<StoredSuggestion> _suggestions = [];
    private List<SuggestionPiece> _suggestionPieces = [];

    /// <summary>
    /// After an edit (an accept, a range set by hand), suggestions worked out again wait for <see cref="ShowNewSuggestions"/>
    /// instead of replacing the list being worked through; also when the day is reloaded meanwhile (the window activated).
    /// Until another day is opened, they're shown, or working them out again changes nothing.
    /// </summary>
    private bool _holdSuggestions;
    private WorkedOutDay? _newSuggestions;

    /// <summary>The Suggestions tab: each open suggestion, then its breaks and switches.</summary>
    public ObservableCollection<object> SuggestionRows { get; } = [];

    [ObservableProperty] private int _openSuggestionCount;
    [ObservableProperty] private string _suggestionSummary = "";
    [ObservableProperty] private bool _isWorkingOutSuggestions;
    [ObservableProperty] private bool _hasNewSuggestions;

    public bool HasOpenSuggestions => OpenSuggestionCount > 0;
    public bool HasNoOpenSuggestions => OpenSuggestionCount == 0;
    public bool SuggestionsOff => !_settings.Current.SuggestProjects;

    /// <summary>"No suggestions for this day": not while they're being worked out, nor when they're off.</summary>
    public bool ShowsNoSuggestions => !IsWorkingOutSuggestions && !SuggestionsOff;

    partial void OnIsWorkingOutSuggestionsChanged(bool value) => OnPropertyChanged(nameof(ShowsNoSuggestions));

    partial void OnOpenSuggestionCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOpenSuggestions));
        OnPropertyChanged(nameof(HasNoOpenSuggestions));
        AcceptAllSuggestionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Shows the day's suggestions as stored, then works them out again in the background (the classifier may train on
    /// the history first) and shows those when they're ready, unless the day or its projects changed meanwhile. While
    /// held (<see cref="_holdSuggestions"/>), ones that change the blocks wait to be asked for instead.
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
            ShowSuggestions([]);
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

    /// <summary>Lists the open suggestions and draws them on the projects lane.</summary>
    private void ShowSuggestions(List<StoredSuggestion> suggestions)
    {
        _suggestions = suggestions;
        SuggestionRows.Clear();
        var open = suggestions.Where(s => s.IsOpen).ToList();
        var adds = TimeSpan.Zero;
        foreach (var suggestion in open)
        {
            var add = Adds(suggestion);
            adds += add;
            // The words pointed to the project computed; once another is chosen they'd only mislead.
            var (evidence, screen) = suggestion.State == SuggestionState.Chosen ? ("", "") : EvidenceTexts(suggestion.Evidence);
            SuggestionRows.Add(new SuggestionHeadRow(suggestion, OptionOf(suggestion.ProjectId),
                RangeText(suggestion.Start, suggestion.End), ProjectTimes.Hm(suggestion.End - suggestion.Start),
                add > TimeSpan.Zero ? $"Adds {ProjectTimes.Hm(add)} without a project"
                    : suggestion.Parts.Count == 1 ? "One break to decide" : $"{suggestion.Parts.Count} breaks or switches to decide",
                evidence, screen, SuggestionRows.Count == 0, ChooseSuggestionProject, AcceptSuggestion, RejectSuggestion));
            foreach (var part in suggestion.Parts)
                SuggestionRows.Add(PartRow(suggestion, part));
        }
        OpenSuggestionCount = open.Count;
        SuggestionSummary = open.Count == 0 ? ""
            : $"{open.Count} {(open.Count == 1 ? "suggestion" : "suggestions")} · adds {ProjectTimes.Hm(adds)}";
        BuildProjectLane();
        TimelineChanged?.Invoke();
    }

    private SuggestionPartRow PartRow(StoredSuggestion suggestion, StoredPart part)
    {
        var range = RangeText(part.Start, part.End);
        var length = ProjectTimes.Hm(part.End - part.Start);
        var block = NameOf(suggestion.ProjectId);
        if (part.Kind == SuggestionPartKind.Switch && part.ProjectId is { } other)
            return new SuggestionPartRow(suggestion, part, $"Switch: {NameOf(other)} · {WhatWasThere(part.Start, part.End)}", range, length,
                BrushOf(other), $"Kept, it goes to {block}; otherwise it's split out as {NameOf(other)}", DecidePart);
        var what = WhatWasThere(part.Start, part.End);
        return new SuggestionPartRow(suggestion, part, what is "Idle" or "Not recorded" ? what : $"Away: {what}",
            range, length, BrushOf(suggestion.ProjectId), $"Counted, it goes to {block} with the block", DecidePart);
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

    /// <summary>
    /// The time a suggestion gives its project: its stretch without what already has a project and without its breaks and
    /// switches as they're decided now.
    /// </summary>
    private TimeSpan Adds(StoredSuggestion suggestion)
    {
        var free = SuggestionStore.Subtract([(suggestion.Start, suggestion.End)], _projectTime.Blocks.Select(b => (b.Start, b.End)));
        var left = SuggestionStore.Subtract(free, suggestion.Parts.Where(p => !p.Decided).Select(p => (p.Start, p.End)));
        return left.Aggregate(TimeSpan.Zero, (sum, r) => sum + (r.End - r.Start));
    }

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
    /// The projects lane: time marked with a project filled; open suggestions outlined over the time without one, their
    /// undecided breaks dashed, switches split out in their own project's colour.
    /// </summary>
    private void BuildProjectLane()
    {
        var lane = _projectTime.Blocks
            .Select(b => new ProjectLaneBlock(SecondsOf(b.Start), SecondsOf(b.End), BrushOf(b.ProjectId), NameOf(b.ProjectId)))
            .ToList();
        var pieces = new List<SuggestionPiece>();
        var taken = _projectTime.Blocks.Select(b => (b.Start, b.End)).ToList();
        foreach (var suggestion in _suggestions.Where(s => s.IsOpen))
        {
            // Under its pieces, so they read as one block across its breaks and the time between that has a project.
            lane.Add(new ProjectLaneBlock(SecondsOf(suggestion.Start), SecondsOf(suggestion.End), BrushOf(suggestion.ProjectId), "", LaneStyle.Extent));
            // Its pieces: the stretches between its breaks and switches, and those; each drawn around the time that
            // has a project already.
            foreach (var (start, end) in SuggestionStore.Subtract([(suggestion.Start, suggestion.End)], suggestion.Parts.Select(p => (p.Start, p.End))))
                pieces.Add(new SuggestionPiece(suggestion, start, end, suggestion.ProjectId, LaneStyle.Suggested));
            foreach (var part in suggestion.Parts)
            {
                var (project, style) = part switch
                {
                    { Kind: SuggestionPartKind.Switch, Decided: false, ProjectId: { } other } => (other, LaneStyle.Suggested),
                    // A break, or a switch whose project was deleted since: left out on Accept unless counted.
                    { Decided: false } => (suggestion.ProjectId, LaneStyle.Break),
                    _ => (suggestion.ProjectId, LaneStyle.Suggested),
                };
                pieces.Add(new SuggestionPiece(suggestion, part.Start, part.End, project, style));
            }
        }
        // A piece whose time all has a project already shows nothing, so it isn't one.
        pieces.RemoveAll(piece => SuggestionStore.Subtract([(piece.Start, piece.End)], taken) is { Count: 0 });
        foreach (var piece in pieces)
            foreach (var (start, end) in SuggestionStore.Subtract([(piece.Start, piece.End)], taken))
                lane.Add(new ProjectLaneBlock(SecondsOf(start), SecondsOf(end), BrushOf(piece.ProjectId), NameOf(piece.ProjectId), piece.Style));
        _suggestionPieces = pieces;
        ProjectLane = lane;
    }

    /// <summary>A project's name, for menus.</summary>
    public string ProjectName(long projectId) => NameOf(projectId);

    /// <summary>
    /// The open suggestion at a moment, its part there (if any), and the piece drawn there (none between pieces), for the
    /// timeline's right-click menu.
    /// </summary>
    public (StoredSuggestion Suggestion, StoredPart? Part, SuggestionPiece? Piece)? SuggestionAt(double seconds)
    {
        var at = TimeAt(seconds);
        foreach (var suggestion in _suggestions.Where(s => s.IsOpen && s.Start <= at && at < s.End))
            return (suggestion, suggestion.Parts.FirstOrDefault(p => p.Start <= at && at < p.End),
                _suggestionPieces.FirstOrDefault(p => p.Suggestion == suggestion && p.Start <= at && at < p.End));
        return null;
    }

    /// <summary>How many pieces a suggestion is drawn as, breaks included.</summary>
    public int PieceCount(StoredSuggestion suggestion) => _suggestionPieces.Count(p => p.Suggestion == suggestion);

    public void AcceptSuggestion(StoredSuggestion suggestion)
    {
        var store = new SuggestionStore(_history.Database);
        // As decided now: the panel's rows and the stored ones may differ by a click made since they were read. When it
        // was worked out again meanwhile into something else, that's shown instead, to be decided afresh.
        if (store.Get(suggestion.Start, suggestion.End).FirstOrDefault(s => s.Id == suggestion.Id && s.IsOpen) is not { } current)
        {
            ShowStoredSuggestions();
            return;
        }
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        // The day as stored now, as the suggestion was worked out from: on today, newer than the captures shown.
        store.Accept(current, _history.LoadDay(Day.Day).Captures, Projects.GetSpans(dayStart, dayEnd), _maxCaptureLength);
        RebuildProjects();
    }

    /// <summary>Accepts one piece of a suggestion as the project it's drawn in, leaving the rest of the suggestion open.</summary>
    public void AcceptPiece(SuggestionPiece piece)
    {
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        new SuggestionStore(_history.Database).AcceptPiece(piece.Start, piece.End, piece.ProjectId,
            _history.LoadDay(Day.Day).Captures, Projects.GetSpans(dayStart, dayEnd), _maxCaptureLength);
        RebuildProjects();
    }

    /// <summary>Gives an open suggestion another project, as the Suggestions tab's project list does.</summary>
    public void ChangeSuggestionProject(StoredSuggestion suggestion, long projectId)
    {
        new SuggestionStore(_history.Database).SetProject(suggestion.Id, projectId);
        ShowStoredSuggestions();
    }

    public void RejectSuggestion(StoredSuggestion suggestion)
    {
        new SuggestionStore(_history.Database).Reject(suggestion.Id);
        ShowStoredSuggestions();
    }

    /// <summary>Decides a break (counted) or a switch (kept in the block) of an open suggestion.</summary>
    public void DecidePart(StoredSuggestion suggestion, StoredPart part, bool decided)
    {
        new SuggestionStore(_history.Database).SetPart(suggestion.Id, part.Start, decided);
        ShowStoredSuggestions();
    }

    [RelayCommand(CanExecute = nameof(HasOpenSuggestions))]
    private void AcceptAllSuggestions()
    {
        var store = new SuggestionStore(_history.Database);
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        var captures = _history.LoadDay(Day.Day).Captures;
        foreach (var suggestion in store.Get(dayStart, dayEnd).Where(s => s.IsOpen))
        {
            // Each accepted block changes what the next one leaves alone.
            store.Accept(suggestion, captures, Projects.GetSpans(dayStart, dayEnd), _maxCaptureLength);
        }
        RebuildProjects();
    }

    private void DecidePart(SuggestionPartRow row, bool decided) =>
        Dispatcher.UIThread.Post(() => DecidePart(row.Suggestion, row.Part, decided));

    private void ChooseSuggestionProject(SuggestionHeadRow row, ProjectOption project) => Dispatcher.UIThread.Post(() =>
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
