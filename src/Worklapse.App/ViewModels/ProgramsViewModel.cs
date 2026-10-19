using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.Core.History;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

/// <summary>An app or website on the Applications page.</summary>
public sealed partial class ProgramRow : ObservableObject
{
    public ProgramRow(ProgramUsage usage, ProjectItem project)
    {
        Program = usage.Program;
        Recorded = usage.Recorded;
        Saved = usage.Productive;
        _productive = usage.Productive;
        SavedExcluded = usage.Excluded;
        _excluded = usage.Excluded;
        SavedProject = project;
        _project = project;
    }

    public ProgramKey Program { get; }
    public string Name => Program.Name;
    public string KindText => Program.Kind == ProgramKind.App ? "App" : "Website";
    public TimeSpan Recorded { get; }
    public string RecordedText => DurationText.Of(Recorded);

    /// <summary>The classification in the database; <see cref="Productive"/> is what the dialog shows.</summary>
    public bool Saved { get; set; }

    /// <summary>The exclusion in the database; <see cref="Excluded"/> is what the dialog shows.</summary>
    public bool SavedExcluded { get; set; }

    /// <summary>The project link in the database; <see cref="Project"/> is what the dialog shows.</summary>
    public ProjectItem SavedProject { get; set; }

    public bool IsDirty => Productive != Saved || Excluded != SavedExcluded || Project != SavedProject;

    [ObservableProperty] private bool _productive;

    /// <summary>Nothing is recorded while it has the focus.</summary>
    [ObservableProperty] private bool _excluded;

    private ProjectItem _project;

    /// <summary>
    /// The project its active time is marked with (<see cref="ProjectItem.None"/> for none). A list that loses its
    /// selection hands back null, which means none.
    /// </summary>
    public ProjectItem Project
    {
        get => _project;
        set => SetProperty(ref _project, value ?? ProjectItem.None);
    }
}

public enum ProgramSort
{
    Productive,
    Excluded,
    Name,
    Kind,
    Project,
    Recorded,
}

/// <summary>
/// The Applications page of the Settings dialog: every app and website recorded, to mark productive, exclude from
/// recording, or link to a project. The marks live in the history database (they belong with the history), and like
/// the other settings take effect on Apply or OK.
/// </summary>
public sealed partial class ProgramsViewModel : ObservableObject
{
    private readonly TimeSpan _maxCaptureLength;
    private List<ProgramRow> _all = [];
    private int _loads;

    public ProgramsViewModel(TimeSpan captureInterval) => _maxCaptureLength = captureInterval * 1.5;

    /// <summary>Raised when a row's checkbox or project changes.</summary>
    public event Action? Changed;

    /// <summary>"None" and the projects, from the Projects page (projects made there can be picked before Apply).</summary>
    public IReadOnlyList<ProjectItem> ProjectChoices { get; init; } = [ProjectItem.None];

    [ObservableProperty] private IReadOnlyList<ProgramRow> _rows = [];
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private ProgramSort _sort = ProgramSort.Recorded;
    [ObservableProperty] private bool _descending = true;

    public string ProductiveHeader => Header("Productive", ProgramSort.Productive);
    public string ExcludedHeader => Header("Excluded", ProgramSort.Excluded);
    public string NameHeader => Header("Name", ProgramSort.Name);
    public string KindHeader => Header("Type", ProgramSort.Kind);
    public string ProjectHeader => Header("Project", ProgramSort.Project);
    public string RecordedHeader => Header("Recorded", ProgramSort.Recorded);

    public bool IsDirty => _all.Any(r => r.IsDirty);

