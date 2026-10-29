using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [ObservableProperty] private string _onTaskText = "";
    [ObservableProperty] private string _assignedText = "";
    [ObservableProperty] private string _loggedText = "";
}

/// <summary>One project on one day. The note and Logged are typed (as on Day review's Time Log) and saved at once.</summary>
public sealed partial class TimesheetLineRow : ObservableObject
{
    private readonly Func<TimesheetLineRow, string, string> _saveNote;
    private readonly Func<TimesheetLineRow, string, string> _saveLogged;
    private string _noteText = "";
    private string _loggedText = "";

    public TimesheetLineRow(DateOnly day, long projectId, Func<TimesheetLineRow, string, string> saveNote,
        Func<TimesheetLineRow, string, string> saveLogged)
    {
        Day = day;
        ProjectId = projectId;
        _saveNote = saveNote;
        _saveLogged = saveLogged;
    }

    public DateOnly Day { get; }
    public long ProjectId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private IBrush _brush = Brushes.Transparent;
    [ObservableProperty] private string _activeText = "";
    [ObservableProperty] private string _onTaskText = "";
    [ObservableProperty] private string _assignedText = "";

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

    /// <summary>Shows the stored values without saving them; leaves a cell being typed in alone when nothing changed.</summary>
    public void Show(string note, string logged)
    {
        if (note != _noteText)
        {
            _noteText = note;
            OnPropertyChanged(nameof(NoteText));
        }
        if (logged != _loggedText)
        {
            _loggedText = logged;
            OnPropertyChanged(nameof(LoggedText));
        }
    }
}

/// <summary>A category or a project in the totals beside the grid.</summary>
public sealed record TimesheetTotalRow(string Name, IBrush Brush, bool IsCategory, string AssignedText, string LoggedText)
{
    public Thickness Indent => IsCategory ? default : new Thickness(16, 0, 0, 0);
}

