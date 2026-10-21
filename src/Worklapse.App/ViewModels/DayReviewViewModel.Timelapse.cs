using Worklapse.Core.History;

namespace Worklapse.App.ViewModels;

/// <summary>A project with time on the day, and its blocks there.</summary>
public sealed record TimelapseProject(ProjectOption Project, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Blocks)
{
    public override string ToString() => Project.Name;
}

/// <summary>What Export Timelapse starts from: the day, and how Day review shows it now.</summary>
/// <param name="Monitor">The monitor chosen in Day review (null: all), kept while it isn't attached.</param>
/// <param name="ViewText">How the picture is shown, for the dialog to say.</param>
/// <param name="Selection">The selected time, if any.</param>
public sealed record TimelapseSource(
    DayHistory Day,
    (DateTimeOffset Start, DateTimeOffset End) DayRange,
    MonitorRecord? Monitor,
    bool FocusOnApplication,
    string ViewText,
    (DateTimeOffset Start, DateTimeOffset End)? Selection,
    IReadOnlyList<TimelapseProject> Projects);

public sealed partial class DayReviewViewModel
{
    public bool CanExportTimelapse => Captures.Count > 0;

    /// <summary>Export Timelapse's choices for the day as shown now.</summary>
    public TimelapseExportViewModel TimelapseExport() => new(TimelapseSource(), _settings);

    private TimelapseSource TimelapseSource()
    {
        var monitor = _chosenMonitor switch
        {
            null => "All monitors",
            { IsTimeSnapper: true } => "TimeSnapper window",
            { } chosen => $"{chosen.Bounds.Width}×{chosen.Bounds.Height} monitor{(chosen.IsPrimary ? " (primary)" : "")}",
        };
        var view = FocusOnApplication ? $"{monitor}, focused on the active app" : monitor;
        var projects = _projectTime.Blocks
            .GroupBy(b => b.ProjectId)
            .Select(g => new TimelapseProject(OptionOf(g.Key), g.Select(b => (b.Start, b.End)).ToList()))
            .ToList();
        return new TimelapseSource(Day, _history.DayRange(Day.Day), _chosenMonitor, FocusOnApplication, view, _range, projects);
    }
}
