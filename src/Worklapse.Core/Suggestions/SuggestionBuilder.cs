using Worklapse.Core.History;
using Worklapse.Core.Projects;

namespace Worklapse.Core.Suggestions;

public enum SuggestionPartKind
{
    /// <summary>Time away inside a suggested block (idle, not recorded, unproductive or another app): counted toward it only if the developer says so.</summary>
    Break,

    /// <summary>A stretch that looks like another project's work inside a suggested block: split out as that project unless the developer keeps it in.</summary>
    Switch,
}

/// <param name="ProjectId">The other project, for a <see cref="SuggestionPartKind.Switch"/>; null for a break.</param>
public sealed record SuggestionPart(DateTimeOffset Start, DateTimeOffset End, SuggestionPartKind Kind, long? ProjectId);

/// <summary>A suggested billing block: a stretch to give one project, made of time without one.</summary>
/// <param name="Adds">Recorded time with no project that accepting gives the project (its parts aside).</param>
/// <param name="Confidence">The classifier's average certainty over the time it guessed, 0 to 1; 1 when it guessed none.</param>
/// <param name="Evidence">The words that pointed to the project most (<see cref="CaptureWords"/>, prefixed).</param>
public sealed record Suggestion(DateTimeOffset Start, DateTimeOffset End, long ProjectId, double Confidence, TimeSpan Adds,
    IReadOnlyList<SuggestionPart> Parts, IReadOnlyList<string> Evidence)
{
    /// <summary>Below this, a suggestion is shown as unsure.</summary>
    public const double UnsureBelow = 0.7;

    public bool IsUnsure => Confidence < UnsureBelow;
}

/// <param name="Glance">Time away shorter than this is part of the block, no question asked.</param>
/// <param name="Split">Time away longer than this ends the block.</param>
public sealed record SuggestionLimits(TimeSpan Glance, TimeSpan Split)
{
    public static SuggestionLimits Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));

    /// <summary>
    /// Other projects' work inside the time away longer than this ends the block too: a switch is a short excursion, and
    /// projects worked on in turn are blocks of their own, not one block swallowing the other.
    /// </summary>
    public TimeSpan Switch { get; init; } = TrainingSet.ShortSwitch;

    /// <summary>A block the classifier is less sure of than this (0 to 1) isn't offered.</summary>
    public double MinimumCertainty { get; init; }
}

/// <summary>
/// Turns a day's captures into suggested blocks. Captures with a project (by hand, link or title rule) keep it; the
/// classifier guesses the rest; a path that charges for every change of project (<see cref="SwitchCost"/>) turns the
/// guesses into stretches rather than flicker; then each project's stretches are joined across time away up to the
/// split limit (while other projects' work in it stays short), with what lies between as breaks and switches for the
/// developer to decide.
/// </summary>
public static class SuggestionBuilder
{
    /// <summary>What changing project costs on the path, in log probability; tuned on the developer's labelled days.</summary>
    public const double SwitchCost = 6;

    /// <summary>A block that would add less than this and has nothing to decide isn't worth showing.</summary>
    public static readonly TimeSpan MinimumAdds = TimeSpan.FromMinutes(1);

    /// <summary>A break or switch shorter than this isn't asked about: it's part of the block, like a glance.</summary>
    public static readonly TimeSpan MinimumPart = TimeSpan.FromMinutes(1);

    /// <summary>How many words a suggestion shows as evidence.</summary>
    private const int EvidenceWords = 8;

