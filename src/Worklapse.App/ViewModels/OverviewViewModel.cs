using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
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

/// <summary>
/// A day of the week: its timeline (on the axis the week shares, in seconds since midnight), totals, and how many
/// suggestions wait for a decision. Clicking it opens the day in Day review.
/// </summary>
public sealed record OverviewDayRow(
    DateOnly Day,
    string DayText,
    string SpanText,
    double AxisStart,
    double AxisEnd,
    IReadOnlyList<ProjectLaneBlock> Blocks,
    IReadOnlyList<TimelineSpan> Activity,
    double? Now,
    string ActiveText,
    string AssignedText,
    IRelayCommand Open,
    int Suggestions = 0)
{
    public bool HasSuggestions => Suggestions > 0;
    public string SuggestionsText => Suggestions == 1 ? "1 suggestion" : $"{Suggestions} suggestions";
}

/// <summary>A day of the month grid; days of the neighbouring months only show their number.</summary>
public sealed record OverviewMonthCell(
    DateOnly Day,
    string NumberText,
    bool InMonth,
    bool IsToday,
    string ActiveText,
    string AssignedText,
    IReadOnlyList<BarSegment> Bar,
    IRelayCommand Open,
    int Suggestions = 0)
{
    public bool HasSuggestions => Suggestions > 0;
    public string SuggestionsTip => Suggestions == 1 ? "1 suggestion to decide" : $"{Suggestions} suggestions to decide";
}

/// <summary>A value in the totals tree; zero shows as a dash.</summary>
public sealed record OverviewCell(string Text, bool IsZero);

/// <summary>
/// A line of the totals tree: a category (or "No category", or "No project") that expands and collapses, or a project
/// (or Productive / Other under "No project"). <see cref="Brush"/> is null for time without a project, drawn as an
/// empty square as in Day review.
/// </summary>
public sealed record OverviewTreeRow(
    string Name,
    IBrush? Brush,
    bool IsGroup,
    bool IsExpanded,
    IRelayCommand? Toggle,
    IReadOnlyList<OverviewCell> Cells,
    string AssignedText,
    string LoggedText)
{
    public bool HasBrush => Brush is not null;
    public Thickness Indent => IsGroup ? new Thickness(4, 0, 0, 0) : new Thickness(30, 0, 0, 0);
}

/// <summary>A project's note under a day in the notes list.</summary>
public sealed record OverviewNoteEntry(IBrush Brush, string Project, string Text);

/// <summary>A day with notes: its day note, and its projects' notes when expanded.</summary>
public sealed partial class OverviewNoteRow(DateOnly day, string dayNote, IReadOnlyList<OverviewNoteEntry> entries, bool expanded,
    Action<DateOnly, bool> expandedChanged) : ObservableObject
{
    public string DayText { get; } = day.ToString("ddd d", CultureInfo.CurrentCulture);
    public bool HasDayNote => dayNote.Length > 0;
    public string Text => HasDayNote ? dayNote : "No day note";
    public FontStyle TextStyle => HasDayNote ? FontStyle.Normal : FontStyle.Italic;
    public IReadOnlyList<OverviewNoteEntry> Entries => entries;
    public bool HasEntries => entries.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEntries))]
    private bool _isExpanded = expanded;

    public bool ShowsEntries => IsExpanded && HasEntries;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => expandedChanged(day, value);
}

