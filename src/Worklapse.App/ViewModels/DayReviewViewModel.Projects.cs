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

namespace Worklapse.App.ViewModels;

/// <summary>A project to pick in Day review's lists and menus; <see cref="IsNone"/> for "None".</summary>
public sealed record ProjectOption(long? Id, string Name, IBrush Brush, string Category)
{
    public bool IsNone => Id is null;
    public override string ToString() => Name;
}

/// <summary>
/// An app or site with its two marks, Productive and Project, which Day review saves as soon as they change.
/// Productive applies to the app or site on every day; what Project applies to is the row's (the app's link, or a
/// stretch of time).
/// </summary>
public abstract partial class ProgramMarksRow : ObservableObject
{
    private readonly Action<ProgramKey, bool>? _setProductive;
    private readonly Action<ProgramKey, ProjectOption>? _setProject;
    private bool _productive;
    private ProjectOption? _project;

    /// <param name="project">Null when it can't be one choice (a stretch of time partly under one project).</param>
    protected ProgramMarksRow(ProgramKey? program, bool productive, ProjectOption? project,
        Action<ProgramKey, bool>? setProductive, Action<ProgramKey, ProjectOption>? setProject)
    {
        Program = program;
        _productive = productive;
        _project = project;
        _setProductive = setProductive;
        _setProject = setProject;
    }

    /// <summary>Null for idle or unrecorded time, which has neither mark.</summary>
    public ProgramKey? Program { get; }

    public bool HasProgram => Program is not null;

    public bool Productive
    {
        get => _productive;
        set
        {
            if (SetProperty(ref _productive, value) && Program is { } program)
                _setProductive?.Invoke(program, value);
        }
    }

    public ProjectOption? Project
    {
        get => _project;
        set
        {
            // A list that loses its selection hands back null: that isn't a choice.
            if (value is null || value == _project)
                return;
            _project = value;
            OnPropertyChanged();
            if (Program is { } program)
                _setProject?.Invoke(program, value);
        }
    }
}

/// <summary>An app or site used on the day, on the Applications tab.</summary>
public sealed class DayAppRow(ProgramKey program, TimeSpan active, bool productive, ProjectOption project,
    Action<ProgramKey, bool> setProductive, Action<ProgramKey, ProjectOption> setProject)
    : ProgramMarksRow(program, productive, project, setProductive, setProject)
{
    public string Name => Program!.Value.Name;
    public string ActiveText => DurationText.Of(active);
}

/// <summary>
/// A recorded segment: one app or site in a row, or idle, or not recorded (in a selection, on the Recorded Segments
/// tab, or the one the playhead is in). Its Project is the project of that stretch of time only (set by hand);
/// Productive is the app's or site's.
/// </summary>
public sealed class SegmentRow(DateTimeOffset from, DateTimeOffset to, string range, string name, ProgramKey? program, bool productive,
    ProjectOption? project, Action<ProgramKey, bool> setProductive, Action<ProgramKey, ProjectOption> setProject)
    : ProgramMarksRow(program, productive, project, setProductive, setProject)
{
    private bool _isPicked;

    public DateTimeOffset From => from;
    public DateTimeOffset To => to;
    public string RangeText => range;
    public string Name => name;

    /// <summary>Inside the selection (on the day's list).</summary>
    public bool IsPicked
    {
        get => _isPicked;
        set => SetProperty(ref _isPicked, value);
    }

    /// <summary>Idle or not recorded time marked with a project (by hand): shown without a list to change it in.</summary>
    public bool ShowsProjectName => !HasProgram && Project is not { IsNone: true };
    public string ProjectName => Project?.Name ?? "Mixed";
    public IBrush ProjectBrush => Project?.Brush ?? Brushes.Transparent;

    /// <summary>h:mm, as on the Projects tab; seconds for a segment under a minute.</summary>
    public string LengthText => to - from < TimeSpan.FromMinutes(1) ? DurationText.Of(to - from) : ProjectTimes.Hm(to - from);
    public FontStyle NameStyle => HasProgram ? FontStyle.Normal : FontStyle.Italic;
}