    /// <param name="notRunning">Projects outside their dates on this day: never guessed nor suggested.</param>
    public static List<Suggestion> ForDay(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        ProjectClassifier classifier, SuggestionLimits limits, TimeSpan maxCaptureLength, SuggestionOptOuts optOuts,
        Func<long, IReadOnlyCollection<string>?>? screenWords = null, IReadOnlySet<long>? notRunning = null)
    {
        notRunning ??= new HashSet<long>();
        var day = Day.Read(captures, spans, classifier, maxCaptureLength, optOuts, screenWords, useAssignments: true, notRunning);
        var pieces = day.Pieces;
        var suggestions = new List<Suggestion>();
        var i = 0;
        while (i < pieces.Count)
        {
            if (day.Labels[i] is not { } project)
            {
                i++;
                continue;
            }

            // The block: this project's stretches, joined while the time away between them stays within the split limit, the
            // other projects' work in each stretch away within the switch limit, and the block still mostly this project's:
            // where two projects take turns, the one with more time has the block and the other's turns are its switches.
            var end = i;
            var own = pieces[i].Length;
            var (others, othersJoined) = (TimeSpan.Zero, TimeSpan.Zero);
            for (var k = i + 1; k < pieces.Count && pieces[k].From - pieces[end].To <= limits.Split; k++)
            {
                if (day.Labels[k] == project)
                {
                    if (othersJoined + others > own)
                        break;
                    end = k;
                    own += pieces[k].Length;
                    othersJoined += others;
                    others = TimeSpan.Zero;
                }
                else if (day.Labels[k] is not null && (others += pieces[k].Length) > limits.Switch)
                    break;
            }

            if (!optOuts.UnsuggestedProjects.Contains(project) && !notRunning.Contains(project)
                && Block(day, i, end, project, limits, classifier) is { } suggestion
                && (suggestion.Adds >= MinimumAdds || suggestion.Parts.Count > 0)
                && suggestion.Confidence >= limits.MinimumCertainty)
                suggestions.Add(suggestion);
            i = end + 1;
        }
        return suggestions;
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

    private static Suggestion? Block(Day day, int first, int last, long project, SuggestionLimits limits, ProjectClassifier classifier)
    {
        var pieces = day.Pieces;
        var parts = new List<SuggestionPart>();
        var adds = TimeSpan.Zero;
        var (guessedSeconds, certainty) = (0.0, 0.0);
        var evidence = new List<IReadOnlyList<string>>();

        var a = first;
        while (a <= last)
        {
            // A piece of the project itself: time it adds if nothing gave it a project yet.
            if (day.Labels[a] == project)
            {
                if (pieces[a].State == PieceState.Guessed)
                {
                    adds += pieces[a].Length;
                    guessedSeconds += pieces[a].Length.TotalSeconds;
                    certainty += pieces[a].Length.TotalSeconds * pieces[a].Certainty;
                    evidence.Add(pieces[a].Words);
                }
                a++;
                continue;
            }

            // Time away, up to the project's next piece.
            var b = a;
            while (day.Labels[b] != project)
                b++;
            var away = pieces[b].From - pieces[a - 1].To;
            if (away < limits.Glance)
            {
                for (var k = a; k < b; k++)
                    if (pieces[k].State != PieceState.Assigned)
                        adds += pieces[k].Length;
            }
            else
            {
                adds += AwayParts(day, a, b, project, parts);
            }
            a = b;
        }

        var start = pieces[first].From;
        var stop = pieces[last].To;
        var confidence = guessedSeconds > 0 ? certainty / guessedSeconds : 1;
        return new Suggestion(start, stop, project, confidence, adds, parts, classifier.Evidence(evidence, project, EvidenceWords));
    }

    /// <summary>
    /// The breaks and switches in the time away between pieces <paramref name="a"/> - 1 and <paramref name="b"/>: one part
    /// for each stretch of it that has no project (time not recorded included), split only where time already has one (or
    /// was set to none by hand), which is left alone. A stretch mostly guessed as another project is a switch to it;
    /// otherwise a break. Stretches shorter than <see cref="MinimumPart"/> aren't asked about: they go with the block.
    /// </summary>
    /// <returns>The recorded time of those short stretches, which the block adds.</returns>
    private static TimeSpan AwayParts(Day day, int a, int b, long project, List<SuggestionPart> parts)
    {
        var pieces = day.Pieces;
        var absorbed = TimeSpan.Zero;
        var cursor = pieces[a - 1].To;
        DateTimeOffset? start = null;
        var recorded = TimeSpan.Zero;
        var otherProjects = new Dictionary<long, TimeSpan>();
        for (var k = a; k <= b; k++)
        {
            if (k == b || pieces[k].State == PieceState.Assigned)
            {
                Close(pieces[k].From);
                cursor = pieces[k].To;
                continue;
            }
            var piece = pieces[k];
            start ??= cursor; // from the end of what came before: time not recorded is part of it
            recorded += piece.Length;
            if (piece.State == PieceState.Guessed && day.Labels[k] is { } other && other != project)
                otherProjects[other] = otherProjects.GetValueOrDefault(other) + piece.Length;
            cursor = piece.To;
        }
        return absorbed;

        void Close(DateTimeOffset end)
        {
            var from = start ?? cursor;
            if (end - from >= MinimumPart)
            {
                var (main, time) = otherProjects.OrderByDescending(o => o.Value).FirstOrDefault();
                parts.Add(time > TimeSpan.Zero && time * 2 >= end - from
                    ? new SuggestionPart(from, end, SuggestionPartKind.Switch, main)
                    : new SuggestionPart(from, end, SuggestionPartKind.Break, null));
            }
            else
                absorbed += recorded;
            start = null;
            recorded = TimeSpan.Zero;
            otherProjects.Clear();
        }
    }

    private enum PieceState
    {
        /// <summary>A project (or none) set by hand, a link or a title rule: kept as it is.</summary>
        Assigned,

        /// <summary>Active and productive with nothing set: the classifier guesses.</summary>
        Guessed,

        /// <summary>Idle or unproductive with nothing set: no project, but time a block may count as a break.</summary>
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
                    : capture.IsActive && capture.Productive ? PieceState.Guessed
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
