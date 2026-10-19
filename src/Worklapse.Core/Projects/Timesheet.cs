using System.Globalization;
using Worklapse.Core.History;

namespace Worklapse.Core.Projects;

/// <summary>One project on one day.</summary>
/// <param name="Note">What was done for the project that day; empty when nothing's written.</param>
/// <param name="Logged">The time to bill, as typed; null until it is.</param>
public sealed record TimesheetLine(DateOnly Day, long ProjectId, string Note,
    TimeSpan Active, TimeSpan Productive, TimeSpan Assigned, TimeSpan? Logged);

public static class Timesheet
{
    /// <summary>
    /// A line for every project with time marked, a note or logged time on each day from <paramref name="from"/> up
    /// to (not including) <paramref name="to"/>; per day, projects in the order their first block starts.
    /// </summary>
    public static List<TimesheetLine> Build(HistoryStore history, ProjectStore projects, DateOnly from, DateOnly to,
        TimeSpan maxCaptureLength)
    {
        var logged = projects.GetLogged(from, to).ToDictionary(l => (l.Day, l.ProjectId), l => l.Logged);
        var lines = new List<TimesheetLine>();
        foreach (var summary in DaySummaries.Build(history, projects, from, to, maxCaptureLength))
            lines.AddRange(LinesOf(summary, logged));
        return lines;
    }

    /// <summary>A day's lines: projects in the order their first block starts, then those with only a note or logged time.</summary>
    private static IEnumerable<TimesheetLine> LinesOf(DaySummary summary, IReadOnlyDictionary<(DateOnly Day, long ProjectId), TimeSpan> logged)
    {
        var day = summary.Day;
        var time = summary.Time;
        var order = time.Blocks.Select(b => b.ProjectId)
            .Concat(summary.Notes.Select(n => n.ProjectId))
            .Concat(logged.Keys.Where(k => k.Day == day).Select(k => k.ProjectId).Order())
            .Distinct();
        foreach (var project in order)
        {
            var totals = time.Totals.GetValueOrDefault(project);
            yield return new TimesheetLine(day, project,
                summary.Notes.FirstOrDefault(n => n.ProjectId == project)?.Text ?? "",
                totals?.Active ?? TimeSpan.Zero, totals?.Productive ?? TimeSpan.Zero, totals?.Assigned ?? TimeSpan.Zero,
                logged.TryGetValue((day, project), out var value) ? value : null);
        }
    }
}

public static class TimesheetCsv
{
    /// <summary>
    /// Writes the lines as CSV for a spreadsheet or an invoice: dates as yyyy-MM-dd, times as decimal hours with two
    /// places, logged time empty where none was typed.
    /// </summary>
    public static void Write(TextWriter writer, IEnumerable<TimesheetLine> lines,
        IReadOnlyDictionary<long, Project> projects, IReadOnlyDictionary<long, Category> categories)
    {
        writer.WriteLine("Date,Category,Project,Notes,Active,Productive,Assigned,Logged");
        foreach (var line in lines)
        {
            var project = projects.GetValueOrDefault(line.ProjectId);
            var category = project?.CategoryId is { } id ? categories.GetValueOrDefault(id) : null;
            writer.WriteLine(string.Join(",",
                line.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Field(category?.Name ?? ""),
                Field(project?.Name ?? ""),
                Field(line.Note),
                Hours(line.Active),
                Hours(line.Productive),
                Hours(line.Assigned),
                line.Logged is { } logged ? Hours(logged) : ""));
        }
    }

    public static string Hours(TimeSpan time) => time.TotalHours.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Field(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}