/// <summary>
/// A project's time on the day, on the Time Log tab, with its note for the day. Logged is typed (h:mm); both are saved
/// to the timesheet.
/// </summary>
public sealed partial class TimeLogRow : ObservableObject
{
    private readonly Func<TimeLogRow, string, string> _saveNote;
    private readonly Func<TimeLogRow, string, string> _saveLogged;
    private string _noteText;
    private string _loggedText;

    public TimeLogRow(long projectId, string name, IBrush brush, TimeSpan assigned, string note, TimeSpan? logged,
        Func<TimeLogRow, string, string> saveNote, Func<TimeLogRow, string, string> saveLogged)
    {
        ProjectId = projectId;
        Name = name;
        Brush = brush;
        AssignedText = ProjectTimes.Hm(assigned);
        _noteText = note;
        _loggedText = logged is { } l ? ProjectTimes.Hm(l) : "";
        _saveNote = saveNote;
        _saveLogged = saveLogged;
    }

    public long ProjectId { get; }
    public string Name { get; }
    public IBrush Brush { get; }
    public string AssignedText { get; }

    public string NoteText
    {
        get => _noteText;
        set
        {
            value ??= "";
            if (value == _noteText)
                return;
            _noteText = _saveNote(this, value);
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(NoteText)));
        }
    }

    public string LoggedText
    {
        get => _loggedText;
        set
        {
            value ??= "";
            if (value == _loggedText)
                return;
            // Saved as typed when it reads as a time; shown back the way it's stored (or as it was, if it doesn't read).
            _loggedText = _saveLogged(this, value);
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LoggedText)));
        }
    }
}

internal static class ProjectTimes
{
    /// <summary>"4:45": hours and minutes, as on a timesheet.</summary>
    public static string Hm(TimeSpan time)
    {
        var minutes = (int)Math.Round(time.TotalMinutes);
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>Reads "4:45", "4.75", "4,75", "4h45", "45m" or "4" (hours); null when it doesn't read.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant().Replace(" ", "");
        if (text.Length == 0)
            return null;
        if (text.Split(':') is [var h, var m] && int.TryParse(h, out var hours) && int.TryParse(m, out var mins) && mins is >= 0 and < 60 && hours >= 0)
            return new TimeSpan(hours, mins, 0);
        if (text.EndsWith('m') && !text.Contains('h') && int.TryParse(text[..^1], out var onlyMinutes) && onlyMinutes >= 0)
            return TimeSpan.FromMinutes(onlyMinutes);
        if (text.Split('h') is [var hh, var mm] && int.TryParse(hh, out var h2) && h2 >= 0)
        {
            mm = mm.TrimEnd('m');
            if (mm.Length == 0)
                return TimeSpan.FromHours(h2);
            if (int.TryParse(mm, out var m2) && m2 is >= 0 and < 60)
                return new TimeSpan(h2, m2, 0);
        }
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalHours) && decimalHours >= 0 && decimalHours < 100)
            return TimeSpan.FromMinutes(Math.Round(decimalHours * 60));
        return null;
    }

    /// <summary>Reads a time of day: "15:10", "1510", "3:10 pm", "3pm"; null when it doesn't read.</summary>
    public static TimeOnly? ParseClock(string text)
    {
        text = text.Trim();
        // "%H", not "H": a one-letter format is read as a standard one, and "H" isn't one (TryParseExact throws).
        string[] formats = ["H:mm", "HH:mm", "Hmm", "HHmm", "%H", "h:mm tt", "h:mmtt", "h tt", "htt", "H.mm", "H:mm:ss"];
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (TimeOnly.TryParseExact(text, formats, culture, DateTimeStyles.AllowWhiteSpaces, out var exact))
                return exact;
            if (TimeOnly.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var loose))
                return loose;
        }
        return null;
    }
}

