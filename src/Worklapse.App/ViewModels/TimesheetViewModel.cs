using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Worklapse.App.Theming;
using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

/// <summary>A day's heading in the timesheet: its date (opens it in Day review), its note and its totals.</summary>
public sealed partial class TimesheetDayRow(DateOnly day, IRelayCommand open) : ObservableObject
{
    public DateOnly Day => day;
    public string DayText { get; } = day.ToString("ddd d", CultureInfo.CurrentCulture);
    public IRelayCommand Open => open;

    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private string _activeText = "";
    [ObservableProperty] private string _productiveText = "";
    [ObservableProperty] private string _assignedText = "";
    [ObservableProperty] private string _loggedText = "";
}

/// <summary>One project on one day. Logged is typed (as on Day review's Time Log) and saved at once.</summary>
public sealed partial class TimesheetLineRow : ObservableObject
{
    private readonly Func<TimesheetLineRow, string, string> _saveLogged;
    private string _loggedText = "";

    public TimesheetLineRow(DateOnly day, long projectId, Func<TimesheetLineRow, string, string> saveLogged)
    {
        Day = day;
        ProjectId = projectId;
        _saveLogged = saveLogged;
    }

    public DateOnly Day { get; }
    public long ProjectId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private IBrush _brush = Brushes.Transparent;
    [ObservableProperty] private string _notesText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesStyle))]
    private bool _hasNotes;

    public FontStyle NotesStyle => HasNotes ? FontStyle.Normal : FontStyle.Italic;
    [ObservableProperty] private string _activeText = "";
    [ObservableProperty] private string _productiveText = "";
    [ObservableProperty] private string _assignedText = "";

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

    /// <summary>Shows the stored value without saving it; leaves a cell being typed in alone when nothing changed.</summary>
    public void ShowLogged(string text)
    {
        if (text == _loggedText)
            return;
        _loggedText = text;
        OnPropertyChanged(nameof(LoggedText));
    }
}

/// <summary>A category or a project in the totals beside the grid.</summary>
public sealed record TimesheetTotalRow(string Name, IBrush Brush, bool IsCategory, string AssignedText, string LoggedText)
{
    public Thickness Indent => IsCategory ? default : new Thickness(16, 0, 0, 0);
}

public enum TimesheetRange
{
    Week,
    Month,
}

