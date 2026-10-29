using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.App.Controls;
using Worklapse.App.Theming;
using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.App.ViewModels;

/// <param name="Monitor">Null for all monitors.</param>
public sealed record MonitorChoice(MonitorRecord? Monitor, string Label)
{
    public long? MonitorId => Monitor?.Id;
    public override string ToString() => Label;
}

public sealed record StatItem(string Label, string Value, string Note, IBrush? Swatch);

/// <summary>One entry of the right-click menu: switches an app or site to <paramref name="MakeOnTask"/>.</summary>
public sealed record ClassificationChoice(string Label, ProgramKey Program, bool MakeOnTask);

/// <param name="More">Apps and sites in the selection beyond those listed.</param>
public sealed record ClassificationMenu(IReadOnlyList<ClassificationChoice> Choices, int More);

public sealed partial class MarkedRange : ObservableObject
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required int FirstIndex { get; init; }
    public required string RangeText { get; init; }
    public required string AppsText { get; init; }
    public required string LengthText { get; init; }
    public required IRelayCommand GoTo { get; init; }
    public required IRelayCommand Restore { get; init; }

    /// <summary>Inside the selection.</summary>
    [ObservableProperty] private bool _isPicked;
}

/// <summary>
/// State of Day review. Positions are indexes into the day's captures and the playhead is always on a capture. A
/// selection is a range of time: dragging snaps it to whole captures where something was recorded and to whole
/// minutes where nothing was, and its ends can be typed, so time with no recording can be given a project too.
/// <see cref="SelectionStart"/> and <see cref="SelectionEnd"/> are the captures inside it, if any.
/// </summary>
public sealed partial class DayReviewViewModel : ObservableObject, IDisposable
{
    private static readonly int[] SpeedSteps = [1, 3, 12, 36];

    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;
    private readonly SuggestionEngine _suggestionEngine;
    private readonly Action<DeleteResult>? _capturesDeleted;
    private readonly TimeSpan _maxCaptureLength;
    private readonly DispatcherTimer _playTimer;
    private IReadOnlyList<DateOnly> _days = [];
    private int? _anchor;
    private (DateTimeOffset Start, DateTimeOffset End)? _range;
    private Dictionary<long, MonitorRecord> _monitorsById = [];
    private IReadOnlyList<long>? _choicesFor;   // the monitors MonitorChoices lists
    private MonitorRecord? _chosenMonitor;      // picked by the user (null: all); kept while it isn't attached
    private bool _updatingChoices;

    /// <param name="capturesDeleted">Called after captures are deleted permanently, to remove their video from disk.</param>
    /// <param name="day">The day to open at its first capture; by default the latest day recorded, at its last.</param>
    /// <param name="suggestionEngine">The app's one suggestion engine, shared with the Overview.</param>
    public DayReviewViewModel(HistoryStore history, SettingsStore settings, SuggestionEngine suggestionEngine,
        Action<DeleteResult>? capturesDeleted = null, DateOnly? day = null)
    {
        _history = history;
        _settings = settings;
        _suggestionEngine = suggestionEngine;
        _undo = new UndoHistory(history.Database, UndoneTables);
        _focusOnApplication = settings.Current.ReviewFocusOnApplication;
        _panelWidth = settings.Current.ReviewPanelWidth;
        _capturesDeleted = capturesDeleted;
        _maxCaptureLength = settings.Current.MaxCaptureLength;
        // Not the constructor taking a callback: that one starts the timer, and Day review would play from the moment it
        // opens (unseen while it opened at a day's last capture; from the first, it played the day through).
        _playTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _playTimer.Tick += (_, _) => PlayTick();
        Speeds = ["Slow", "Normal", "Fast", "Very fast"];
        _speed = "Normal";
        LoadDays();
        LoadDay(day ?? (_days.Count > 0 ? _days[^1] : history.Today));
        if (Captures.Count > 0)
            MoveTo(day is null ? Captures.Count - 1 : 0);
    }

    /// <summary>Raised when what the timeline shows changes (data, selection or playhead).</summary>
    public event Action? TimelineChanged;