public sealed partial class DayReviewViewModel
{
    private ProjectStore? _projectStore;
    private ProjectTime _projectTime = ProjectTime.Empty;
    private List<ProjectSpan> _handSpans = [];
    private Dictionary<long, Project> _projectsById = [];
    private Dictionary<long, Category> _categoriesById = [];
    private bool _loadingDayNote;

    private ProjectStore Projects => _projectStore ??= new ProjectStore(_history.Database);

    /// <summary>The projects lane above the activity.</summary>
    public IReadOnlyList<ProjectLaneBlock> ProjectLane { get; private set; } = [];

    /// <summary>"None", then every project not archived (plus archived ones still linked), for the lists.</summary>
    [ObservableProperty] private IReadOnlyList<ProjectOption> _projectOptions = [];

    /// <summary>The projects to set a selection to (not archived), for menus.</summary>
    public IReadOnlyList<ProjectOption> AssignableProjects => ProjectOptions.Where(o => !o.IsNone && !IsArchived(o.Id)).ToList();

    [ObservableProperty] private bool _isSidePanelOpen = true;

    /// <summary>The panel's tab: 0 Segments, 1 Applications, 2 Suggestions, 3 Time Log, 4 Marked.</summary>
    [ObservableProperty] private int _panelTab;

    public ObservableCollection<DayAppRow> AppRows { get; } = [];

    /// <summary>The day's recorded segments, first capture to last, for the Recorded Segments tab.</summary>
    public ObservableCollection<SegmentRow> DaySegments { get; } = [];
    public ObservableCollection<TimeLogRow> TimeLogRows { get; } = [];
    [ObservableProperty] private string _appsTotalText = "";
    [ObservableProperty] private string _assignedTotalText = "";
    [ObservableProperty] private string _loggedTotalText = "";

    /// <summary>The day's note, saved when the box loses the focus.</summary>
    [ObservableProperty] private string _dayNote = "";

    // --- the selection panel ---------------------------------------------------------------------

    [ObservableProperty] private string _selectionStartText = "";
    [ObservableProperty] private string _selectionEndText = "";
    [ObservableProperty] private string _selectionLengthText = "";

    /// <summary>The apps and sites used in the selection, with their marks (which apply to every day).</summary>
    public ObservableCollection<DayAppRow> SelectionApps { get; } = [];
    [ObservableProperty] private string _selectionAppsTotalText = "";

    public ObservableCollection<SegmentRow> SelectionRuns { get; } = [];

    // The ends of the selection, typed: applied when the box loses the focus; shown back as they are when they don't read.
    partial void OnSelectionStartTextChanged(string value) => ApplyTypedSelection(value, start: true);

    partial void OnSelectionEndTextChanged(string value) => ApplyTypedSelection(value, start: false);

    private bool _showingSelection;

    private void ApplyTypedSelection(string text, bool start)
    {
        if (_showingSelection || _range is not { } range)
            return;
        if (ProjectTimes.ParseClock(text) is { } clock)
        {
            // A time before the day starts is in the early hours after its date.
            var at = TimeAt(clock.ToTimeSpan().TotalSeconds + (clock < _history.DayStart ? 24 * 3600 : 0));
            var (s, e) = start ? (at, range.End) : (range.Start, at);
            // An end typed earlier than the start means the next day's early hours rarely; treat it as a mistake.
            if (e > s)
            {
                SelectRange(s, e);
                return;
            }
        }
        Dispatcher.UIThread.Post(ShowSelection);
    }

    /// <summary>Sets the selection to a project by hand (a phone call while away counts too).</summary>
    [RelayCommand]
    private void SetSelectionProject(long projectId)
    {
        if (_range is { } range)
            SetProject(range, projectId);
    }

    /// <summary>The selection ends up with no project, whatever its apps are linked to.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelectionProject()
    {
        if (_range is { } range)
            SetProject(range, null);
    }

    partial void OnDayNoteChanged(string value)
    {
        if (!_loadingDayNote)
            Projects.SetDayNote(Day.Day, value);
    }

    // --- building -------------------------------------------------------------------------------