/// <summary>
/// The Timesheet view: a line per project per day for a week or a month. Notes and logged time are saved as they're
/// typed. Reloads
/// when shown (Day review may have changed logged time or notes).
/// </summary>
public sealed partial class TimesheetViewModel(SettingsStore settings, Action<DateOnly> openDay)
    : PeriodViewModel<TimesheetData>(settings, openDay)
{
    /// <summary>Day headings (<see cref="TimesheetDayRow"/>) each followed by its lines (<see cref="TimesheetLineRow"/>).</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<TimesheetTotalRow> Totals { get; } = [];

    [ObservableProperty] private string _totalActiveText = "";
    [ObservableProperty] private string _totalOnTaskText = "";
    [ObservableProperty] private string _totalAssignedText = "";
    [ObservableProperty] private string _totalLoggedText = "";

    /// <summary>Shown in place of the grid while there's nothing to list, or the history can't be read.</summary>
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty] private bool _hasNoTotals = true;

    /// <summary>The sum of <see cref="Totals"/>; logged reads 0:00 rather than nothing, like the rows above it.</summary>
    [ObservableProperty] private string _grandAssignedText = "";
    [ObservableProperty] private string _grandLoggedText = "";

    /// <summary>Why the last export failed; empty when it didn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportError))]
    private string _exportError = "";

    public bool HasExportError => ExportError.Length > 0;

    /// <summary>A file name for the export: "Timesheet 2026-10-12 to 2026-10-18.csv".</summary>
    public string ExportFileName => $"Timesheet {Period.Start:yyyy-MM-dd} to {Period.End.AddDays(-1):yyyy-MM-dd}.csv";

    [RelayCommand]
    private void DismissExportError() => ExportError = "";

    /// <summary>Writes the range, read again so it's current, as CSV with decimal hours, to the file chosen.</summary>
    public async Task ExportAsync(string fileName, Func<Task<Stream>> openFile)
    {
        ExportError = "";
        var (period, settings) = (Period, Settings.Current);
        try
        {
            var data = await Task.Run(() => Load(settings.DataFolder, period, settings.MaxCaptureLength, settings.DayStart));
            // A spreadsheet holding the file open makes this fail; said so in the bar under the tool bar.
            await using var stream = await openFile();
            // With a byte order mark, so spreadsheets read names with accents correctly.
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            TimesheetCsv.Write(writer, data.Lines, data.Catalog.Projects, data.Catalog.Categories);
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            ExportError = $"Couldn't export {fileName}: {ex.Message}";
        }
    }

    protected override TimesheetData Load(string dataFolder, Period period, TimeSpan maxCaptureLength, TimeOnly dayStart) =>
        TimesheetData.Load(dataFolder, period.Start, period.End, maxCaptureLength, dayStart);

    protected override void ShowError(string message)
    {
        Rows.Clear();
        Totals.Clear();
        HasNoTotals = true;
        TotalActiveText = TotalOnTaskText = TotalAssignedText = TotalLoggedText = "";
        GrandAssignedText = GrandLoggedText = "";
        EmptyText = message;
        IsEmpty = true;
    }

    /// <summary>
    /// Shows a load. Rows are kept by day and project, so the ones still there are updated in place (today grows
    /// while recording) and a cell being typed in keeps its text and its focus.
    /// </summary>
    protected override void Show(TimesheetData data)
    {
        var byDay = data.Lines.ToLookup(l => l.Day);
        var lines = data.Lines.ToDictionary(l => (l.Day, l.ProjectId));
        var keys = new List<(DateOnly Day, long? Project)>();
        foreach (var day in data.Days())
        {
            keys.Add((day, null));
            keys.AddRange(byDay[day].Select(l => (day, (long?)l.ProjectId)));
        }
        SyncRows(keys);

        foreach (var row in Rows)
        {
            if (row is TimesheetDayRow dayRow)
            {
                var dayLines = byDay[dayRow.Day].ToList();
                dayRow.NoteText = data.DayNotes.GetValueOrDefault(dayRow.Day, "");
                dayRow.ActiveText = Hm(dayLines.SumTime(l => l.Active));
                dayRow.OnTaskText = Hm(dayLines.SumTime(l => l.OnTask));
                dayRow.AssignedText = Hm(dayLines.SumTime(l => l.Assigned));
                dayRow.LoggedText = LoggedHm(dayLines);
            }
            else if (row is TimesheetLineRow lineRow)
            {
                var line = lines[(lineRow.Day, lineRow.ProjectId)];
                lineRow.Name = data.Catalog.NameOf(line.ProjectId);
                lineRow.Brush = data.Catalog.BrushOf(line.ProjectId);
                lineRow.ActiveText = Hm(line.Active);
                lineRow.OnTaskText = Hm(line.OnTask);
                lineRow.AssignedText = Hm(line.Assigned);
                lineRow.Show(line.Note, line.Logged is { } logged ? Hm(logged) : "");
            }
        }

        TotalActiveText = Hm(data.Lines.SumTime(l => l.Active));
        TotalOnTaskText = Hm(data.Lines.SumTime(l => l.OnTask));
        TotalAssignedText = Hm(data.Lines.SumTime(l => l.Assigned));
        TotalLoggedText = LoggedHm(data.Lines);
        ShowTotals(data);

        IsEmpty = Rows.Count == 0;
        EmptyText = Period.Kind == PeriodKind.Week ? "Nothing recorded or logged this week." : "Nothing recorded or logged this month.";
    }

    /// <summary>Makes <see cref="Rows"/> the rows for these keys, in order, keeping the row objects already there.</summary>
    private void SyncRows(List<(DateOnly Day, long? Project)> keys)
    {
        var existing = Rows.ToDictionary(KeyOf);
        var wanted = keys.Select(key => existing.TryGetValue(key, out var row) ? row
            : key.Project is { } project ? new TimesheetLineRow(key.Day, project, SaveNote, SaveLogged)
            : new TimesheetDayRow(key.Day, new RelayCommand(() => OpenDay(key.Day)))).ToList();
        var keep = wanted.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i]))
                Rows.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i]))
                continue;
            var at = Rows.IndexOf(wanted[i]);
            if (at >= 0)
                Rows.Move(at, i);
            else
                Rows.Insert(i, wanted[i]);
        }

        static (DateOnly, long?) KeyOf(object row) => row switch
        {
            TimesheetDayRow d => (d.Day, null),
            TimesheetLineRow l => (l.Day, l.ProjectId),
            _ => throw new InvalidOperationException("Not a timesheet row."),
        };
    }

    /// <summary>Assigned and logged per category, then per project inside it; projects without a category last.</summary>
    private void ShowTotals(TimesheetData data)
    {
        Totals.Clear();
        var byProject = data.Lines.GroupBy(l => l.ProjectId)
            .ToDictionary(g => g.Key, g => (Assigned: g.SumTime(l => l.Assigned), Logged: g.SumTime(l => l.Logged ?? TimeSpan.Zero)));
        foreach (var (category, projects) in data.Catalog.ByCategory(byProject.Keys))
        {
            Totals.Add(new TimesheetTotalRow(category?.Name ?? "No category", ProjectCatalog.BrushOf(category), true,
                Hm(projects.SumTime(p => byProject[p].Assigned)), Hm(projects.SumTime(p => byProject[p].Logged))));
            foreach (var project in projects)
                Totals.Add(new TimesheetTotalRow(data.Catalog.NameOf(project), data.Catalog.BrushOf(project), false,
                    Hm(byProject[project].Assigned), Hm(byProject[project].Logged)));
        }
        HasNoTotals = Totals.Count == 0;
        GrandAssignedText = Hm(byProject.Values.SumTime(p => p.Assigned));
        GrandLoggedText = Hm(byProject.Values.SumTime(p => p.Logged));
    }

    /// <summary>Saves a line's note as typed (empty clears it); returns the text to show.</summary>
    private string SaveNote(TimesheetLineRow row, string typed)
    {
        var note = typed.Trim();
        return Save(row, line => line with { Note = note }, (projects, line) => projects.SetNote(line.Day, line.ProjectId, note))
            ? note : row.NoteText;
    }

    /// <summary>Saves a line's logged time from what was typed (empty clears it); returns the text to show.</summary>
    private string SaveLogged(TimesheetLineRow row, string typed)
    {
        TimeSpan? value;
        if (string.IsNullOrWhiteSpace(typed))
            value = null;
        else if (ProjectTimes.ParseDuration(typed) is { } parsed)
            value = parsed;
        else
            return row.LoggedText;
        return Save(row, line => line with { Logged = value }, (projects, line) => projects.SetLogged(line.Day, line.ProjectId, value))
            ? (value is { } saved ? Hm(saved) : "") : row.LoggedText;
    }

    /// <summary>
    /// Saves a change to a line and shows the totals again. False when it isn't saved: nothing's loaded, or the project
    /// was deleted in Settings since it was read (what's there now is read instead).
    /// </summary>
    private bool Save(TimesheetLineRow row, Func<TimesheetLine, TimesheetLine> change, Action<ProjectStore, TimesheetLine> save)
    {
        if (Data is not { } data)
            return false;
        var index = data.Lines.FindIndex(l => l.Day == row.Day && l.ProjectId == row.ProjectId);
        if (index < 0)
            return false;
        var line = data.Lines[index];

        using (var db = WorklapseDatabase.Open(data.DataFolder))
        {
            var projects = new ProjectStore(db);
            if (!projects.ProjectExists(row.ProjectId))
            {
                Dispatcher.UIThread.Post(Refresh);
                return false;
            }
            save(projects, line);
        }
        data.Lines[index] = change(line);
        // Totals follow; the rows stay (a line whose note or logged time was all it had goes at the next reload). A
        // load started before the save would show the old value, so read again too.
        Dispatcher.UIThread.Post(() =>
        {
            if (Data != data || IsDisposed)
                return;
            Show(data);
            Refresh();
        });
        return true;
    }

    private static string Hm(TimeSpan time) => ProjectTimes.Hm(time);

    /// <summary>The sum of what's logged, or nothing when none of the lines has any.</summary>
    private static string LoggedHm(IEnumerable<TimesheetLine> lines)
    {
        var logged = lines.Where(l => l.Logged is not null).ToList();
        return logged.Count == 0 ? "" : Hm(logged.SumTime(l => l.Logged!.Value));
    }
}

/// <summary>A range of the timesheet as read from the history, with the projects and categories to name it.</summary>
public sealed record TimesheetData(
    string DataFolder,
    List<TimesheetLine> Lines,
    Dictionary<DateOnly, string> DayNotes,
    ProjectCatalog Catalog)
{
    /// <summary>Reads a range with a connection of its own (connections are single-thread); for a background thread.</summary>
    public static TimesheetData Load(string dataFolder, DateOnly from, DateOnly to, TimeSpan maxCaptureLength, TimeOnly dayStart)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        var history = new HistoryStore(db, dayStart);
        var projects = new ProjectStore(db);
        return new TimesheetData(dataFolder,
            Timesheet.Build(history, projects, from, to, maxCaptureLength),
            projects.GetDayNotes(from, to),
            ProjectCatalog.Read(projects));
    }

    /// <summary>The days with lines or a day note, in order.</summary>
    public IEnumerable<DateOnly> Days() => Lines.Select(l => l.Day).Concat(DayNotes.Keys).Distinct().Order();
}