/// <summary>
/// The Timesheet view: a line per project per day for a week or a month, read off the UI thread with a connection of
/// its own. Logged time is saved as it's typed. Reloads when shown (Day review may have changed logged time or notes).
/// </summary>
public sealed partial class TimesheetViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Action<DateOnly> _openDay;
    private DateOnly _start;          // a Monday, or the first of a month
    private int _generation;          // the newest load; older ones are dropped when they finish
    private bool _disposed;
    private TimesheetData? _data;

    public TimesheetViewModel(SettingsStore settings, Action<DateOnly> openDay)
    {
        _settings = settings;
        _openDay = openDay;
        _start = WeekStart(Today);
        ShowRange();
    }

    /// <summary>Day headings (<see cref="TimesheetDayRow"/>) each followed by its lines (<see cref="TimesheetLineRow"/>).</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<TimesheetTotalRow> Totals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeek), nameof(IsMonth), nameof(TodayText))]
    private TimesheetRange _range = TimesheetRange.Week;

    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _footerTitle = "";
    [ObservableProperty] private string _totalActiveText = "";
    [ObservableProperty] private string _totalProductiveText = "";
    [ObservableProperty] private string _totalAssignedText = "";
    [ObservableProperty] private string _totalLoggedText = "";

    /// <summary>Shown in place of the grid while there's nothing to list, or the history can't be read.</summary>
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty] private bool _hasNoTotals = true;

    public string TodayText => Range == TimesheetRange.Week ? "This Week" : "This Month";

    // The Week and Month tool buttons (a radio pair: the one turned off by the other's click changes nothing).
    public bool IsWeek
    {
        get => Range == TimesheetRange.Week;
        set
        {
            if (value)
                SetRange(TimesheetRange.Week);
        }
    }

    public bool IsMonth
    {
        get => Range == TimesheetRange.Month;
        set
        {
            if (value)
                SetRange(TimesheetRange.Month);
        }
    }

    /// <summary>The first day shown and the day after the last.</summary>
    public (DateOnly From, DateOnly To) Span => (_start, Range == TimesheetRange.Week ? _start.AddDays(7) : _start.AddMonths(1));

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [RelayCommand]
    private void Previous() => MoveTo(Range == TimesheetRange.Week ? _start.AddDays(-7) : _start.AddMonths(-1));

    [RelayCommand]
    private void Next() => MoveTo(Range == TimesheetRange.Week ? _start.AddDays(7) : _start.AddMonths(1));

    [RelayCommand]
    private void GoToToday() => MoveTo(Range == TimesheetRange.Week ? WeekStart(Today) : MonthStart(Today));

    private void SetRange(TimesheetRange range)
    {
        if (range == Range)
            return;
        // A week becomes the month it starts in; a month becomes this week if it holds today, else its first week.
        var (from, to) = Span;
        _start = range == TimesheetRange.Month ? MonthStart(_start)
            : Today >= from && Today < to ? WeekStart(Today) : WeekStart(_start);
        Range = range;
        MoveTo(_start);
    }

    private void MoveTo(DateOnly start)
    {
        _start = start;
        ShowRange();
        Refresh();
    }

    private void ShowRange()
    {
        var (from, to) = Span;
        var last = to.AddDays(-1);
        var culture = CultureInfo.CurrentCulture;
        if (Range == TimesheetRange.Month)
        {
            RangeText = from.ToString("MMMM yyyy", culture);
            FooterTitle = from.ToString("MMMM", culture);
        }
        else
        {
            RangeText = from.Month == last.Month ? $"{from.Day} – {last.ToString("d MMMM yyyy", culture)}"
                : from.Year == last.Year ? $"{from.ToString("d MMM", culture)} – {last.ToString("d MMM yyyy", culture)}"
                : $"{from.ToString("d MMM yyyy", culture)} – {last.ToString("d MMM yyyy", culture)}";
            FooterTitle = $"Week {ISOWeek.GetWeekOfYear(from.ToDateTime(TimeOnly.MinValue))}";
        }
    }

    /// <summary>Reads the range again in the background; what's shown stays until it's ready.</summary>
    public async void Refresh()
    {
        if (_disposed)
            return;
        var generation = ++_generation;
        var (from, to) = Span;
        var folder = _settings.Current.DataFolder;
        var maxCaptureLength = TimeSpan.FromSeconds(_settings.Current.IntervalSeconds * 1.5);
        TimesheetData data;
        try
        {
            data = await Task.Run(() => TimesheetData.Load(folder, from, to, maxCaptureLength));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            if (generation == _generation && !_disposed)
                ShowError(ex.Message);
            return;
        }
        // A newer load is on its way, or the window has closed.
        if (generation != _generation || _disposed)
            return;
        _data = data;
        Show();
    }

    /// <summary>Writes the range, read again so it's current, as CSV with decimal hours, to the file chosen.</summary>
    public async Task ExportAsync(string fileName, Func<Task<Stream>> openFile)
    {
        ExportError = "";
        var (from, to) = Span;
        var folder = _settings.Current.DataFolder;
        var maxCaptureLength = TimeSpan.FromSeconds(_settings.Current.IntervalSeconds * 1.5);
        try
        {
            var data = await Task.Run(() => TimesheetData.Load(folder, from, to, maxCaptureLength));
            // A spreadsheet holding the file open makes this fail; said so in the bar under the tool bar.
            await using var stream = await openFile();
            // With a byte order mark, so spreadsheets read names with accents correctly.
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            TimesheetCsv.Write(writer, data.Lines, data.Projects, data.Categories);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ExportError = $"Couldn't export {fileName}: {ex.Message}";
        }
    }

    /// <summary>Why the last export failed; empty when it didn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportError))]
    private string _exportError = "";

    public bool HasExportError => ExportError.Length > 0;

    [RelayCommand]
    private void DismissExportError() => ExportError = "";

    /// <summary>A file name for the export: "Timesheet 2026-10-12 to 2026-10-18.csv".</summary>
    public string ExportFileName
    {
        get
        {
            var (from, to) = Span;
            return $"Timesheet {from:yyyy-MM-dd} to {to.AddDays(-1):yyyy-MM-dd}.csv";
        }
    }

    public void Dispose() => _disposed = true;

    private void ShowError(string message)
    {
        _data = null;
        Rows.Clear();
        Totals.Clear();
        HasNoTotals = true;
        TotalActiveText = TotalProductiveText = TotalAssignedText = TotalLoggedText = "";
        EmptyText = "Couldn't read the history: " + message;
        IsEmpty = true;
    }

    /// <summary>
    /// Shows <see cref="_data"/>. When the same lines are there as before (only times changed: today grows while
    /// recording), the rows are updated in place, so a cell being typed in keeps its text.
    /// </summary>
    private void Show()
    {
        var data = _data!;
        var keys = new List<(DateOnly Day, long? Project)>();
        foreach (var day in data.Days())
        {
            keys.Add((day, null));
            keys.AddRange(data.Lines.Where(l => l.Day == day).Select(l => (day, (long?)l.ProjectId)));
        }
        var same = keys.Count == Rows.Count && keys.Select((k, i) => Rows[i] switch
        {
            TimesheetDayRow d => d.Day == k.Day && k.Project is null,
            TimesheetLineRow l => l.Day == k.Day && l.ProjectId == k.Project,
            _ => false,
        }).All(x => x);
        if (!same)
        {
            Rows.Clear();
            foreach (var (day, project) in keys)
                Rows.Add(project is { } id ? new TimesheetLineRow(day, id, SaveLogged) : new TimesheetDayRow(day, new RelayCommand(() => _openDay(day))));
        }

        foreach (var row in Rows)
        {
            if (row is TimesheetDayRow dayRow)
            {
                var lines = data.Lines.Where(l => l.Day == dayRow.Day).ToList();
                dayRow.NoteText = data.DayNotes.GetValueOrDefault(dayRow.Day, "");
                dayRow.ActiveText = Hm(lines.Sum(l => l.Active));
                dayRow.ProductiveText = Hm(lines.Sum(l => l.Productive));
                dayRow.AssignedText = Hm(lines.Sum(l => l.Assigned));
                dayRow.LoggedText = LoggedHm(lines);
            }
            else if (row is TimesheetLineRow lineRow)
            {
                var line = data.Lines.First(l => l.Day == lineRow.Day && l.ProjectId == lineRow.ProjectId);
                lineRow.Name = data.NameOf(line.ProjectId);
                lineRow.Brush = data.BrushOf(line.ProjectId);
                lineRow.HasNotes = line.Notes.Count > 0;
                lineRow.NotesText = lineRow.HasNotes ? line.NotesText : "No notes";
                lineRow.ActiveText = Hm(line.Active);
                lineRow.ProductiveText = Hm(line.Productive);
                lineRow.AssignedText = Hm(line.Assigned);
                lineRow.ShowLogged(line.Logged is { } logged ? Hm(logged) : "");
            }
        }

        TotalActiveText = Hm(data.Lines.Sum(l => l.Active));
        TotalProductiveText = Hm(data.Lines.Sum(l => l.Productive));
        TotalAssignedText = Hm(data.Lines.Sum(l => l.Assigned));
        TotalLoggedText = LoggedHm(data.Lines);
        ShowTotals(data);

        IsEmpty = Rows.Count == 0;
        EmptyText = Range == TimesheetRange.Week ? "Nothing recorded or logged this week." : "Nothing recorded or logged this month.";
    }

    /// <summary>Assigned and logged per category, then per project inside it; projects without a category last.</summary>
    private void ShowTotals(TimesheetData data)
    {
        Totals.Clear();
        var byProject = data.Lines.GroupBy(l => l.ProjectId)
            .ToDictionary(g => g.Key, g => (Assigned: g.Sum(l => l.Assigned), Logged: g.Sum(l => l.Logged ?? TimeSpan.Zero)));
        var groups = byProject.Keys
            .GroupBy(id => data.Projects.GetValueOrDefault(id)?.CategoryId is { } c && data.Categories.ContainsKey(c) ? c : (long?)null)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key is { } c ? data.Categories[c].Name : "", StringComparer.CurrentCultureIgnoreCase);
        foreach (var group in groups)
        {
            var projects = group.OrderBy(data.NameOf, StringComparer.CurrentCultureIgnoreCase).ToList();
            var (name, brush) = group.Key is { } id
                ? (data.Categories[id].Name, ProjectColors.Brush(data.Categories[id].Color))
                : ("No category", ProjectColors.Brush(ProjectStore.DefaultColor));
            Totals.Add(new TimesheetTotalRow(name, brush, true,
                Hm(projects.Sum(p => byProject[p].Assigned)), Hm(projects.Sum(p => byProject[p].Logged))));
            foreach (var project in projects)
                Totals.Add(new TimesheetTotalRow(data.NameOf(project), data.BrushOf(project), false,
                    Hm(byProject[project].Assigned), Hm(byProject[project].Logged)));
        }
        HasNoTotals = Totals.Count == 0;
    }

    /// <summary>Saves a line's logged time from what was typed (empty clears it); returns the text to show.</summary>
    private string SaveLogged(TimesheetLineRow row, string typed)
    {
        if (_data is not { } data)
            return row.LoggedText;
        var index = data.Lines.FindIndex(l => l.Day == row.Day && l.ProjectId == row.ProjectId);
        if (index < 0)
            return row.LoggedText;
        var line = data.Lines[index];
        var shown = line.Logged is { } before ? Hm(before) : "";
        TimeSpan? value;
        if (string.IsNullOrWhiteSpace(typed))
            value = null;
        else if (ProjectTimes.ParseDuration(typed) is { } parsed)
            value = parsed;
        else
            return shown;

        using (var db = WorklapseDatabase.Open(data.DataFolder))
        {
            var projects = new ProjectStore(db);
            // Deleted in Settings since this was read: show what's there now instead.
            if (projects.GetProjects().All(p => p.Id != row.ProjectId))
            {
                Dispatcher.UIThread.Post(Refresh);
                return shown;
            }
            projects.SetLogged(row.Day, row.ProjectId, value);
        }
        data.Lines[index] = line with { Logged = value };
        // Totals follow; the rows stay (a line whose logged time was all it had goes at the next reload).
        Dispatcher.UIThread.Post(() =>
        {
            if (_data == data && !_disposed)
                Show();
        });
        return value is { } saved ? Hm(saved) : "";
    }

    private static string Hm(TimeSpan time) => ProjectTimes.Hm(time);

    /// <summary>The sum of what's logged, or nothing when none of the lines has any.</summary>
    private static string LoggedHm(IEnumerable<TimesheetLine> lines)
    {
        var logged = lines.Where(l => l.Logged is not null).ToList();
        return logged.Count == 0 ? "" : Hm(logged.Sum(l => l.Logged!.Value));
    }

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateOnly MonthStart(DateOnly day) => new(day.Year, day.Month, 1);
}

