namespace Worklapse.Core.Projects;

/// <summary>
/// A week's or a month's time in columns (<see cref="Period.ColumnOf"/>: a week's days, a month's weeks), for the
/// Overview's totals.
/// </summary>
/// <param name="Assigned">Per project with time marked or logged time: its assigned time in each column.</param>
/// <param name="Logged">Per project: the time logged over the period.</param>
/// <param name="Productive">Active time without a project in productive apps and sites, per column.</param>
/// <param name="Other">The rest of the active time without a project, per column.</param>
/// <param name="Recorded">Time captured, active or idle, per column (<see cref="ProjectTime.Recorded"/>).</param>
public sealed record PeriodTotals(
    Period Period,
    IReadOnlyDictionary<long, TimeSpan[]> Assigned,
    IReadOnlyDictionary<long, TimeSpan> Logged,
    TimeSpan[] Productive,
    TimeSpan[] Other,
    TimeSpan[] Recorded)
{
    /// <param name="days">The period's days (others are left out).</param>
    /// <param name="logged">The time logged in the period.</param>
    public static PeriodTotals Build(Period period, IEnumerable<DaySummary> days, IEnumerable<LoggedTime> logged)
    {
        var columns = period.ColumnCount;
        var assigned = new Dictionary<long, TimeSpan[]>();
        var (productive, other, recorded) = (new TimeSpan[columns], new TimeSpan[columns], new TimeSpan[columns]);
        foreach (var day in days.Where(d => period.Contains(d.Day)))
        {
            var column = period.ColumnOf(day.Day);
            foreach (var (project, totals) in day.Time.Totals)
                Cells(project)[column] += totals.Assigned;
            productive[column] += day.Time.Unassigned.Productive;
            other[column] += day.Time.Unassigned.Other;
            recorded[column] += day.Time.Recorded;
        }
        var loggedByProject = new Dictionary<long, TimeSpan>();
        foreach (var entry in logged.Where(l => period.Contains(l.Day)))
        {
            loggedByProject[entry.ProjectId] = loggedByProject.GetValueOrDefault(entry.ProjectId) + entry.Logged;
            Cells(entry.ProjectId); // a project with only logged time gets a line
        }
        return new PeriodTotals(period, assigned, loggedByProject, productive, other, recorded);

        TimeSpan[] Cells(long project) =>
            assigned.TryGetValue(project, out var cells) ? cells : assigned[project] = new TimeSpan[columns];
    }

    /// <summary>The assigned time of all projects, per column.</summary>
    public TimeSpan[] AssignedPerColumn() =>
        Enumerable.Range(0, Period.ColumnCount).Select(i => Assigned.Values.Aggregate(TimeSpan.Zero, (sum, cells) => sum + cells[i])).ToArray();
}
