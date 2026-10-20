using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Suggestions;

/// <summary>
/// What suggestions leave alone: projects never suggested (and whose time teaches nothing; archived projects belong here
/// too), and apps and sites whose windows neither teach nor sway suggestions.
/// </summary>
public sealed record SuggestionOptOuts(IReadOnlySet<long> UnsuggestedProjects, IReadOnlySet<string> UnlearnedApps, IReadOnlySet<string> UnlearnedSites)
{
    public static SuggestionOptOuts None { get; } = new(new HashSet<long>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The opt-outs as set in the database; archived projects count as never suggested.</summary>
    public static SuggestionOptOuts Load(WorklapseDatabase db)
    {
        var projects = new ProjectStore(db).GetProjects().Where(p => !p.Suggest || p.Archived).Select(p => p.Id).ToHashSet();
        return new SuggestionOptOuts(projects, Names("SELECT DISTINCT exe_name FROM apps WHERE learn = 0;"),
            Names("SELECT domain FROM sites WHERE learn = 0;"));

        HashSet<string> Names(string sql)
        {
            using var command = db.Command(sql);
            using var reader = command.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }
    }

    /// <summary>Whether a capture's window counts: its site decides when it has one, else its app (as for Productive).</summary>
    public bool Learns(CaptureRecord capture) =>
        capture.Site is { } site ? !UnlearnedSites.Contains(site) : capture.AppName is not { } app || !UnlearnedApps.Contains(app);
}

/// <summary>The project a capture has as far as the developer has said, and how it got it.</summary>
/// <param name="ProjectId">The project; null for none (a range set by hand with no project, or nothing at all).</param>
/// <param name="ByHand">From a range set by hand, which decides over links and title rules.</param>
/// <param name="IsAssigned">Something gave it a project, or a range by hand said none; false when nothing has.</param>
public readonly record struct Assignment(long? ProjectId, bool ByHand, bool IsAssigned);

/// <summary>What history teaches the <see cref="ProjectClassifier"/>.</summary>
public static class TrainingSet
{
    /// <summary>
    /// A stretch of another project's linked windows inside a range set by hand, shorter than this, is left out of
    /// training: the developer sometimes splits such a switch out and sometimes leaves it in the block, so it would
    /// teach the surrounding project's name for the other project's windows.
    /// </summary>
    public static readonly TimeSpan ShortSwitch = TimeSpan.FromMinutes(10);

    /// <summary>How long a project's name, links and title rules count as an example, so a new project can be suggested at all.</summary>
    public const double StarterSeconds = 60;

    /// <summary>Each capture's project as the developer has set it: a range by hand over it, else its link or title rule.</summary>
    /// <param name="spans">The ranges set by hand, in time order, not overlapping.</param>
    public static Assignment[] Assignments(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans)
    {
        var assignments = new Assignment[captures.Count];
        var s = 0;
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            while (s < spans.Count && spans[s].End <= capture.Time)
                s++;
            if (s < spans.Count && spans[s].Start <= capture.Time)
                assignments[i] = new Assignment(spans[s].ProjectId, ByHand: true, IsAssigned: true);
            else if (capture.IsActive && capture.LinkedProjectId is { } linked)
                assignments[i] = new Assignment(linked, ByHand: false, IsAssigned: true);
        }
        return assignments;
    }