/// <summary>A range of the timesheet as read from the history, with the projects and categories to name it.</summary>
internal sealed record TimesheetData(
    string DataFolder,
    DateOnly From,
    DateOnly To,
    List<TimesheetLine> Lines,
    Dictionary<DateOnly, string> DayNotes,
    Dictionary<long, Project> Projects,
    Dictionary<long, Category> Categories)
{
    /// <summary>Reads a range with a connection of its own (connections are single-thread); for a background thread.</summary>
    public static TimesheetData Load(string dataFolder, DateOnly from, DateOnly to, TimeSpan maxCaptureLength)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        var history = new HistoryStore(db);
        var projects = new ProjectStore(db);
        return new TimesheetData(dataFolder, from, to,
            Timesheet.Build(history, projects, from, to, maxCaptureLength),
            projects.GetDayNotes(from, to),
            projects.GetProjects().ToDictionary(p => p.Id),
            projects.GetCategories().ToDictionary(c => c.Id));
    }

    /// <summary>The days with lines or a day note, in order.</summary>
    public IEnumerable<DateOnly> Days() => Lines.Select(l => l.Day).Concat(DayNotes.Keys).Distinct().Order();

    public string NameOf(long projectId) => Projects.TryGetValue(projectId, out var p)
        ? p.Archived ? $"{p.Name} (archived)" : p.Name
        : "Deleted project";

    public IBrush BrushOf(long projectId) => Projects.TryGetValue(projectId, out var p)
        ? ProjectColors.Brush(ProjectStore.ColorOf(p, Categories))
        : AppBrushes.NotRecorded;
}

internal static class TimeSpanSums
{
    public static TimeSpan Sum<T>(this IEnumerable<T> items, Func<T, TimeSpan> selector) =>
        items.Aggregate(TimeSpan.Zero, (sum, item) => sum + selector(item));
}
