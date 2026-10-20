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
}

/// <summary>
/// Turns a day's captures into suggested blocks. Captures with a project (by hand, link or title rule) keep it; the
/// classifier guesses the rest; a path that charges for every change of project (<see cref="SwitchCost"/>) turns the
/// guesses into stretches rather than flicker; then each project's stretches are joined across time away up to the
/// split limit, with what lies between as breaks and switches for the developer to decide.
/// </summary>
public static class SuggestionBuilder
{
    /// <summary>What changing project costs on the path, in log probability; tuned on the developer's labelled days.</summary>
    public const double SwitchCost = 6;

    /// <summary>A block that would add less than this and has nothing to decide isn't worth showing.</summary>
    public static readonly TimeSpan MinimumAdds = TimeSpan.FromMinutes(1);

    /// <summary>How many words a suggestion shows as evidence.</summary>
    private const int EvidenceWords = 8;

    public static List<Suggestion> ForDay(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        ProjectClassifier classifier, SuggestionLimits limits, TimeSpan maxCaptureLength, SuggestionOptOuts optOuts,
        Func<long, IReadOnlyCollection<string>?>? screenWords = null)
    {
        var day = Day.Read(captures, spans, classifier, maxCaptureLength, optOuts, screenWords, useAssignments: true);
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

            // The block: this project's stretches, joined while the time away between them stays within the split limit.
            var end = i;
            for (var k = i + 1; k < pieces.Count && pieces[k].From - pieces[end].To <= limits.Split; k++)
                if (day.Labels[k] == project)
                    end = k;

            if (!optOuts.UnsuggestedProjects.Contains(project)
                && Block(day, i, end, project, limits, classifier) is { } suggestion
                && (suggestion.Adds >= MinimumAdds || suggestion.Parts.Count > 0))
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
        var day = Day.Read(captures, spans, classifier, maxCaptureLength, optOuts, screenWords, useAssignments);
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
                AwayParts(day, a, b, project, parts);
            }
            a = b;
        }

        var start = pieces[first].From;
        var stop = pieces[last].To;
        var confidence = guessedSeconds > 0 ? certainty / guessedSeconds : 1;
        return new Suggestion(start, stop, project, confidence, adds, parts, classifier.Evidence(evidence, project, EvidenceWords));
    }

    /// <summary>
    /// The breaks and switches in the time away between pieces <paramref name="a"/> - 1 and <paramref name="b"/>: time
    /// guessed as another project is a switch; time without a project and time not recorded are breaks; time that already
    /// has a project (or was set to none by hand) is left alone and parts nothing around it.
    /// </summary>
    private static void AwayParts(Day day, int a, int b, long project, List<SuggestionPart> parts)
    {
        var pieces = day.Pieces;
        SuggestionPart? open = null;
        var cursor = pieces[a - 1].To;
        for (var k = a; k <= b; k++)
        {
            // Not recorded between the last piece and this one: a break.
            if (pieces[k].From > cursor)
                Extend(cursor, pieces[k].From, SuggestionPartKind.Break, null);
            if (k == b)
                break;
            var piece = pieces[k];
            if (piece.State == PieceState.Assigned)
                Close();
            else if (piece.State == PieceState.Guessed && day.Labels[k] is { } other && other != project)
                Extend(piece.From, piece.To, SuggestionPartKind.Switch, other);
            else
                Extend(piece.From, piece.To, SuggestionPartKind.Break, null);
            cursor = piece.To;
        }
        Close();

        void Extend(DateTimeOffset from, DateTimeOffset to, SuggestionPartKind kind, long? other)
        {
            if (open is { } current && current.Kind == kind && current.ProjectId == other && current.End == from)
            {
                open = current with { End = to };
                return;
            }
            Close();
            open = new SuggestionPart(from, to, kind, other);
        }

        void Close()
        {
            if (open is not null)
                parts.Add(open);
            open = null;
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
            TimeSpan maxCaptureLength, SuggestionOptOuts optOuts, Func<long, IReadOnlyCollection<string>?>? screenWords, bool useAssignments)
        {
            var assignments = TrainingSet.Assignments(captures, spans);
            var seconds = CaptureLengths.Of(captures, maxCaptureLength);
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
                pieces.Add(new Piece(i, capture.Time, capture.Time.AddSeconds(seconds[i]), state, words));
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
                        for (var l = 0; l < probabilities.Length; l++)
                            emission[l] = Math.Log(probabilities[l]);
                        if (probabilities.Length == 0)
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