    /// <summary>Raised when the frame to show changes.</summary>
    public event Action? FrameChanged;

    public DayHistory Day { get; private set; } = new(DateOnly.MinValue, [], []);
    public IReadOnlyList<CaptureRecord> Captures => Day.Captures;
    public IReadOnlyList<TimelineSpan> Spans { get; private set; } = [];
    public (double Start, double End) View { get; private set; }

    public ObservableCollection<MonitorChoice> MonitorChoices { get; } = [];
    public ObservableCollection<StatItem> Stats { get; } = [];
    public ObservableCollection<MarkedRange> MarkedRanges { get; } = [];
    public IReadOnlyList<string> Speeds { get; }

    [ObservableProperty] private int _current = -1;
    [ObservableProperty] private int? _selectionStart;
    [ObservableProperty] private int? _selectionEnd;
    [ObservableProperty] private string _speed;
    [ObservableProperty] private MonitorChoice? _selectedMonitor;
    [ObservableProperty] private bool _isPlaying;

    /// <summary>Show only the window that had focus, not the monitors around it. Remembered in the settings.</summary>
    [ObservableProperty] private bool _focusOnApplication;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _dayText = "";
    [ObservableProperty] private string _frameTime = "";
    [ObservableProperty] private string _frameState = "";
    [ObservableProperty] private IBrush _frameStateBrush = AppBrushes.NotRecorded;
    [ObservableProperty] private string _summaryTitle = "";
    [ObservableProperty] private string _summaryRange = "";
    [ObservableProperty] private string _markedTotalText = "";
    [ObservableProperty] private string _confirmMessage = "";

    /// <summary>The monitors attached at the current capture, by id.</summary>
    public IReadOnlyList<MonitorRecord> CurrentMonitors => Current >= 0 && Current < Captures.Count
        ? Captures[Current].MonitorIds.Select(id => _monitorsById[id]).ToList()
        : [];

    public bool HasSelection => _range is not null;

    /// <summary>The selection has captures in it (to play, or to look at what was recorded).</summary>
    public bool HasCaptureSelection => SelectionStart is not null && SelectionEnd is not null;
    public bool HasMarked => MarkedRanges.Count > 0;
    public bool HasNoMarked => MarkedRanges.Count == 0;
    /// <summary>The days with captures, oldest first: the day arrows step between them.</summary>
    public IReadOnlyList<DateOnly> Days => _days;

    public bool CanGoNextDay => _days.Any(d => d > Day.Day);
    public bool CanGoPreviousDay => _days.Any(d => d < Day.Day);

    /// <summary>Today is shown: it grows while recording, so it can be read again.</summary>
    public bool IsToday => Day.Day == _history.Today;

    public string PlayText => IsPlaying ? "Pause" : "Play";

    // --- navigation ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanGoPreviousDay))]
    private void PreviousDay() => GoToDay(_days.Where(d => d < Day.Day).Max());

    [RelayCommand(CanExecute = nameof(CanGoNextDay))]
    private void NextDay() => GoToDay(_days.Where(d => d > Day.Day).Min());

    [RelayCommand]
    private void Refresh()
    {
        var keepId = Current >= 0 && Current < Captures.Count ? Captures[Current].Id : (long?)null;
        var atEnd = Current == Captures.Count - 1;
        LoadDays();
        Reload(keepId);
        if (atEnd && Captures.Count > 0)
            MoveTo(Captures.Count - 1);
        OnPropertyChanged(nameof(IsToday));
    }

    public void GoToDay(DateOnly day)
    {
        Stop();
        LoadDay(day);
        if (Captures.Count > 0)
            MoveTo(0);
    }

    [RelayCommand]
    private void PreviousFrame() => Step(-1, extendSelection: false);

    [RelayCommand]
    private void NextFrame() => Step(1, extendSelection: false);

