using Worklapse.Core.History;
using Worklapse.Core.Projects;

namespace Worklapse.Core.Suggestions;

/// <summary>
/// A stretch of time without a project that looks like one project's work: to accept, reject or give another project.
/// It runs over the project's own time and glances away, so it may hold time that has the project already.
/// </summary>
/// <param name="Adds">Recorded time with no project that accepting gives the project.</param>
/// <param name="Confidence">The classifier's average certainty over the time it guessed, 0 to 1.</param>
/// <param name="LeastSure">Where the capture the classifier was least sure of starts: where checking the stretch begins.</param>
/// <param name="Evidence">The words that pointed to the project most (<see cref="CaptureWords"/>, prefixed).</param>
public sealed record Suggestion(DateTimeOffset Start, DateTimeOffset End, long ProjectId, double Confidence, TimeSpan Adds,
    DateTimeOffset LeastSure, IReadOnlyList<string> Evidence)
{
    /// <summary>
    /// Below this, a suggestion is shown as unsure. The classifier is overconfident: on the labelled days (2026-10-23),
    /// stretches it was 95% sure of or more were right about 82% of the time, those below about 66%.
    /// </summary>
    public const double UnsureBelow = 0.95;

    public bool IsUnsure => Confidence < UnsureBelow;
}

/// <summary>
/// Time away (idle, off-task, nothing to go on, not recorded) between two stretches of project time, set or
/// suggested: counted toward one of them only if the developer says so.
/// </summary>
/// <param name="Before">The project of the time just before.</param>
/// <param name="After">The project of the time just after.</param>
public sealed record SuggestedBreak(DateTimeOffset Start, DateTimeOffset End, long Before, long After)
{
    /// <summary>The project on both sides, when it's the same one.</summary>
    public long? Inside => Before == After ? Before : null;
}

/// <summary>A day's suggestions: its stretches and its breaks, each in time order.</summary>
public sealed record DaySuggestions(IReadOnlyList<Suggestion> Stretches, IReadOnlyList<SuggestedBreak> Breaks)
{
    public static DaySuggestions None { get; } = new([], []);
}

/// <param name="Glance">Time away shorter than this is part of a stretch, no question asked.</param>
/// <param name="Split">Time away longer than this isn't asked about.</param>
public sealed record SuggestionLimits(TimeSpan Glance, TimeSpan Split)
{
    public static SuggestionLimits Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));

    /// <summary>A stretch the classifier is less sure of than this (0 to 1) isn't offered.</summary>
    public double MinimumCertainty { get; init; }
}

/// <summary>
/// Turns a day's captures into suggestions. Captures with a project (by hand, link or title rule) keep it; the classifier
/// guesses the rest; a path that charges for every change of project (<see cref="SwitchCost"/>) turns the guesses into
/// stretches rather than flicker. Each project's guessed time is offered in stretches of its own, however projects take
/// turns: a stretch is joined only across glances away and the project's own time. The time away between project time
/// is offered as breaks, for the developer to count toward a project or not.
/// </summary>
public static class SuggestionBuilder
{
    /// <summary>What changing project costs on the path, in log probability; tuned on the developer's labelled days.</summary>
    public const double SwitchCost = 6;

    /// <summary>A stretch that would add less than this isn't worth showing.</summary>
    public static readonly TimeSpan MinimumAdds = TimeSpan.FromMinutes(1);

    /// <summary>How many words a suggestion shows as evidence.</summary>
    private const int EvidenceWords = 8;

