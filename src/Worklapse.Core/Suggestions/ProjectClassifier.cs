namespace Worklapse.Core.Suggestions;

/// <summary>A capture's words and what it was: a project, or no project (null), for as long as it lasted.</summary>
public sealed record TrainingExample(IReadOnlyList<string> Words, long? ProjectId, double Seconds);

/// <summary>
/// Guesses which project a capture's words (<see cref="CaptureWords"/>) belong to, or none: multinomial naive Bayes,
/// each example counted by how long its capture lasted. Retrained from scratch whenever the labels change; with
/// months of history that takes milliseconds.
/// </summary>
public sealed class ProjectClassifier
{
    /// <summary>
    /// How much a word read off the screen counts against a title word. Screens hold hundreds of words, titles a few;
    /// tuned on a frame-labelled day (2026-06-17: 0.1, 0.3 and 1.0 gave 89%, 94% and 92%).
    /// </summary>
    public const double ScreenWordWeight = 0.3;

    /// <summary>Added to every word's count, so a word never seen with a project doesn't rule it out.</summary>
    private const double Smoothing = 0.1;

    /// <summary>Evidence words shorter than this aren't shown.</summary>
    private const int MinEvidenceLength = 4;

    /// <summary>Examples are counted in units of this many seconds, so smoothing means the same at any capture interval.</summary>
    private const double SecondsPerCount = 10;

    private readonly long?[] _labels;
    private readonly double[] _logPriors;
    private readonly Dictionary<string, double>[] _counts;
    private readonly double[] _logTotals;
    private readonly HashSet<string> _vocabulary;

    private ProjectClassifier(long?[] labels, double[] logPriors, Dictionary<string, double>[] counts, double[] logTotals, HashSet<string> vocabulary)
    {
        _labels = labels;
        _logPriors = logPriors;
        _counts = counts;
        _logTotals = logTotals;
        _vocabulary = vocabulary;
    }

    /// <summary>What it can answer, in the order <see cref="Probabilities"/> gives them: projects, and null for none.</summary>
    public IReadOnlyList<long?> Labels => _labels;

    public bool IsEmpty => _labels.Length == 0;

    public static ProjectClassifier Train(IEnumerable<TrainingExample> examples)
    {
        var index = new Dictionary<long, int>();
        var noneIndex = -1;
        var labels = new List<long?>();
        var seconds = new List<double>();
        var counts = new List<Dictionary<string, double>>();
        var vocabulary = new HashSet<string>();

        foreach (var example in examples)
        {
            if (example.Seconds <= 0)
                continue;
            int i;
            if (example.ProjectId is { } project)
            {
                if (!index.TryGetValue(project, out i))
                    index[project] = i = Add(project);
            }
            else
            {
                if (noneIndex < 0)
                    noneIndex = Add(null);
                i = noneIndex;
            }
            seconds[i] += example.Seconds;
            var weight = example.Seconds / SecondsPerCount;
            foreach (var word in example.Words)
            {
                counts[i][word] = counts[i].GetValueOrDefault(word) + weight;
                vocabulary.Add(word);
            }
        }

        var total = seconds.Sum();
        var logPriors = seconds.Select(s => Math.Log(s / total)).ToArray();
        // One more than the vocabulary: room for words never seen in training, which every label smooths alike.
        var vocabularySize = vocabulary.Count + 1;
        var logTotals = counts.Select(c => Math.Log(c.Values.Sum() + Smoothing * vocabularySize)).ToArray();
        return new ProjectClassifier(labels.ToArray(), logPriors, counts.ToArray(), logTotals, vocabulary);

        int Add(long? label)
        {
            labels.Add(label);
            seconds.Add(0);
            counts.Add([]);
            return labels.Count - 1;
        }
    }

    /// <summary>How likely each of <see cref="Labels"/> is for these words; they add up to 1.</summary>
    public double[] Probabilities(IReadOnlyList<string> words)
    {
        var scores = LogScores(words);
        if (scores.Length == 0)
            return scores;
        var max = scores.Max();
        var sum = 0.0;
        for (var i = 0; i < scores.Length; i++)
            sum += scores[i] = Math.Exp(scores[i] - max);
        for (var i = 0; i < scores.Length; i++)
            scores[i] /= sum;
        return scores;
    }

    /// <summary>
    /// The words that pull most toward <paramref name="label"/> over the next best label, most first, each counted once
    /// per occurrence in <paramref name="words"/>: what a suggestion shows as its evidence.
    /// </summary>
    public List<string> Evidence(IEnumerable<IReadOnlyList<string>> words, long? label, int count)
    {
        var target = Array.IndexOf(_labels, label);
        if (target < 0)
            return [];
        var pull = new Dictionary<string, double>();
        foreach (var list in words)
            foreach (var word in list)
            {
                // Short words ("of", "to") pull, but say nothing to the developer.
                if (!Known(word) || word.Length - word.IndexOf(':') - 1 < MinEvidenceLength)
                    continue;
                var own = LogLikelihood(target, word);
                var best = double.NegativeInfinity;
                for (var i = 0; i < _labels.Length; i++)
                    if (i != target)
                        best = Math.Max(best, LogLikelihood(i, word));
                // With no other label to beat, the words most likely for this one.
                var gain = double.IsNegativeInfinity(best) ? own - Math.Log(Smoothing) + _logTotals[target] : own - best;
                pull[word] = pull.GetValueOrDefault(word) + Weight(word) * gain;
            }
        return pull.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Take(count).Select(p => p.Key).ToList();
    }

    private double[] LogScores(IReadOnlyList<string> words)
    {
        var scores = (double[])_logPriors.Clone();
        foreach (var word in words)
        {
            if (!Known(word))
                continue; // a word no label has seen counts the same for all of them
            var weight = Weight(word);
            for (var i = 0; i < scores.Length; i++)
                scores[i] += weight * LogLikelihood(i, word);
        }
        return scores;
    }

    private bool Known(string word) => _vocabulary.Contains(word);

    private double LogLikelihood(int label, string word) =>
        Math.Log(_counts[label].GetValueOrDefault(word) + Smoothing) - _logTotals[label];

    private static double Weight(string word) => word.StartsWith(CaptureWords.ScreenPrefix, StringComparison.Ordinal) ? ScreenWordWeight : 1;
}