    /// <summary>Moves the playhead to the neighbouring visible capture; with Shift, extends the selection.</summary>
    public void Step(int direction, bool extendSelection)
    {
        Stop();
        var next = NextVisible(Current + direction, direction);
        if (next < 0)
            return;

        if (!extendSelection)
        {
            _anchor = null;
            MoveTo(next);
            return;
        }

        if (_anchor is not { } anchor)
        {
            // Extending an existing selection from one of its ends keeps the other end fixed.
            anchor = (SelectionStart, SelectionEnd) switch
            {
                (int start, int end) when Current == start => end,
                (int start, int end) when Current == end => start,
                _ => Current,
            };
            _anchor = anchor;
        }
        SetSelection(Math.Min(anchor, next), Math.Max(anchor, next));
        ShowSelectionPanel(true);
        MoveTo(next);
    }

    /// <summary>Moves the playhead to the capture showing at a timeline position.</summary>
    public void Seek(double seconds)
    {
        Stop();
        _anchor = null;
        var index = IndexAt(seconds);
        if (index >= 0)
            MoveTo(index);
    }

    /// <summary>
    /// A drag on the timeline: selects the time between the two points, each end snapped to the capture it falls in, or
    /// to the minute where nothing was recorded. The playhead follows the pointer when there are captures to show.
    /// </summary>
    public void DragSelect(double anchorSeconds, double pointerSeconds, bool finished)
    {
        Stop();
        var (from, to) = anchorSeconds <= pointerSeconds ? (anchorSeconds, pointerSeconds) : (pointerSeconds, anchorSeconds);
        var start = SnapStart(from);
        var end = SnapEnd(to);
        if (end <= start)
            return;
        SetRange((start, end));
        ShowSelectionPanel(true);

        if (HasCaptureSelection)
        {
            var (first, last) = (SelectionStart!.Value, SelectionEnd!.Value);
            MoveTo(pointerSeconds >= anchorSeconds ? last : first);
            if (finished)
                _anchor = pointerSeconds >= anchorSeconds ? first : last;
        }
    }