    /// <summary>
    /// Works out the day's projects again: after the day is (re)loaded, or a range is set, or a note or logged time
    /// changes. Links and productive marks need the day reloaded first (they come with the captures). After an edit
    /// (<paramref name="edited"/>), suggestions worked out again wait to be shown.
    /// </summary>
    private void RebuildProjects(bool edited = true)
    {
        if (edited)
            _holdSuggestions = true;
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        _categoriesById = Projects.GetCategories().ToDictionary(c => c.Id);
        _projectsById = Projects.GetProjects().ToDictionary(p => p.Id);
        _handSpans = Projects.GetSpans(dayStart, dayEnd);
        _projectTime = ProjectTimeline.Build(Captures, _handSpans, dayStart, dayEnd, _maxCaptureLength);

        // Lists: every project not archived, and archived ones something on this day is linked to or marked with.
        var linked = Captures.SelectMany(c => new[] { c.AppProjectId, c.SiteProjectId, c.RuleProjectId }).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        linked.UnionWith(_projectTime.Blocks.Select(b => b.ProjectId));
        var options = new List<ProjectOption> { new(null, "None", Avalonia.Media.Brushes.Transparent, "") };
        options.AddRange(_projectsById.Values
            .Where(p => !p.Archived || linked.Contains(p.Id))
            .OrderBy(p => p.CategoryId is { } c && _categoriesById.TryGetValue(c, out var cat) ? cat.Name : "￿", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProjectOption(p.Id, p.Archived ? $"{p.Name} (archived)" : p.Name, BrushOf(p.Id),
                p.CategoryId is { } c && _categoriesById.TryGetValue(c, out var cat) ? cat.Name : "")));
        ProjectOptions = options;
        OnPropertyChanged(nameof(AssignableProjects));

        BuildProjectLane();
        WidenViewForHandSpans();

        BuildAppRows(AppRows, dayStart, dayEnd);
        AppsTotalText = DurationText.Of(SpanSummary.For(Captures, dayStart, dayEnd, _maxCaptureLength).Active);
        BuildDaySegments();
        BuildTimeLog();
        _loadingDayNote = true;
        DayNote = Projects.GetDayNote(Day.Day);
        _loadingDayNote = false;

        // The rows pointed at are new ones now; what they reached may have changed.
        _reach.Clear();
        UpdateFrameSegment(force: true);
        SelectionChanged();
        // What has a project changed, so the suggestions may have too.
        RefreshSuggestions();
    }

    /// <summary>Time set by hand outside the recording (an evening call) widens the timeline to show it.</summary>
    private void WidenViewForHandSpans()
    {
        var (start, end) = View;
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        foreach (var span in _handSpans.Where(s => s.ProjectId is not null))
        {
            start = Math.Min(start, Math.Floor(Math.Max(SecondsOf(dayStart), SecondsOf(span.Start)) / 1800) * 1800);
            end = Math.Max(end, Math.Ceiling(Math.Min(SecondsOf(dayEnd), SecondsOf(span.End)) / 1800) * 1800);
        }
        View = (start, end);
    }

    /// <summary>The apps and sites used from start to end, longest first.</summary>
    private void BuildAppRows(ICollection<DayAppRow> rows, DateTimeOffset start, DateTimeOffset end)
    {
        rows.Clear();
        foreach (var (program, time) in SpanSummary.ByProgram(Captures, start, end, _maxCaptureLength))
        {
            var (productive, projectId) = MarksOf(program);
            rows.Add(new DayAppRow(program, time, productive, OptionOf(projectId), SetProductiveSoon, SetProjectSoon));
        }
    }