/// <summary>
/// The Overview, Worklapse's start view: a week of day timelines (or a month grid) over a tree of totals by category
/// and project, with the days' notes beside it.
/// </summary>
public sealed partial class OverviewViewModel(SettingsStore settings, Action<DateOnly> openDay)
    : PeriodViewModel<OverviewData>(settings, openDay)
{
    private const string NoCategoryKey = "category:none";
    private const string NoProjectKey = "noproject";

    private readonly HashSet<string> _collapsed = [];
    private readonly HashSet<DateOnly> _expandedNotes = [];

    // Works out the week's suggestions, so the days needing a look stand out; kept for its trained classifier.
    private readonly SuggestionEngine _suggestions = new();

    /// <summary>The weekdays' short names, Monday first, over the month grid.</summary>
    public static IReadOnlyList<string> WeekdayNames { get; } =
        Enumerable.Range(1, 7).Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[i % 7]).ToList();

    /// <summary>Week mode: the seven days.</summary>
    public ObservableCollection<OverviewDayRow> Days { get; } = [];

    /// <summary>Month mode: whole weeks, Monday first, covering the month.</summary>
    public ObservableCollection<OverviewMonthCell> MonthCells { get; } = [];

    /// <summary>The time axis the week's timelines share, in seconds since midnight (for the hours over them).</summary>
    [ObservableProperty] private double _axisStart = 8 * 3600;
    [ObservableProperty] private double _axisEnd = 18 * 3600;

    /// <summary>The tree's columns between the names and the totals: days in week mode, weeks in month mode.</summary>
    [ObservableProperty] private IReadOnlyList<string> _columns = [];
    public ObservableCollection<OverviewTreeRow> Tree { get; } = [];
    [ObservableProperty] private IReadOnlyList<OverviewCell> _assignedFooter = [];
    [ObservableProperty] private IReadOnlyList<OverviewCell> _recordedFooter = [];
    [ObservableProperty] private string _assignedTotalText = "";
    [ObservableProperty] private string _loggedTotalText = "";
    [ObservableProperty] private string _recordedTotalText = "";
    [ObservableProperty] private string _noProjectTotalText = "";

    public ObservableCollection<OverviewNoteRow> Notes { get; } = [];
    [ObservableProperty] private bool _hasNoNotes = true;

    /// <summary>Why the history couldn't be read; empty when it could.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    protected override OverviewData Load(string dataFolder, Period period, TimeSpan maxCaptureLength, TimeOnly dayStart)
    {
        // Loads may overlap (a quick change of week); the engine is one thread's at a time.
        lock (_suggestions)
            return OverviewData.Load(dataFolder, period, maxCaptureLength, dayStart, Settings.Current,
                period.Kind == PeriodKind.Week ? _suggestions : null);
    }

    /// <summary>Shows why the period can't be read, and nothing of the one shown before (it isn't this one).</summary>
    protected override void ShowError(string message)
    {
        ErrorText = message;
        Days.Clear();
        MonthCells.Clear();
        Tree.Clear();
        Notes.Clear();
        HasNoNotes = true;
        AssignedFooter = RecordedFooter = [];
        AssignedTotalText = LoggedTotalText = RecordedTotalText = NoProjectTotalText = "";
    }

    protected override void Show(OverviewData data)
    {
        ErrorText = "";
        ShowDays(data);
        ShowMonth(data);
        ShowTree(data);
        ShowNotes(data);
    }

    private void ShowDays(OverviewData data)
    {
        Days.Clear();
        if (data.Period.Kind != PeriodKind.Week)
            return;

        // One axis for the week: the earliest start to the latest end, in whole hours (the latest end up to 25 hours
        // after midnight on the day the clocks go back, more on days that start after midnight). An empty week shows
        // the working day, or from the day's start if that's later.
        var midnights = data.Days.ToDictionary(d => d.Day, d => LocalDay.Midnight(d.Day));
        var extents = data.Days.Where(d => d.Extent is not null)
            .Select(d => (Start: Seconds(midnights[d.Day], d.Extent!.Value.First), End: Seconds(midnights[d.Day], d.Extent.Value.Last)))
            .ToList();
        var latestDayEnd = data.Days.Max(d => Seconds(midnights[d.Day], LocalDay.Range(d.Day, data.DayStart).End));
        var morning = Math.Max(8 * 3600.0, data.DayStart.ToTimeSpan().TotalSeconds);
        (AxisStart, AxisEnd) = extents.Count == 0 ? (morning, morning + 10 * 3600)
            : (Math.Floor(extents.Min(e => e.Start) / 3600) * 3600,
               Math.Min(latestDayEnd, Math.Ceiling(extents.Max(e => e.End) / 3600) * 3600));
        if (AxisEnd - AxisStart < 3600)
            AxisEnd = AxisStart + 3600;

        var today = LocalDay.Today(data.DayStart);
        foreach (var summary in data.Days)
        {
            var day = summary.Day;
            var midnight = midnights[day];
            var blocks = summary.Time.Blocks
                .Select(b => new ProjectLaneBlock(Seconds(midnight, b.Start), Seconds(midnight, b.End), data.Catalog.BrushOf(b.ProjectId), data.Catalog.NameOf(b.ProjectId)))
                .ToList();
            var activity = summary.Activity
                .Select(r => new TimelineSpan(Seconds(midnight, r.Start), Seconds(midnight, r.End), r.Kind switch
                {
                    ActivityKind.Productive => TimelineKind.Productive,
                    ActivityKind.Other => TimelineKind.Other,
                    _ => TimelineKind.Idle,
                }, Marked: false))
                .ToList();
            var span = summary.Extent is { } extent ? $"{Clock(extent.First)} – {Clock(extent.Last)}" : "";
            double? now = day == today ? Seconds(midnight, DateTimeOffset.Now) : null;
            Days.Add(new OverviewDayRow(day, day.ToString("ddd d", CultureInfo.CurrentCulture), span, AxisStart, AxisEnd, blocks, activity, now,
                HmOrDash(summary.Time.Active), HmOrDash(summary.Time.Assigned), new RelayCommand(() => OpenDay(day)),
                data.Suggestions.GetValueOrDefault(day)));
        }
    }

    private void ShowMonth(OverviewData data)
    {
        MonthCells.Clear();
        if (data.Period.Kind != PeriodKind.Month)
            return;
        var today = LocalDay.Today(data.DayStart);
        var byDay = data.Days.ToDictionary(d => d.Day);
        for (var cursor = data.Period.GridStart; cursor < data.Period.GridEnd; cursor = cursor.AddDays(1))
        {
            var day = cursor; // each cell's command keeps its own day
            var number = day.Day.ToString(CultureInfo.CurrentCulture);
            if (!byDay.TryGetValue(day, out var summary))
            {
                MonthCells.Add(new OverviewMonthCell(day, number, data.Period.Contains(day), day == today, "", "", [], new RelayCommand(() => OpenDay(day))));
                continue;
            }
            var time = summary.Time;
            var bar = data.Catalog.ByCategory(time.Totals.Keys)
                .Select(g => new BarSegment(g.Projects.Sum(p => time.Totals[p].Assigned.TotalSeconds), ProjectCatalog.BrushOf(g.Category)))
                .Append(new BarSegment((time.Unassigned.Productive + time.Unassigned.Other).TotalSeconds, AppBrushes.NotRecorded, Hatched: true))
                .Where(s => s.Value > 0)
                .ToList();
            var empty = time.Assigned == TimeSpan.Zero && time.Active == TimeSpan.Zero;
            MonthCells.Add(new OverviewMonthCell(day, number, true, day == today,
                empty ? "" : Hm(time.Active), empty ? "" : Hm(time.Assigned), bar, new RelayCommand(() => OpenDay(day)),
                data.Suggestions.GetValueOrDefault(day)));
        }
    }

    /// <summary>Category → project, a column per day (week) or week (month), then Assigned and Logged; then time without a project.</summary>
    private void ShowTree(OverviewData data)
    {
        var period = data.Period;
        var totals = data.Totals;
        var culture = CultureInfo.CurrentCulture;
        Columns = Enumerable.Range(0, period.ColumnCount)
            .Select(i => period.ColumnStart(i) is var start && period.Kind == PeriodKind.Week
                ? start.ToString("ddd d", culture)
                : $"Wk {Period.WeekNumber(start)}")
            .ToList();

        Tree.Clear();
        foreach (var (category, projects) in data.Catalog.ByCategory(totals.Assigned.Keys))
        {
            var key = category is { } c ? $"category:{c.Id}" : NoCategoryKey;
            var sums = Enumerable.Range(0, period.ColumnCount).Select(i => projects.SumTime(p => totals.Assigned[p][i])).ToArray();
            AddGroup(key, category?.Name ?? "No category", ProjectCatalog.BrushOf(category), sums,
                Hm(projects.SumTime(p => totals.Logged.GetValueOrDefault(p))));
            if (!_collapsed.Contains(key))
                foreach (var project in projects)
                    Tree.Add(new OverviewTreeRow(data.Catalog.NameOf(project), data.Catalog.BrushOf(project), false, false, null,
                        Cells(totals.Assigned[project]), Hm(totals.Assigned[project].SumTime(t => t)), Hm(totals.Logged.GetValueOrDefault(project))));
        }

        var noProject = totals.Productive.Zip(totals.Other, (p, o) => p + o).ToArray();
        AddGroup(NoProjectKey, "No project", null, noProject, "–");
        if (!_collapsed.Contains(NoProjectKey))
        {
            Tree.Add(new OverviewTreeRow("Productive", AppBrushes.Productive, false, false, null, Cells(totals.Productive), Hm(totals.Productive.SumTime(t => t)), "–"));
            Tree.Add(new OverviewTreeRow("Other", AppBrushes.Other, false, false, null, Cells(totals.Other), Hm(totals.Other.SumTime(t => t)), "–"));
        }

        var assigned = totals.AssignedPerColumn();
        AssignedFooter = Cells(assigned);
        RecordedFooter = Cells(totals.Recorded);
        AssignedTotalText = Hm(assigned.SumTime(t => t));
        LoggedTotalText = Hm(totals.Logged.Values.SumTime(t => t));
        RecordedTotalText = Hm(totals.Recorded.SumTime(t => t));
        NoProjectTotalText = Hm(noProject.SumTime(t => t));

        void AddGroup(string key, string name, IBrush? brush, TimeSpan[] cells, string loggedText)
        {
            Tree.Add(new OverviewTreeRow(name, brush, true, !_collapsed.Contains(key), new RelayCommand(() =>
            {
                if (!_collapsed.Remove(key))
                    _collapsed.Add(key);
                if (Data is { } shown)
                    ShowTree(shown);
            }), Cells(cells), Hm(cells.SumTime(t => t)), loggedText));
        }
    }

    private void ShowNotes(OverviewData data)
    {
        Notes.Clear();
        foreach (var summary in data.Days)
        {
            var day = summary.Day;
            // In the order the projects' first blocks start, then projects with a note and no time.
            var order = summary.Time.Blocks.Select(b => b.ProjectId).Distinct().ToList();
            var entries = summary.Notes
                .OrderBy(n => order.IndexOf(n.ProjectId) is var i and >= 0 ? i : int.MaxValue)
                .Select(n => new OverviewNoteEntry(data.Catalog.BrushOf(n.ProjectId), data.Catalog.NameOf(n.ProjectId), n.Text))
                .ToList();
            var dayNote = data.DayNotes.GetValueOrDefault(day, "");
            if (dayNote.Length == 0 && entries.Count == 0)
                continue;
            Notes.Add(new OverviewNoteRow(day, dayNote, entries, _expandedNotes.Contains(day), (d, expanded) =>
            {
                if (expanded) _expandedNotes.Add(d);
                else _expandedNotes.Remove(d);
            }));
        }
        HasNoNotes = Notes.Count == 0;
    }

    /// <summary>Seconds since the midnight the day's date begins with, as the timelines measure time.</summary>
    private static double Seconds(DateTimeOffset midnight, DateTimeOffset time) => (time - midnight).TotalSeconds;

    private static string Clock(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private static string Hm(TimeSpan time) => ProjectTimes.Hm(time);

    private static string HmOrDash(TimeSpan time) => time == TimeSpan.Zero ? "–" : Hm(time);

    private static IReadOnlyList<OverviewCell> Cells(TimeSpan[] values) =>
        values.Select(v => new OverviewCell(HmOrDash(v), v == TimeSpan.Zero)).ToList();
}

