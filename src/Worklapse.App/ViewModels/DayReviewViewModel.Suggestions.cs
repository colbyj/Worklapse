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

public sealed partial class DayReviewViewModel
{
    // Words a suggestion shows as its evidence, at most, of each kind.
    private const int EvidenceShown = 6;

    private readonly SuggestionEngine _engine = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private int _suggestionVersion;
    private List<StoredSuggestion> _suggestions = [];

    /// <summary>The Suggestions tab: each open suggestion, then its breaks and switches.</summary>
    public ObservableCollection<object> SuggestionRows { get; } = [];

    [ObservableProperty] private int _openSuggestionCount;
    [ObservableProperty] private string _suggestionSummary = "";
    [ObservableProperty] private bool _isWorkingOutSuggestions;

    public bool HasOpenSuggestions => OpenSuggestionCount > 0;
    public bool HasNoOpenSuggestions => OpenSuggestionCount == 0;
    public bool SuggestionsOff => !_settings.Current.SuggestProjects;

    partial void OnOpenSuggestionCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasOpenSuggestions));
        OnPropertyChanged(nameof(HasNoOpenSuggestions));
        AcceptAllSuggestionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Shows the day's suggestions as stored, then works them out again in the background (the classifier may train on
    /// the history first) and shows those when they're ready, unless the day or its projects changed meanwhile.
    /// </summary>
    private void RefreshSuggestions()
    {
        var version = Interlocked.Increment(ref _suggestionVersion);
        var settings = _settings.Current;
        OnPropertyChanged(nameof(SuggestionsOff));
        if (!settings.SuggestProjects)
        {
            ShowSuggestions([]);
            return;
        }
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        ShowSuggestions(new SuggestionStore(_history.Database).Get(dayStart, dayEnd));

        var (day, folder, start) = (Day.Day, _history.Database.DataFolder, _history.DayStart);
        IsWorkingOutSuggestions = true;
        Task.Run(async () =>
        {
            await _engineGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _suggestionVersion) != version)
                    return null; // asked again since: that one will do it
                using var history = new HistoryStore(WorklapseDatabase.Open(folder), start);
                return _engine.Refresh(history, day, settings);
            }
            finally
            {
                _engineGate.Release();
            }
        }).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (version != _suggestionVersion)
                return;
            IsWorkingOutSuggestions = false;
            if (task.Exception is { } failed)
                Log.Error("Working out the day's suggestions failed.", failed.GetBaseException());
            else if (task.Result is { } worked && Day.Day == day)
                ShowSuggestions(worked);
        }), TaskScheduler.Default);
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
            var (evidence, screen) = EvidenceTexts(suggestion.Evidence);
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
        var taken = _projectTime.Blocks.Select(b => (b.Start, b.End)).ToList();
        foreach (var suggestion in _suggestions.Where(s => s.IsOpen))
        {
            var free = SuggestionStore.Subtract([(suggestion.Start, suggestion.End)], taken);
            foreach (var (start, end) in SuggestionStore.Subtract(free, suggestion.Parts.Select(p => (p.Start, p.End))))
                lane.Add(new ProjectLaneBlock(SecondsOf(start), SecondsOf(end), BrushOf(suggestion.ProjectId), NameOf(suggestion.ProjectId), LaneStyle.Suggested));
            foreach (var part in suggestion.Parts)
                foreach (var (start, end) in SuggestionStore.Subtract([(part.Start, part.End)], taken))
                {
                    var (project, style) = part switch
                    {
                        { Kind: SuggestionPartKind.Switch, Decided: false, ProjectId: { } other } => (other, LaneStyle.Suggested),
                        { Kind: SuggestionPartKind.Break, Decided: false } => (suggestion.ProjectId, LaneStyle.Break),
                        _ => (suggestion.ProjectId, LaneStyle.Suggested),
                    };
                    lane.Add(new ProjectLaneBlock(SecondsOf(start), SecondsOf(end), BrushOf(project), NameOf(project), style));
                }
        }
        ProjectLane = lane;
    }

    /// <summary>A project's name, for menus.</summary>
    public string ProjectName(long projectId) => NameOf(projectId);

    /// <summary>The open suggestion (and its part, if any) at a moment, for the timeline's right-click menu.</summary>
    public (StoredSuggestion Suggestion, StoredPart? Part)? SuggestionAt(double seconds)
    {
        var at = TimeAt(seconds);
        foreach (var suggestion in _suggestions.Where(s => s.IsOpen && s.Start <= at && at < s.End))
            return (suggestion, suggestion.Parts.FirstOrDefault(p => p.Start <= at && at < p.End));
        return null;
    }

    public void AcceptSuggestion(StoredSuggestion suggestion)
    {
        var store = new SuggestionStore(_history.Database);
        // As decided now: the panel's rows and the stored ones may differ by a click made since they were read.
        if (store.Get(suggestion.Start, suggestion.End).FirstOrDefault(s => s.Id == suggestion.Id && s.IsOpen) is not { } current)
            return;
        store.Accept(current, Captures, _handSpans, _maxCaptureLength);
        RebuildProjects();
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
        foreach (var suggestion in store.Get(dayStart, dayEnd).Where(s => s.IsOpen))
        {
            // Each accepted block changes what the next one leaves alone.
            store.Accept(suggestion, Captures, Projects.GetSpans(dayStart, dayEnd), _maxCaptureLength);
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