    /// <summary>
    /// The examples one day gives: every active capture with a project set by hand, link or title rule (or set to none by
    /// hand), counted for as long as it lasted. An unproductive capture counts as no project whatever its range says:
    /// a glance at email inside a billed block isn't that project's work. Left out: captures nothing says anything about,
    /// marked for deletion, of projects never suggested or of apps and sites that don't teach, and short switches
    /// (<see cref="ShortSwitch"/>).
    /// </summary>
    /// <param name="screenWords">Words read off a capture's screen, by capture id; null when none were read.</param>
    public static IEnumerable<TrainingExample> FromDay(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        TimeSpan maxCaptureLength, SuggestionOptOuts optOuts, Func<long, IReadOnlyCollection<string>?>? screenWords = null)
    {
        var assignments = Assignments(captures, spans);
        var seconds = CaptureLengths.Of(captures, maxCaptureLength);
        var shortSwitches = ShortSwitches(captures, assignments, seconds);
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            var assignment = assignments[i];
            if (!capture.IsActive || capture.PendingDelete || !assignment.IsAssigned || shortSwitches[i] || !optOuts.Learns(capture))
                continue;
            if (assignment.ProjectId is { } project && optOuts.UnsuggestedProjects.Contains(project))
                continue;
            var label = capture.Productive ? assignment.ProjectId : null;
            yield return new TrainingExample(CaptureWords.Of(capture, screenWords?.Invoke(capture.Id)), label, seconds[i]);
        }
    }

    /// <summary>
    /// One example per project from what the developer has already said about it: its name, its linked apps and sites,
    /// and the text of its title rules, as if seen for <see cref="StarterSeconds"/>.
    /// </summary>
    public static IEnumerable<TrainingExample> Starters(IEnumerable<Project> projects, IEnumerable<TitleRule> rules,
        IEnumerable<ProgramUsage> programs, SuggestionOptOuts optOuts)
    {
        var rulesByProject = rules.ToLookup(r => r.ProjectId);
        var programsByProject = programs.Where(p => p.ProjectId is not null).ToLookup(p => p.ProjectId!.Value);
        foreach (var project in projects)
        {
            if (project.Archived || optOuts.UnsuggestedProjects.Contains(project.Id))
                continue;
            var words = CaptureWords.Of(null, null, project.Name);
            foreach (var rule in rulesByProject[project.Id])
                words.AddRange(CaptureWords.Of(rule.App, null, rule.Text));
            foreach (var program in programsByProject[project.Id])
                words.AddRange(program.Program.Kind == ProgramKind.App
                    ? CaptureWords.Of(program.Program.Name, null, null)
                    : CaptureWords.Of(null, program.Program.Name, null));
            yield return new TrainingExample(words, project.Id, StarterSeconds);
        }
    }

    /// <summary>
    /// Captures inside a range set by hand whose own link or rule names another project, in a run of such captures shorter
    /// than <see cref="ShortSwitch"/>.
    /// </summary>
    private static bool[] ShortSwitches(IReadOnlyList<CaptureRecord> captures, Assignment[] assignments, double[] seconds)
    {
        var result = new bool[captures.Count];
        var i = 0;
        while (i < captures.Count)
        {
            if (!IsSwitch(i))
            {
                i++;
                continue;
            }
            var other = captures[i].LinkedProjectId;
            var end = i;
            var length = 0.0;
            while (end < captures.Count && IsSwitch(end) && captures[end].LinkedProjectId == other
                   && assignments[end].ProjectId == assignments[i].ProjectId)
                length += seconds[end++];
            if (length < ShortSwitch.TotalSeconds)
                for (var k = i; k < end; k++)
                    result[k] = true;
            i = end;
        }
        return result;

        bool IsSwitch(int k) => assignments[k] is { ByHand: true, ProjectId: { } own }
                                && captures[k].IsActive && captures[k].LinkedProjectId is { } linked && linked != own;
    }
}

/// <summary>How long each capture stands for, as <see cref="SpanSummary.For"/> counts it.</summary>
internal static class CaptureLengths
{
    /// <summary>Seconds per capture: until the next one, at most <paramref name="maxCaptureLength"/>.</summary>
    public static double[] Of(IReadOnlyList<CaptureRecord> captures, TimeSpan maxCaptureLength)
    {
        var seconds = new double[captures.Count];
        for (var i = 0; i < captures.Count; i++)
        {
            var length = i + 1 < captures.Count ? captures[i + 1].Time - captures[i].Time : maxCaptureLength;
            seconds[i] = (length > maxCaptureLength ? maxCaptureLength : length).TotalSeconds;
        }
        return seconds;
    }
}