    /// <summary>
    /// The whole day's segments, from the first capture to the end of the last, or further for time set to a project
    /// while nothing was recorded (an evening call); that's the span shown beside the Day button too.
    /// </summary>
    private void BuildDaySegments()
    {
        DaySegments.Clear();
        DateTimeOffset? start = Captures.Count > 0 ? Captures[0].Time : null;
        DateTimeOffset? end = Captures.Count > 0 ? Captures[^1].Time + CaptureLength(Captures.Count - 1) : null;
        var (dayStart, dayEnd) = _history.DayRange(Day.Day);
        foreach (var span in _handSpans.Where(s => s.ProjectId is not null))
        {
            var (from, to) = (span.Start > dayStart ? span.Start : dayStart, span.End < dayEnd ? span.End : dayEnd);
            if (start is null || from < start) start = from;
            if (end is null || to > end) end = to;
        }
        DaySpanText = "";
        if (start is { } s && end is { } e && e > s)
        {
            BuildSegments((s, e), DaySegments);
            DaySpanText = RangeText(s, e);
        }
    }

    /// <summary>The day's projects as the Timesheet lists them: by their first block, then those with only a note or logged time.</summary>
    private void BuildTimeLog()
    {
        TimeLogRows.Clear();
        var notes = Projects.GetNotes(Day.Day, Day.Day.AddDays(1)).ToDictionary(n => n.ProjectId, n => n.Text);
        var logged = Projects.GetLogged(Day.Day, Day.Day.AddDays(1)).ToDictionary(l => l.ProjectId, l => l.Logged);
        var order = _projectTime.Blocks.Select(b => b.ProjectId).Concat(notes.Keys.Order()).Concat(logged.Keys.Order()).Distinct();
        foreach (var id in order)
        {
            var assigned = _projectTime.Totals.TryGetValue(id, out var totals) ? totals.Assigned : TimeSpan.Zero;
            TimeLogRows.Add(new TimeLogRow(id, NameOf(id), BrushOf(id), assigned, notes.GetValueOrDefault(id, ""),
                logged.TryGetValue(id, out var l) ? l : null, SaveNote, SaveLogged));
        }
        UpdateTimeLogTotals(logged.Values);
    }