    /// <param name="notRunning">Projects outside their dates on this day: never guessed nor suggested.</param>
    public static DaySuggestions ForDay(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        ProjectClassifier classifier, SuggestionLimits limits, TimeSpan maxCaptureLength, SuggestionOptOuts optOuts,
        Func<long, IReadOnlyCollection<string>?>? screenWords = null, IReadOnlySet<long>? notRunning = null)
    {
        notRunning ??= new HashSet<long>();
        var day = Day.Read(captures, spans, classifier, maxCaptureLength, optOuts, screenWords, useAssignments: true, notRunning);
        var pieces = day.Pieces;

        // What the breaks lie between: time with a project, and the time offered stretches guess.
        var projectTime = new bool[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
            projectTime[i] = pieces[i].State == PieceState.Assigned && day.Labels[i] is not null;

        var stretches = new List<Suggestion>();
        var first = 0;
        while (first < pieces.Count)
        {
            if (pieces[first].State != PieceState.Guessed || day.Labels[first] is not { } project)
            {
                first++;
                continue;
            }
            var last = StretchEnd(day, first, project, limits.Glance);
            if (!optOuts.UnsuggestedProjects.Contains(project) && !notRunning.Contains(project)
                && Stretch(day, first, last, project, classifier) is var suggestion
                && suggestion.Adds >= MinimumAdds && suggestion.Confidence >= limits.MinimumCertainty)
            {
                stretches.Add(suggestion);
                for (var k = first; k <= last; k++)
                    projectTime[k] |= day.Labels[k] == project;
            }
            first = last + 1;
        }
        return new DaySuggestions(stretches, Breaks(day, projectTime, limits));
    }

    /// <summary>
    /// Each capture's project as the path decides it, for measuring the classifier: null for none, and for captures that
    /// aren't active or are marked for deletion. With <paramref name="useAssignments"/> off, it guesses every active
    /// capture, as if nothing had been set.
    /// </summary>
    public static long?[] Guess(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans, ProjectClassifier classifier,
        TimeSpan maxCaptureLength, SuggestionOptOuts optOuts, Func<long, IReadOnlyCollection<string>?>? screenWords, bool useAssignments)
    {
        var day = Day.Read(captures, spans, classifier, maxCaptureLength, optOuts, screenWords, useAssignments, new HashSet<long>());
        var guesses = new long?[captures.Count];
        for (var i = 0; i < day.Pieces.Count; i++)
            if (captures[day.Pieces[i].Capture].IsActive)
                guesses[day.Pieces[i].Capture] = day.Labels[i];
        return guesses;
    }

    /// <summary>
    /// The last piece of the stretch of <paramref name="project"/> starting at the guessed piece <paramref name="first"/>:
    /// the project's pieces, guessed or set, joined across time away shorter than <paramref name="glance"/>. Another
    /// project's time, or time set to none by hand, ends it. It ends on guessed time: what's set to the project already at
    /// its end stays out.
    /// </summary>
    private static int StretchEnd(Day day, int first, long project, TimeSpan glance)
    {
        var pieces = day.Pieces;
        var last = first;
        for (var k = first + 1; k < pieces.Count; k++)
        {
            var away = pieces[k].To - pieces[last].To;
            if (day.Labels[k] == project)
            {
                if (pieces[k].From > pieces[last].To && pieces[k].From - pieces[last].To >= glance)
                    break;
                last = k;
            }
            else if (pieces[k].State == PieceState.Assigned || day.Labels[k] is not null || away >= glance)
                break;
        }
        while (pieces[last].State != PieceState.Guessed)
            last--;
        return last;
    }

    /// <summary>The stretch from piece <paramref name="first"/> to <paramref name="last"/> (both guessed) as a suggestion.</summary>
    private static Suggestion Stretch(Day day, int first, int last, long project, ProjectClassifier classifier)
    {
        var pieces = day.Pieces;
        var adds = TimeSpan.Zero;
        var (guessedSeconds, certainty) = (0.0, 0.0);
        var leastSure = pieces[first];
        var evidence = new List<IReadOnlyList<string>>();
        for (var k = first; k <= last; k++)
        {
            var piece = pieces[k];
            if (piece.State == PieceState.Assigned)
                continue;
            // Guessed as the project, or a glance away: time accepting gives the project.
            adds += piece.Length;
            if (piece.State != PieceState.Guessed || day.Labels[k] != project)
                continue;
            guessedSeconds += piece.Length.TotalSeconds;
            certainty += piece.Length.TotalSeconds * piece.Certainty;
            if (piece.Certainty < leastSure.Certainty)
                leastSure = piece;
            evidence.Add(piece.Words);
        }
        return new Suggestion(pieces[first].From, pieces[last].To, project, certainty / guessedSeconds, adds, leastSure.From,
            classifier.Evidence(evidence, project, EvidenceWords));
    }

    /// <summary>
    /// The time away between pieces of project time (<paramref name="projectTime"/>), from the glance limit up to the split
    /// limit: time with no project, including time not recorded and guesses not offered. Time set to none by hand ends it.
    /// </summary>
    private static List<SuggestedBreak> Breaks(Day day, bool[] projectTime, SuggestionLimits limits)
    {
        var pieces = day.Pieces;
        var breaks = new List<SuggestedBreak>();
        int? before = null;
        for (var k = 0; k < pieces.Count; k++)
        {
            if (projectTime[k])
            {
                if (before is { } b && day.Labels[b] is { } from && day.Labels[k] is { } to
                    && pieces[k].From - pieces[b].To is var away && away >= limits.Glance && away <= limits.Split)
                    breaks.Add(new SuggestedBreak(pieces[b].To, pieces[k].From, from, to));
                before = k;
            }
            else if (pieces[k].State == PieceState.Assigned)
                before = null; // set to none by hand
        }
        return breaks;
    }

    private enum PieceState
    {
        /// <summary>A project (or none) set by hand, a link or a title rule: kept as it is.</summary>
        Assigned,

        /// <summary>Active and on-task with nothing set: the classifier guesses.</summary>
        Guessed,

        /// <summary>Idle or off-task with nothing set: no project, but time a break may count.</summary>
        Away,
    }

    /// <summary>The time one capture stands for, and what's known or guessed about it.</summary>
    private sealed record Piece(int Capture, DateTimeOffset From, DateTimeOffset To, PieceState State, IReadOnlyList<string> Words)
    {
        public TimeSpan Length => To - From;

        /// <summary>For a guessed piece, the classifier's probability for the project the path chose.</summary>
        public double Certainty { get; set; }
    }

    /// <summary>A day's pieces (captures marked for deletion left out) and the project the path gives each.</summary>
    private sealed record Day(List<Piece> Pieces, long?[] Labels)
    {
        public static Day Read(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans, ProjectClassifier classifier,
            TimeSpan maxCaptureLength, SuggestionOptOuts optOuts, Func<long, IReadOnlyCollection<string>?>? screenWords, bool useAssignments,
            IReadOnlySet<long> notRunning)
        {
            var assignments = TrainingSet.Assignments(captures, spans);
            var lengths = CaptureLengths.Spans(captures, maxCaptureLength);
            var pieces = new List<Piece>();
            for (var i = 0; i < captures.Count; i++)
            {
                var capture = captures[i];
                if (capture.PendingDelete)
                    continue;
                var state = useAssignments && assignments[i].IsAssigned ? PieceState.Assigned
                    : capture.IsActive && capture.OnTask ? PieceState.Guessed
                    : PieceState.Away;
                var words = state == PieceState.Guessed && optOuts.Learns(capture)
                    ? CaptureWords.Of(capture, screenWords?.Invoke(capture.Id))
                    : (IReadOnlyList<string>)[];
                pieces.Add(new Piece(i, capture.Time, capture.Time + lengths[i], state, words));
            }

            // Every label the path may use: the classifier's, none, and whatever is already assigned that day.
            var labels = new List<long?>(classifier.Labels);
            if (!labels.Contains(null))
                labels.Add(null);
            foreach (var piece in pieces)
                if (piece.State == PieceState.Assigned && !labels.Contains(assignments[piece.Capture].ProjectId))
                    labels.Add(assignments[piece.Capture].ProjectId);

            var none = labels.IndexOf(null);
            var emissions = new double[pieces.Count][];
            for (var p = 0; p < pieces.Count; p++)
            {
                var emission = emissions[p] = new double[labels.Count];
                Array.Fill(emission, double.NegativeInfinity);
                switch (pieces[p].State)
                {
                    case PieceState.Assigned:
                        emission[labels.IndexOf(assignments[pieces[p].Capture].ProjectId)] = 0;
                        break;
                    case PieceState.Away:
                        emission[none] = 0;
                        break;
                    default:
                        var probabilities = classifier.Probabilities(pieces[p].Words);
                        // A project outside its dates isn't a guess: the others share its probability.
                        var allowed = 0.0;
                        for (var l = 0; l < probabilities.Length; l++)
                            if (labels[l] is not { } id || !notRunning.Contains(id))
                                allowed += probabilities[l];
                        for (var l = 0; l < probabilities.Length; l++)
                            if (allowed > 0 && (labels[l] is not { } id || !notRunning.Contains(id)))
                                emission[l] = Math.Log(probabilities[l] / allowed);
                        if (probabilities.Length == 0 || allowed <= 0)
                            emission[none] = 0;
                        break;
                }
            }

            var path = BlockPath.Find(emissions, SwitchCost);
            var result = new long?[pieces.Count];
            for (var p = 0; p < pieces.Count; p++)
            {
                result[p] = labels[path[p]];
                if (pieces[p].State == PieceState.Guessed && path[p] < classifier.Labels.Count)
                    pieces[p].Certainty = Math.Exp(emissions[p][path[p]]);
            }
            return new Day(pieces, result);
        }
    }
}

/// <summary>The most likely label for each step of a sequence when every change of label costs the same.</summary>
internal static class BlockPath
{
    /// <param name="logEmissions">Per step, the log probability of each label; negative infinity rules a label out.</param>
    /// <param name="switchCost">What changing label costs, in log probability.</param>
    /// <returns>The chosen label's index per step.</returns>
    public static int[] Find(IReadOnlyList<double[]> logEmissions, double switchCost)
    {
        if (logEmissions.Count == 0)
            return [];
        var count = logEmissions[0].Length;
        var score = (double[])logEmissions[0].Clone();
        var from = new int[logEmissions.Count][];
        for (var t = 1; t < logEmissions.Count; t++)
        {
            var best = 0;
            for (var l = 1; l < count; l++)
                if (score[l] > score[best])
                    best = l;
            var next = new double[count];
            var back = from[t] = new int[count];
            for (var l = 0; l < count; l++)
            {
                var stay = score[l];
                var change = score[best] - switchCost;
                back[l] = stay >= change ? l : best;
                next[l] = Math.Max(stay, change) + logEmissions[t][l];
            }
            score = next;
        }

        var path = new int[logEmissions.Count];
        var last = 0;
        for (var l = 1; l < count; l++)
            if (score[l] > score[last])
                last = l;
        path[^1] = last;
        for (var t = logEmissions.Count - 1; t > 0; t--)
            path[t - 1] = from[t][path[t]];
        return path;
    }
}