    /// <summary>Selects a range typed in (or set otherwise); nothing when it doesn't end after it starts.</summary>
    public void SelectRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
            return;
        Stop();
        _anchor = null;
        SetRange((start, end));
        if (HasCaptureSelection && (Current < SelectionStart || Current > SelectionEnd))
            MoveTo(SelectionStart!.Value);
    }

    /// <summary>A selection's start at a timeline position: the start of the capture there, else that minute.</summary>
    private DateTimeOffset SnapStart(double seconds)
    {
        if (CaptureAtSeconds(seconds) is { } i)
            return Captures[i].Time;
        return TimeAt(Math.Floor(seconds / 60) * 60);
    }

    /// <summary>A selection's end at a timeline position: the end of the capture there, else the next minute.</summary>
    private DateTimeOffset SnapEnd(double seconds)
    {
        if (CaptureAtSeconds(seconds) is { } i)
            return Captures[i].Time + CaptureLength(i);
        return TimeAt(Math.Ceiling(seconds / 60) * 60);
    }

    /// <summary>The capture whose time covers a timeline position, if one does.</summary>
    private int? CaptureAtSeconds(double seconds)
    {
        for (var i = 0; i < Captures.Count; i++)
        {
            var start = Seconds(Captures[i]);
            if (start > seconds)
                break;
            if (seconds < start + CaptureLength(i).TotalSeconds)
                return i;
        }
        return null;
    }

    /// <summary>The moment at a timeline position (seconds after the day's local midnight).</summary>
    public DateTimeOffset TimeAt(double seconds)
    {
        var local = Day.Day.ToDateTime(TimeOnly.MinValue).AddSeconds(seconds);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void ClearSelection()
    {
        _anchor = null;
        _pickAnchor = null;
        SetSelection(null, null);
    }

    // --- playback -----------------------------------------------------------------------------

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            Stop();
            return;
        }
        if (Captures.Count == 0)
            return;
        // With a selection, play it from its start unless the playhead is already inside it.
        if (HasCaptureSelection && (Current < SelectionStart || Current >= SelectionEnd))
            MoveTo(SelectionStart!.Value);
        IsPlaying = true;
        _playTimer.Start();
    }

    private void Stop()
    {
        _playTimer.Stop();
        IsPlaying = false;
    }

    /// <summary>Pauses playback (Day review is no longer shown: playing on unseen would keep decoding).</summary>
    public void StopPlaying()
    {
        if (IsPlaying)
            Stop();
    }

    private void PlayTick()
    {
        var step = SpeedSteps[Math.Max(0, Speeds.ToList().IndexOf(Speed))];
        var end = HasCaptureSelection && Current <= SelectionEnd ? SelectionEnd!.Value : Captures.Count - 1;
        var next = Current;
        for (var i = 0; i < step && next >= 0 && next < end; i++)
            next = NextVisible(next + 1, 1);
        if (next < 0 || next >= end)
        {
            if (next >= 0)
                MoveTo(Math.Min(next, end));
            Stop();
            return;
        }
        MoveTo(next);
    }

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayText));

    // --- marking for deletion -----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MarkSelection()
    {
        if (_range is { } range)
            MarkForDeletion(range);
    }

    /// <summary>Marks the captures in a stretch of time for deletion; marking the selection also clears it.</summary>
    public void MarkForDeletion((DateTimeOffset Start, DateTimeOffset End) range)
    {
        if (_range == range)
            ClearSelection();
        MarkForDeletion([range]);
    }

    /// <summary>Marks the captures in stretches of time (an app's, say) for deletion.</summary>
    public void MarkForDeletion(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges)
    {
        if (ranges.Count == 0)
            return;
        var keepId = Current >= 0 && Current < Captures.Count ? Captures[Current].Id : (long?)null;
        Edit($"Mark for Deletion, {RangeText(ranges.Min(r => r.Start), ranges.Max(r => r.End))}", () =>
        {
            foreach (var (start, end) in ranges)
                _history.SetPendingDelete(start, end, pending: true);
        });
        ConfirmMessage = "";
        Reload(keepId);
    }

    [RelayCommand]
    private void ConfirmDeletes()
    {
        var keep = NextVisible(Current, 1) is var v and >= 0 ? Captures[v].Id : (long?)null;
        var removed = Enumerable.Range(0, Captures.Count).Where(i => Captures[i].PendingDelete).Sum(i => CaptureLength(i).TotalSeconds);
        var result = _history.DeleteMarked(Day.Day);
        // What's deleted for good can't come back, nor can edits made to it.
        ClearUndo();
        ConfirmMessage = result.Captures == 0 ? "" : $"Deleted {Duration(TimeSpan.FromSeconds(removed))}.";
        Reload(keep);
        if (result.Captures > 0)
            _capturesDeleted?.Invoke(result);
    }

    private void Restore(DateTimeOffset start, DateTimeOffset end)
    {
        Edit($"Restore, {RangeText(start, end)}", () => _history.SetPendingDelete(start, end, pending: false));
        ConfirmMessage = "";
        Reload(Current >= 0 ? Captures[Current].Id : null);
    }

    // --- on-task or not --------------------------------------------------------------------

    /// <summary>Apps and sites listed for a selection, longest first; the rest are on the Applications page.</summary>
    public const int MaxClassificationChoices = 12;

    /// <summary>
    /// What the right-click menu offers: for the current capture, its site and its app; for the selection, each
    /// app and site in it with its time there. Each one switches between On-task and Off-task.
    /// </summary>
    public ClassificationMenu ClassificationChoices(bool forSelection)
    {
        if (forSelection && HasSelection)
        {
            if (!HasCaptureSelection)
                return new ClassificationMenu([], 0);
            var (start, end) = SelectionTimes();
            // The captures the times count: each lasts until the next (at most the maximum), so one that began before the
            // selection and runs into it is in it too. By index from the first capture inside, its app was missing.
            var inSelection = Captures.Where((c, i) => c.Time < end && c.Time + CaptureLength(i) > start).ToList();
            var times = SpanSummary.ByProgram(Captures, start, end, _maxCaptureLength);
            var choices = times.Take(MaxClassificationChoices)
                .Select(t => Choice(t.Program, inSelection, t.Time))
                .ToList();
            return new ClassificationMenu(choices, times.Count - choices.Count);
        }

        if (Current < 0 || Current >= Captures.Count || !Captures[Current].IsActive)
            return new ClassificationMenu([], 0);
        var capture = Captures[Current];
        var programs = new List<ProgramKey>();
        if (capture.Site is { } site)
            programs.Add(new ProgramKey(ProgramKind.Site, site));
        if (capture.AppName is { } app)
            programs.Add(new ProgramKey(ProgramKind.App, app));
        return new ClassificationMenu(programs.Select(p => Choice(p, [capture], null)).ToList(), 0);
    }

    /// <summary>Marks an app or site; it applies to all history, and the timeline and totals update.</summary>
    public void SetOnTask(ProgramKey program, bool onTask)
    {
        Edit($"Mark {program.Name} as {(onTask ? "On-task" : "Off-task")}", () => _history.SetOnTask(program, onTask));
        Reload(Current >= 0 && Current < Captures.Count ? Captures[Current].Id : null);
    }

    /// <summary>Marks the captures in stretches of time on-task or off-task over their apps' and sites', or leaves them to those again (null).</summary>
    public void SetTaskMark(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges, bool? onTask)
    {
        if (ranges.Count == 0)
            return;
        var name = onTask switch { true => "Mark Time as On-task", false => "Mark Time as Off-task", null => "Mark Time as App or Site Default" };
        Edit($"{name}, {RangeText(ranges.Min(r => r.Start), ranges.Max(r => r.End))}", () =>
        {
            foreach (var (start, end) in ranges)
                _history.SetTaskMark(start, end, onTask);
        });
        Reload(Current >= 0 && Current < Captures.Count ? Captures[Current].Id : null);
    }

    /// <summary>Whether any capture in the stretches has a mark set by hand (so going back to the default would change something).</summary>
    public bool HasTaskMark(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges) =>
        Captures.Any(c => c.TaskMark is not null && ranges.Any(r => c.Time >= r.Start && c.Time < r.End));

    private static ClassificationChoice Choice(ProgramKey program, IReadOnlyList<CaptureRecord> captures, TimeSpan? time)
    {
        bool onTask;
        var browserWithSites = false;
        if (program.Kind == ProgramKind.Site)
        {
            onTask = captures.First(c => c.Site == program.Name).SiteOnTask == true;
        }
        else
        {
            var ofApp = captures.Where(c => string.Equals(c.AppName, program.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            onTask = ofApp[0].AppOnTask;
            // Its classification doesn't reach the pages with a known site, which follow the site.
            browserWithSites = ofApp.Any(c => c.Site is not null);
        }

        var label = $"Mark {program.Name} as {(onTask ? "Off-task" : "On-task")}";
        if (browserWithSites)
            label += " (pages without a known site)";
        if (time is { } t)
            label += $"  —  {Duration(t)}";
        return new ClassificationChoice(label, program, !onTask);
    }

    // --- monitor and speed choices ------------------------------------------------------------

    partial void OnFocusOnApplicationChanged(bool value)
    {
        _settings.Save(_settings.Current with { ReviewFocusOnApplication = value });
        FrameChanged?.Invoke();
    }

    partial void OnSelectedMonitorChanged(MonitorChoice? value)
    {
        if (_updatingChoices)
            return;
        _chosenMonitor = value?.Monitor;
        FrameChanged?.Invoke();
    }

    /// <summary>
    /// Lists the monitors attached at the current capture; the day can have more (plugging monitors in or out
    /// records them as new ones, often in the same place). Selects the monitor the user picked, else the one in
    /// its place, else all, and goes back to the picked one when it's attached again.
    /// </summary>
    private void UpdateMonitorChoices()
    {
        var attached = Current >= 0 && Current < Captures.Count ? Captures[Current].MonitorIds : [];
        if (_choicesFor is not null && attached.SequenceEqual(_choicesFor))
            return;
        _choicesFor = attached;

        _updatingChoices = true;
        MonitorChoices.Clear();
        MonitorChoices.Add(new MonitorChoice(null, "All monitors"));
        var number = 1;
        foreach (var monitor in attached.Select(id => _monitorsById[id]).OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X))
            MonitorChoices.Add(new MonitorChoice(monitor, monitor.IsTimeSnapper ? "TimeSnapper window"
                : $"{number++} — {monitor.Bounds.Width}×{monitor.Bounds.Height}{(monitor.IsPrimary ? " (primary)" : "")}"));
        SelectedMonitor = _chosenMonitor is not { } chosen ? MonitorChoices[0]
            : MonitorChoices.FirstOrDefault(c => c.MonitorId == chosen.Id)
              ?? MonitorChoices.FirstOrDefault(c => c.Monitor?.Bounds == chosen.Bounds)
              ?? MonitorChoices[0];
        _updatingChoices = false;
    }

    // --- loading ------------------------------------------------------------------------------

    private void LoadDays() => _days = _history.GetDays();

    private void LoadDay(DateOnly day)
    {
        SetDay(_history.LoadDay(day));
        ConfirmMessage = "";
        _anchor = null;
        _pickAnchor = null;
        _range = null;
        SelectionStart = SelectionEnd = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasCaptureSelection));
        ShowSelectionPanel(false);
        Current = -1;
        StopHoldingSuggestions();
        _checking = null;
        ClearUndo();
        RebuildDerived();
        UpdateMonitorChoices();

        DayText = Day.Day.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        Title = $"{Day.Day.ToString("D", CultureInfo.CurrentCulture)} — Day review — Worklapse";
        OnPropertyChanged(nameof(CanGoNextDay));
        OnPropertyChanged(nameof(CanGoPreviousDay));
        OnPropertyChanged(nameof(IsToday));
        PreviousDayCommand.NotifyCanExecuteChanged();
        NextDayCommand.NotifyCanExecuteChanged();
        UpdateFrameText();
        UpdateSummary();
        TimelineChanged?.Invoke();
        FrameChanged?.Invoke();
    }

    /// <summary>Reloads the current day after an edit, keeping the playhead on the same capture if it still exists.</summary>
    private void Reload(long? keepCaptureId)
    {
        SetDay(_history.LoadDay(Day.Day));

        // The playhead and the selection's captures are indexes into the old list, which a delete makes longer than
        // the new one: find them again in the reloaded day before anything is rebuilt from them.
        Current = -1;
        int Find(long id) => Captures.ToList().FindIndex(c => c.Id == id);
        SetRange(_range);
        RebuildDerived();

        var index = keepCaptureId is { } id ? Find(id) : -1;
        MoveTo(index >= 0 ? index : Math.Min(Captures.Count - 1, Math.Max(0, Current)));
        if (Current < 0)
        {
            // Nothing left to move to (everything deleted): clear the frame shown.
            UpdateFrameText();
            UpdateMonitorChoices();
            FrameChanged?.Invoke();
        }
        TimelineChanged?.Invoke();
    }

    private void SetDay(DayHistory day)
    {
        Day = day;
        _monitorsById = day.Monitors.ToDictionary(m => m.Id);
        // The records are new even when the same monitors are attached.
        _choicesFor = null;
        OnPropertyChanged(nameof(CanExportTimelapse));
    }

    private void RebuildDerived()
    {
        var spans = new List<TimelineSpan>();
        for (var i = 0; i < Captures.Count; i++)
        {
            var c = Captures[i];
            var start = Seconds(c);
            var end = start + CaptureLength(i).TotalSeconds;
            var kind = !c.IsActive ? TimelineKind.Idle : c.OnTask ? TimelineKind.OnTask : TimelineKind.OffTask;
            if (spans.Count > 0 && spans[^1] is var last && last.Kind == kind && last.Marked == c.PendingDelete && start - last.End < 1)
                spans[^1] = last with { End = end };
            else
                spans.Add(new TimelineSpan(start, end, kind, c.PendingDelete));
        }
        Spans = spans;
        // An empty day shows the working day, or from the day's start if that's later.
        var morning = Math.Max(8 * 3600, _history.DayStart.ToTimeSpan().TotalSeconds);
        View = Captures.Count == 0
            ? (morning, morning + 9 * 3600)
            : (Math.Floor(spans[0].Start / 1800) * 1800, Math.Ceiling(spans[^1].End / 1800) * 1800);

        MarkedRanges.Clear();
        for (var i = 0; i < Captures.Count; i++)
        {
            if (!Captures[i].PendingDelete)
                continue;
            var first = i;
            while (i + 1 < Captures.Count && Captures[i + 1].PendingDelete)
                i++;
            var last = i;
            var start = Captures[first].Time;
            var end = Captures[last].Time + CaptureLength(last);
            var apps = Captures.Skip(first).Take(last - first + 1).Select(c => c.AppName).Where(a => a is not null).Distinct();
            var length = TimeSpan.FromSeconds(Enumerable.Range(first, last - first + 1).Sum(k => CaptureLength(k).TotalSeconds));
            var goTo = first;
            MarkedRanges.Add(new MarkedRange
            {
                Start = start,
                End = end,
                FirstIndex = first,
                RangeText = $"{ClockShort(start)} – {ClockShort(end)}",
                AppsText = string.Join(", ", apps),
                LengthText = Duration(length),
                GoTo = new RelayCommand(() => { Stop(); MoveTo(goTo); }),
                Restore = new RelayCommand(() => Restore(start, end.AddMilliseconds(1))),
            });
        }
        var markedTotal = TimeSpan.FromSeconds(Enumerable.Range(0, Captures.Count).Where(k => Captures[k].PendingDelete).Sum(k => CaptureLength(k).TotalSeconds));
        MarkedTotalText = Duration(markedTotal);
        OnPropertyChanged(nameof(HasMarked));
        OnPropertyChanged(nameof(HasNoMarked));
        UpdateSummary();
        RebuildProjects(edited: false);
    }

    // --- derived text -------------------------------------------------------------------------

    private void MoveTo(int index)
    {
        if (index < 0 || index >= Captures.Count)
            return;
        Current = index;
        UpdateFrameText();
        UpdateMonitorChoices();
        TimelineChanged?.Invoke();
        FrameChanged?.Invoke();
    }

    /// <summary>Selects the captures from <paramref name="start"/> to <paramref name="end"/> (the time they cover), or nothing.</summary>
    private void SetSelection(int? start, int? end) =>
        SetRange(start is int s && end is int e ? (Captures[s].Time, Captures[e].Time + CaptureLength(e)) : null);

    /// <summary>Selects a range of time, or nothing; the captures in it become <see cref="SelectionStart"/>..<see cref="SelectionEnd"/>.</summary>
    private void SetRange((DateTimeOffset Start, DateTimeOffset End)? range)
    {
        _range = range;
        int? first = null, last = null;
        if (range is { } r)
            for (var i = 0; i < Captures.Count; i++)
            {
                if (Captures[i].Time < r.Start || Captures[i].Time >= r.End)
                    continue;
                first ??= i;
                last = i;
            }
        SelectionStart = first;
        SelectionEnd = last;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasCaptureSelection));
        if (range is null)
            ShowSelectionPanel(false);
        OnPropertyChanged(nameof(ShowsDay));
        OnPropertyChanged(nameof(ShowsSelection));
        MarkSelectionCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        UpdateSummary();
        SelectionChanged();
        TimelineChanged?.Invoke();
    }

    private void UpdateFrameText()
    {
        UpdateFrameSegment();
        if (Current < 0 || Current >= Captures.Count)
        {
            FrameTime = "No captures";
            FrameState = "";
            FrameStateBrush = AppBrushes.NotRecorded;
            return;
        }
        var c = Captures[Current];
        FrameTime = c.Time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        (FrameState, FrameStateBrush) = c.PendingDelete ? ("Marked for deletion", AppBrushes.Marked)
            : !c.IsActive ? ("Idle", AppBrushes.Idle)
            : c.TaskMark is { } onTask ? (onTask ? "On-task (marked)" : "Off-task (marked)", onTask ? AppBrushes.OnTask : AppBrushes.OffTask)
            : c.OnTask ? ("On-task", AppBrushes.OnTask)
            : ("Off-task", AppBrushes.OffTask);
    }

    private void UpdateSummary()
    {
        Stats.Clear();
        if (Captures.Count == 0)
        {
            SummaryTitle = "No recordings this day";
            SummaryRange = "";
            return;
        }

        var (start, end) = HasSelection ? SelectionTimes() : (Captures[0].Time, Captures[^1].Time + CaptureLength(Captures.Count - 1));
        var summary = SpanSummary.For(Captures, start, end, _maxCaptureLength);
        SummaryTitle = HasSelection ? "Selection" : "Whole day";
        SummaryRange = $"{Clock(start)} – {Clock(end)}";
        var activeShare = summary.Length.Ticks == 0 ? 0 : (int)Math.Round(100.0 * summary.Active.Ticks / summary.Length.Ticks);
        var onTaskShare = summary.Active.Ticks == 0 ? 0 : (int)Math.Round(100.0 * summary.OnTask.Ticks / summary.Active.Ticks);
        Stats.Add(new StatItem("Length", Duration(summary.Length), "", null));
        Stats.Add(new StatItem("Active", Duration(summary.Active), $"({activeShare}%)", null));
        Stats.Add(new StatItem("On-task", Duration(summary.OnTask), $"({onTaskShare}% of active)", AppBrushes.OnTask));
        Stats.Add(new StatItem("Off-task", Duration(summary.OffTask), "", AppBrushes.OffTask));
        Stats.Add(new StatItem("Idle", Duration(summary.Idle), "", AppBrushes.Idle));
        if (summary.NotRecorded >= TimeSpan.FromMinutes(1))
            Stats.Add(new StatItem("Not recorded", Duration(summary.NotRecorded), "", null));
        if (summary.Marked > TimeSpan.Zero)
            Stats.Add(new StatItem("Marked", Duration(summary.Marked), "(not counted)", AppBrushes.Marked));
    }

    // --- helpers ------------------------------------------------------------------------------

    public double Seconds(CaptureRecord capture)
    {
        var local = capture.Time.ToLocalTime().DateTime;
        return (local - Day.Day.ToDateTime(TimeOnly.MinValue)).TotalSeconds;
    }

    public double? PlayheadSeconds => Current >= 0 && Current < Captures.Count ? Seconds(Captures[Current]) : null;

    public (double, double)? SelectionSeconds => _range is { } r ? (SecondsOf(r.Start), SecondsOf(r.End)) : null;

    /// <summary>The selection's time, if there is one.</summary>
    public (DateTimeOffset Start, DateTimeOffset End)? SelectionRange => _range;

    private (DateTimeOffset Start, DateTimeOffset End) SelectionTimes() => _range!.Value;

    /// <summary>Seconds after the day's local midnight.</summary>
    public double SecondsOf(DateTimeOffset time) => (time.ToLocalTime().DateTime - Day.Day.ToDateTime(TimeOnly.MinValue)).TotalSeconds;

    /// <summary>
    /// The time a capture stands for: until the next one, at most the maximum, which the last one lasts too. The same
    /// rule as the totals (<see cref="SpanSummary"/>) and the projects (<see cref="ProjectTimeline"/>).
    /// </summary>
    private TimeSpan CaptureLength(int index)
    {
        if (index + 1 < Captures.Count)
        {
            var gap = Captures[index + 1].Time - Captures[index].Time;
            return gap < _maxCaptureLength ? gap : _maxCaptureLength;
        }
        return _maxCaptureLength;
    }

    /// <summary>The nearest capture at or past <paramref name="index"/> in a direction that isn't marked for deletion.</summary>
    private int NextVisible(int index, int direction)
    {
        for (; index >= 0 && index < Captures.Count; index += direction)
            if (!Captures[index].PendingDelete)
                return index;
        return -1;
    }

    private int IndexAt(double seconds)
    {
        var best = -1;
        for (var i = 0; i < Captures.Count; i++)
        {
            if (Seconds(Captures[i]) > seconds)
                break;
            best = i;
        }
        return best >= 0 ? best : (Captures.Count > 0 ? 0 : -1);
    }

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    private static string Duration(TimeSpan span) => DurationText.Of(span);

    public void Dispose()
    {
        _playTimer.Stop();
        // Suggestions still being worked out are dropped when they arrive.
        Interlocked.Increment(ref _suggestionVersion);
        _history.Dispose();
    }
}