/// <summary>A period as read from the history, with what's needed to name and colour it.</summary>
public sealed record OverviewData(
    Period Period,
    List<DaySummary> Days,
    PeriodTotals Totals,
    Dictionary<DateOnly, string> DayNotes,
    ProjectCatalog Catalog,
    TimeOnly DayStart,
    Dictionary<DateOnly, int> Suggestions)
{
    /// <summary>
    /// Reads a period with a connection of its own (connections are single-thread); for a background thread. A week
    /// also works out each day's activity for its timeline; a month only its totals. With <paramref name="suggestions"/>,
    /// each recorded day's suggestions are worked out again; without, those stored are counted.
    /// </summary>
    public static OverviewData Load(string dataFolder, Period period, TimeSpan maxCaptureLength, TimeOnly dayStart,
        WorklapseSettings settings, SuggestionEngine? suggestions)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        var history = new HistoryStore(db, dayStart);
        var projects = new ProjectStore(db);
        var days = DaySummaries.Build(history, projects, period.Start, period.End, maxCaptureLength, withActivity: period.Kind == PeriodKind.Week);
        return new OverviewData(period, days,
            PeriodTotals.Build(period, days, projects.GetLogged(period.Start, period.End)),
            projects.GetDayNotes(period.Start, period.End),
            ProjectCatalog.Read(projects),
            dayStart,
            OpenSuggestions(history, days, settings, suggestions));
    }

    private static Dictionary<DateOnly, int> OpenSuggestions(HistoryStore history, List<DaySummary> days, WorklapseSettings settings,
        SuggestionEngine? engine)
    {
        if (!settings.SuggestProjects)
            return [];
        if (engine is null)
            return new SuggestionStore(history.Database).CountOpen(days.Select(d => (d.Day, history.DayRange(d.Day).Start, history.DayRange(d.Day).End)));
        return days.Where(d => d.Extent is not null)
            .ToDictionary(d => d.Day, d => engine.Refresh(history, d.Day, settings).Count(s => s.IsOpen));
    }
}
