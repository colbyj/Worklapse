using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.App.Export;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.Export;
using Worklapse.Core.History;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.App.ViewModels;

public sealed record TimelapseSpeed(int FramesPerSecond, string Label)
{
    public override string ToString() => Label;
}

public sealed record TimelapseSize(int Width, int Height, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Export Timelapse: what to put in the video (the whole day or the selection, all of it or one project's time in it)
/// and how it looks, then the export's progress. The picture is Day review's (its monitor and Focus on Active App); the choices are
/// remembered in the settings.
/// </summary>
public sealed partial class TimelapseExportViewModel : ObservableObject
{
    private readonly TimelapseSource _source;
    private readonly SettingsStore _settings;
    private CancellationTokenSource? _cancel;
    private List<TimelapseFrame> _frames = [];
    private (int Width, int Height) _videoSize;

    public TimelapseExportViewModel(TimelapseSource source, SettingsStore settings)
    {
        _source = source;
        _settings = settings;
        var current = settings.Current;

        Speeds = [new(10, "10 frames per second"), new(15, "15 frames per second"), new(30, "30 frames per second"), new(60, "60 frames per second")];
        Sizes = [new(1280, 720, "1280 × 720"), new(1920, 1080, "1920 × 1080"), new(2560, 1440, "2560 × 1440"), new(3840, 2160, "3840 × 2160")];
        _speed = Speeds.MinBy(s => Math.Abs(s.FramesPerSecond - current.TimelapseFramesPerSecond))!;
        _size = Sizes.MinBy(s => Math.Abs(s.Height - current.TimelapseHeight))!;
        _clock = current.TimelapseClock;
        _includeIdle = current.TimelapseIncludeIdle;

        SelectionText = source.Selection is { } selection ? DayReviewViewModel.RangeText(selection.Start, selection.End) : "";
        // A selection made before asking is most likely what to export.
        _isSelection = source.Selection is not null;
        _isWholeDay = !_isSelection;
        Title = $"Export Timelapse — {source.Day.Day.ToString("D", CultureInfo.CurrentCulture)}";
        UpdateProjects();
    }

    public string Title { get; }
    public IReadOnlyList<TimelapseSpeed> Speeds { get; }
    public IReadOnlyList<TimelapseSize> Sizes { get; }
    public bool HasSelection => _source.Selection is not null;
    public string SelectionText { get; }
    public string ViewText => _source.ViewText;

    [ObservableProperty] private bool _isWholeDay;
    [ObservableProperty] private bool _isSelection;

    /// <summary>Only the time of <see cref="SelectedProject"/> within the day or the selection.</summary>
    [ObservableProperty] private bool _onlyProject;

    /// <summary>The projects with time in the day or the selection, their blocks cut to it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProjects))]
    private IReadOnlyList<TimelapseProject> _projects = [];

    [ObservableProperty] private TimelapseProject? _selectedProject;
    [ObservableProperty] private string _noProjectsText = "";

    public bool HasProjects => Projects.Count > 0;
    [ObservableProperty] private TimelapseSpeed _speed;
    [ObservableProperty] private TimelapseSize _size;
    [ObservableProperty] private bool _clock;
    [ObservableProperty] private bool _includeIdle;

    /// <summary>The frames, length and size the choices make.</summary>
    [ObservableProperty] private string _summaryText = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsExporting), nameof(IsDone), nameof(CloseText))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private ExportState _state;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _errorText = "";

    /// <summary>The video written, once done.</summary>
    [ObservableProperty] private string? _savedPath;

    public bool IsChoosing => State == ExportState.Choosing;
    public bool IsExporting => State == ExportState.Exporting;
    public bool IsDone => State == ExportState.Done;
    public string CloseText => State == ExportState.Exporting ? "Cancel" : "Close";

    /// <summary>A file name to suggest: the day, and the project when it is one project's time.</summary>
    public string FileName
    {
        get
        {
            var name = $"Worklapse {_source.Day.Day:yyyy-MM-dd}";
            if (OnlyProject && SelectedProject is { } project)
                name += " " + string.Concat(project.Project.Name.Split(Path.GetInvalidFileNameChars()));
            return name + ".mp4";
        }
    }

    public bool CanExport => State == ExportState.Choosing && _frames.Count > 0;

    partial void OnIsWholeDayChanged(bool value) => UpdateProjects();
    partial void OnIsSelectionChanged(bool value) => UpdateProjects();
    partial void OnOnlyProjectChanged(bool value) => Plan();
    partial void OnSelectedProjectChanged(TimelapseProject? value) => Plan();
    partial void OnSpeedChanged(TimelapseSpeed value) => Plan();
    partial void OnSizeChanged(TimelapseSize value) => Plan();
    partial void OnIncludeIdleChanged(bool value) => Plan();

    /// <summary>The day or the selection, as chosen.</summary>
    private (DateTimeOffset Start, DateTimeOffset End) Range =>
        IsSelection && _source.Selection is { } selection ? selection : _source.DayRange;

    /// <summary>
    /// Lists the projects with time in the range, each block cut to it, keeping the project chosen if it has time
    /// there; then plans.
    /// </summary>
    private void UpdateProjects()
    {
        // The radio buttons clear one another one at a time; wait until one is set.
        if (!(IsWholeDay || IsSelection))
            return;
        var (start, end) = Range;
        var chosen = SelectedProject?.Project;
        Projects = _source.Projects
            .Select(p => p with
            {
                Blocks = p.Blocks.Where(b => b.Start < end && b.End > start)
                    .Select(b => (b.Start > start ? b.Start : start, b.End < end ? b.End : end))
                    .ToList(),
            })
            .Where(p => p.Blocks.Count > 0)
            .ToList();
        NoProjectsText = IsSelection ? "No project time in the selection" : "No project time this day";
        SelectedProject = Projects.FirstOrDefault(p => p.Project == chosen) ?? Projects.FirstOrDefault();
        if (!HasProjects)
            OnlyProject = false;
        Plan();
    }

    /// <summary>The export's frames planned for the choices, without the speed and size.</summary>
    private TimelapseOptions Options()
    {
        IReadOnlyList<(DateTimeOffset, DateTimeOffset)> ranges = OnlyProject ? SelectedProject?.Blocks ?? [] : [Range];
        return new TimelapseOptions(ranges, _source.Monitor, _source.FocusOnApplication, IncludeIdle, Speed.FramesPerSecond,
            (Size.Width, Size.Height), Clock);
    }

    private void Plan()
    {
        if (!(IsWholeDay || IsSelection))
            return;
        var options = Options();
        _frames = TimelapsePlan.Frames(_source.Day, options);
        _videoSize = TimelapsePlan.VideoSize(_frames, options.Box);
        if (_frames.Count == 0)
        {
            SummaryText = "Nothing recorded to show";
        }
        else
        {
            var length = TimeSpan.FromSeconds(Math.Ceiling(_frames.Count / (double)Speed.FramesPerSecond));
            var lengthText = length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss");
            SummaryText = $"{_frames.Count.ToString("N0", CultureInfo.CurrentCulture)} frames · {lengthText} · {_videoSize.Width} × {_videoSize.Height}";
        }
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when Export is clicked: the view asks where to save, then calls <see cref="ExportAsync"/>.</summary>
    public event Func<Task>? ExportRequested;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task Export() => ExportRequested?.Invoke() ?? Task.CompletedTask;

    /// <summary>Makes the video at <paramref name="path"/>, showing its progress; remembers the choices.</summary>
    public async Task ExportAsync(string path)
    {
        if (!CanExport)
            return;
        var options = Options();
        var day = _source.Day.Day;
        _settings.Save(_settings.Current with
        {
            TimelapseFramesPerSecond = options.FramesPerSecond,
            TimelapseHeight = Size.Height,
            TimelapseClock = Clock,
            TimelapseIncludeIdle = IncludeIdle,
        });

        var settings = _settings.Current;
        var job = new TimelapseJob(_frames, () =>
        {
            using var history = new HistoryStore(WorklapseDatabase.Open(settings.DataFolder), settings.DayStart);
            return TimelapsePlan.Frames(history.LoadDay(day), options);
        }, _videoSize, options.FramesPerSecond, options.Clock);

        _cancel = new CancellationTokenSource();
        ErrorText = "";
        Percent = 0;
        ProgressText = $"Frame 0 of {_frames.Count.ToString("N0", CultureInfo.CurrentCulture)}";
        State = ExportState.Exporting;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Percent = 100.0 * p.Done / Math.Max(1, p.Total);
            ProgressText = $"Frame {p.Done.ToString("N0", CultureInfo.CurrentCulture)} of {p.Total.ToString("N0", CultureInfo.CurrentCulture)}";
        });
        try
        {
            await TimelapseExporter.RunAsync(settings.DataFolder, job, path, progress, _cancel.Token);
            SavedPath = path;
            ProgressText = $"Saved {Path.GetFileName(path)}";
            Percent = 100;
            State = ExportState.Done;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            State = ExportState.Choosing;
        }
        catch (Exception ex)
        {
            Log.Error("Exporting a timelapse failed", ex);
            ErrorText = $"Couldn't export the timelapse: {ex.Message}";
            State = ExportState.Choosing;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
        }
    }

    /// <summary>Stops an export under way; the unfinished file is removed.</summary>
    public void Cancel() => _cancel?.Cancel();
}

public enum ExportState
{
    Choosing,
    Exporting,
    Done,
}