    /// <summary>
    /// Reads the list from the history in <paramref name="dataFolder"/>, off the UI thread; <paramref name="resolve"/>
    /// finds the Projects page's item for a project id.
    /// </summary>
    public async Task LoadAsync(string dataFolder, Func<long?, ProjectItem> resolve)
    {
        var load = ++_loads;
        Status = "Loading…";
        List<ProgramUsage> programs;
        try
        {
            programs = await Task.Run(() =>
            {
                using var history = new HistoryStore(WorklapseDatabase.Open(dataFolder));
                return history.GetPrograms(_maxCaptureLength);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            if (load == _loads)
                Status = "Couldn't read the history: " + ex.Message;
            return;
        }
        if (load != _loads)
            return; // a newer load (another folder) replaced this one

        _all = programs.Select(p => new ProgramRow(p, resolve(p.ProjectId))).ToList();
        foreach (var row in _all)
            row.PropertyChanged += (_, _) => Changed?.Invoke();
        Refresh();
    }

    /// <summary>The names of the apps and sites linked to <paramref name="project"/> in the dialog.</summary>
    public IEnumerable<string> LinkedTo(ProjectItem project) =>
        _all.Where(r => r.Project == project).Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>A project deleted on the Projects page: whatever was linked to it no longer is.</summary>
    public void Unlink(ProjectItem project)
    {
        foreach (var row in _all.Where(r => r.Project == project))
            row.Project = ProjectItem.None;
    }

    /// <summary>
    /// Writes the changed marks to the history in <paramref name="dataFolder"/>. The Projects page is applied first,
    /// so projects made in the dialog have their ids.
    /// </summary>
    public void Apply(string dataFolder)
    {
        var changed = _all.Where(r => r.IsDirty).ToList();
        if (changed.Count == 0)
            return;
        using var history = new HistoryStore(WorklapseDatabase.Open(dataFolder));
        foreach (var row in changed)
        {
            if (row.Productive != row.Saved)
                history.SetProductive(row.Program, row.Productive);
            if (row.Excluded != row.SavedExcluded)
                history.SetExcluded(row.Program, row.Excluded);
            if (row.Project != row.SavedProject)
                history.SetProject(row.Program, row.Project.Id);
            row.Saved = row.Productive;
            row.SavedExcluded = row.Excluded;
            row.SavedProject = row.Project;
        }
    }

    [RelayCommand]
    private void SortBy(ProgramSort column)
    {
        if (Sort == column)
            Descending = !Descending;
        else
            (Sort, Descending) = (column, column is ProgramSort.Recorded or ProgramSort.Productive or ProgramSort.Excluded);
        Refresh();
    }

    partial void OnFilterChanged(string value) => Refresh();

    private void Refresh()
    {
        var filter = Filter.Trim();
        IEnumerable<ProgramRow> rows = filter.Length == 0 ? _all : _all.Where(r => r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        Func<ProgramRow, object> key = Sort switch
        {
            ProgramSort.Productive => r => r.Productive,
            ProgramSort.Excluded => r => r.Excluded,
            ProgramSort.Name => r => r.Name,
            ProgramSort.Kind => r => r.KindText,
            ProgramSort.Project => r => r.Project.IsNone ? "" : r.Project.Name,
            _ => r => r.Recorded,
        };
        var ordered = Descending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        Rows = ordered.ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var apps = _all.Count(r => r.Program.Kind == ProgramKind.App);
        var sites = _all.Count - apps;
        Status = _all.Count == 0 ? "Nothing recorded yet."
            : filter.Length > 0 ? $"{Rows.Count} of {apps} apps and {sites} websites"
            : $"{apps} apps and {sites} websites";
        OnPropertyChanged(nameof(ProductiveHeader));
        OnPropertyChanged(nameof(ExcludedHeader));
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(KindHeader));
        OnPropertyChanged(nameof(ProjectHeader));
        OnPropertyChanged(nameof(RecordedHeader));
    }

    private string Header(string text, ProgramSort column) => Sort == column ? $"{text} {(Descending ? "▼" : "▲")}" : text;
}

internal static class DurationText
{
    /// <summary>"45 s", "12 m", "3 h 05 m", as in Day review.</summary>
    public static string Of(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        if (span.TotalSeconds < 60)
            return $"{(int)Math.Round(span.TotalSeconds)} s";
        return minutes < 60 ? $"{minutes} m" : $"{minutes / 60} h {minutes % 60:00} m";
    }
}