    private void UpdateTimeLogTotals(IEnumerable<TimeSpan> logged)
    {
        AssignedTotalText = ProjectTimes.Hm(_projectTime.Totals.Values.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Assigned));
        LoggedTotalText = ProjectTimes.Hm(logged.Aggregate(TimeSpan.Zero, (sum, t) => sum + t));
    }

    /// <summary>Saves a project's note for the day as typed (empty clears it); returns the text to show.</summary>
    private string SaveNote(TimeLogRow row, string typed)
    {
        if (!StillThere(row))
            return row.NoteText;
        var note = typed.Trim();
        Projects.SetNote(Day.Day, row.ProjectId, note);
        return note;
    }

    /// <summary>Saves a project's logged time from what was typed; returns the text to show.</summary>
    private string SaveLogged(TimeLogRow row, string typed)
    {
        if (!StillThere(row))
            return row.LoggedText;
        var current = Projects.GetLogged(Day.Day, Day.Day.AddDays(1)).FirstOrDefault(l => l.ProjectId == row.ProjectId)?.Logged;
        if (string.IsNullOrWhiteSpace(typed))
        {
            Projects.SetLogged(Day.Day, row.ProjectId, null);
            current = null;
        }
        else if (ProjectTimes.ParseDuration(typed) is { } value)
        {
            Projects.SetLogged(Day.Day, row.ProjectId, value);
            current = value;
        }
        UpdateTimeLogTotals(Projects.GetLogged(Day.Day, Day.Day.AddDays(1)).Select(l => l.Logged));
        return current is { } shown ? ProjectTimes.Hm(shown) : "";
    }

    /// <summary>Whether a row's project still exists; one deleted in Settings meanwhile takes its row away instead.</summary>
    private bool StillThere(TimeLogRow row)
    {
        if (Projects.ProjectExists(row.ProjectId))
            return true;
        Dispatcher.UIThread.Post(() => RebuildProjects());
        return false;
    }

    /// <summary>Fills the selection's lists from the selection, if there is one, and marks the day's rows inside it.</summary>
    private void SelectionChanged()
    {
        SelectionApps.Clear();
        SelectionRuns.Clear();
        ClearSelectionProjectCommand.NotifyCanExecuteChanged();
        foreach (var row in DaySegments)
            row.IsPicked = _range is { } picked && row.From >= picked.Start && row.To <= picked.End;
        foreach (var marked in MarkedRanges)
            marked.IsPicked = _range is { } picked && marked.Start >= picked.Start && marked.End <= picked.End;
        if (_range is not { } range)
            return;

        ShowSelection();
        SelectionLengthText = ProjectTimes.Hm(range.End - range.Start);
        BuildAppRows(SelectionApps, range.Start, range.End);
        SelectionAppsTotalText = DurationText.Of(SpanSummary.For(Captures, range.Start, range.End, _maxCaptureLength).Active);
        BuildSegments(range, SelectionRuns);
    }

    /// <summary>Shows the selection's ends in the boxes, without that counting as typing.</summary>
    private void ShowSelection()
    {
        if (_range is not { } range)
            return;
        _showingSelection = true;
        SelectionStartText = ClockShort(range.Start);
        SelectionEndText = ClockShort(range.End);
        _showingSelection = false;
    }

    /// <summary>
    /// A range as segments: each app or site in a row (repeated when switching back), idle stretches, and time
    /// nothing was recorded.
    /// </summary>
    private void BuildSegments((DateTimeOffset Start, DateTimeOffset End) range, ICollection<SegmentRow> segments)
    {
        var cursor = range.Start;
        (DateTimeOffset From, DateTimeOffset To, CaptureRecord Capture)? run = null;

        // Time not recorded: a row for each stretch of it set to a project (a call while away), and for what's between.
        void Gap(DateTimeOffset to)
        {
            var from = cursor;
            foreach (var span in _handSpans.Where(s => s.ProjectId is not null && s.End > from && s.Start < to))
            {
                if (span.Start - from >= TimeSpan.FromSeconds(1))
                    segments.Add(Segment(from, span.Start, null));
                var (start, end) = (span.Start > from ? span.Start : from, span.End < to ? span.End : to);
                segments.Add(Segment(start, end, null));
                from = end;
            }
            if (to - from >= TimeSpan.FromSeconds(1))
                segments.Add(Segment(from, to, null));
        }

        void Flush()
        {
            if (run is not { } r)
                return;
            segments.Add(Segment(r.From, r.To, r.Capture));
            cursor = r.To;
            run = null;
        }

        for (var i = 0; i < Captures.Count; i++)
        {
            var c = Captures[i];
            var from = c.Time;
            var to = c.Time + CaptureLength(i);
            if (to <= range.Start || from >= range.End)
                continue;
            if (from < range.Start) from = range.Start;
            if (to > range.End) to = range.End;

            if (run is { } r && r.To == from && SameRun(r.Capture, c))
            {
                run = (r.From, to, r.Capture);
                continue;
            }
            Flush();
            Gap(from);
            cursor = from;
            run = (from, to, c);
        }
        Flush();
        Gap(range.End);
    }

    /// <summary>A segment from..to of what a capture shows, or of time not recorded (no capture).</summary>
    private SegmentRow Segment(DateTimeOffset from, DateTimeOffset to, CaptureRecord? capture)
    {
        if (capture is not { } c)
            return new SegmentRow(from, to, RangeText(from, to), "Not recorded", null, false, ProjectOfSpan(from, to), SetProductiveSoon, (_, _) => { });
        ProgramKey? program = !c.IsActive || c.PendingDelete ? null
            : c.Site is { } site ? new ProgramKey(ProgramKind.Site, site)
            : c.AppName is { } app ? new ProgramKey(ProgramKind.App, app) : null;
        var name = c.PendingDelete ? "Marked for deletion" : !c.IsActive ? "Idle" : program?.Name ?? "Unknown";
        var productive = program is { } p && MarksOf(p).Productive;
        return new SegmentRow(from, to, RangeText(from, to), name, program, productive, ProjectOfSpan(from, to),
            SetProductiveSoon, (_, project) => SetSpanProjectSoon(from, to, project));
    }

    /// <summary>Two captures in the same segment: the same app or site, or both idle, or both marked for deletion.</summary>
    private static bool SameRun(CaptureRecord a, CaptureRecord b) =>
        a.IsActive == b.IsActive && a.PendingDelete == b.PendingDelete
        && (!a.IsActive || (string.Equals(a.AppName, b.AppName, StringComparison.OrdinalIgnoreCase) && a.Site == b.Site));

    /// <summary>A program's marks as the day's captures carry them (Productive, and its own project link).</summary>
    private (bool Productive, long? ProjectId) MarksOf(ProgramKey program)
    {
        foreach (var c in Captures)
        {
            if (program.Kind == ProgramKind.Site && c.Site == program.Name)
                return (c.SiteProductive == true, c.SiteProjectId);
            if (program.Kind == ProgramKind.App && string.Equals(c.AppName, program.Name, StringComparison.OrdinalIgnoreCase))
                return (c.AppProductive, c.AppProjectId);
        }
        return (false, null);
    }

    /// <summary>
    /// The project from..to is marked with: the one project over all of it, None over none of it, null (mixed) when
    /// it's partly one project and partly another or none.
    /// </summary>
    private ProjectOption? ProjectOfSpan(DateTimeOffset from, DateTimeOffset to)
    {
        var covered = TimeSpan.Zero;
        long? project = null;
        var several = false;
        foreach (var block in _projectTime.Blocks)
        {
            var overlap = (block.End < to ? block.End : to) - (block.Start > from ? block.Start : from);
            if (overlap <= TimeSpan.Zero)
                continue;
            covered += overlap;
            several |= project is { } seen && seen != block.ProjectId;
            project = block.ProjectId;
        }
        if (covered == TimeSpan.Zero)
            return OptionOf(null);
        return several || covered < to - from ? null : OptionOf(project);
    }

    /// <summary>Sets a stretch of time to a project by hand, or to no project; only that stretch, not the app's link.</summary>
    private void SetSpanProjectSoon(DateTimeOffset from, DateTimeOffset to, ProjectOption project) => Dispatcher.UIThread.Post(() =>
    {
        Projects.SetSpan(from, to, project.Id);
        RebuildProjects();
    });

    private void SetProductiveSoon(ProgramKey program, bool productive) =>
        Dispatcher.UIThread.Post(() => SetProductive(program, productive));

    /// <summary>Links an app or site to a project (all history); the day is reloaded to show it.</summary>
    private void SetProjectSoon(ProgramKey program, ProjectOption project) => Dispatcher.UIThread.Post(() =>
    {
        _history.SetProject(program, project.Id);
        Reload(Current >= 0 && Current < Captures.Count ? Captures[Current].Id : null);
    });

    private ProjectOption OptionOf(long? projectId) =>
        ProjectOptions.FirstOrDefault(o => o.Id == projectId) ?? ProjectOptions[0];

    private bool IsArchived(long? id) => id is { } value && _projectsById.TryGetValue(value, out var p) && p.Archived;

    private string NameOf(long projectId) => _projectsById.TryGetValue(projectId, out var p) ? p.Name : "Deleted project";

    private IBrush BrushOf(long? projectId) => projectId is { } id && _projectsById.TryGetValue(id, out var p)
        ? ProjectColors.Brush(ProjectStore.ColorOf(p, _categoriesById))
        : AppBrushes.NotRecorded;

    /// <summary>"9:08 – 9:10 AM": with a 12-hour clock, AM or PM once when both ends share it.</summary>
    internal static string RangeText(DateTimeOffset from, DateTimeOffset to)
    {
        var culture = CultureInfo.CurrentCulture;
        var pattern = culture.DateTimeFormat.ShortTimePattern;
        var (start, end) = (from.ToLocalTime(), to.ToLocalTime());
        if (pattern.Contains("tt") && start.ToString("tt", culture) == end.ToString("tt", culture))
            return $"{start.ToString(pattern.Replace("tt", "").Trim(), culture)} – {end.ToString(pattern, culture)}";
        return $"{ClockShort(from)} – {ClockShort(to)}";
    }

    private static string ClockShort(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
}
