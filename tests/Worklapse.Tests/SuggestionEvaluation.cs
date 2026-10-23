using System.Text;
using System.Text.Json;
using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Tests;

/// <summary>
/// Measures the project classifier on real history: each day with ranges set by hand is guessed by a classifier trained on
/// every other day, and compared with what the developer set. Explicit, so it runs only when asked:
/// <code>
/// WORKLAPSE_EVAL_DATA=&lt;a folder holding a COPY of worklapse.db&gt; Worklapse.Tests.exe -method "*SuggestionEvaluation*" -explicit only -showliveoutput on
/// </code>
/// Optional: WORKLAPSE_EVAL_OCR, the words read off screens: "db" for those in the database (screen_words), or a .jsonl of
/// {"id": capture id, "text": text read off its screen}; unset for none. WORKLAPSE_EVAL_FULL_DAYS,
/// comma-separated days labelled frame by frame (outside their ranges, links count and the rest is no project; on other
/// days time outside ranges isn't scored). Never point it at the real data folder: opening it may migrate the database.
/// </summary>
public class SuggestionEvaluation
{
    [Fact(Explicit = true)]
    public void LeaveOneDayOut()
    {
        var folder = Environment.GetEnvironmentVariable("WORKLAPSE_EVAL_DATA");
        Assert.False(string.IsNullOrEmpty(folder), "Set WORKLAPSE_EVAL_DATA to a folder holding a copy of worklapse.db");
        var fullDays = (Environment.GetEnvironmentVariable("WORKLAPSE_EVAL_FULL_DAYS") ?? "2026-06-17")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => DateOnly.ParseExact(d.Trim(), "yyyy-MM-dd")).ToHashSet();
        var source = Environment.GetEnvironmentVariable("WORKLAPSE_EVAL_OCR");
        var screen = source == "db" ? [] : ReadScreenWords(source);
        Func<long, IReadOnlyCollection<string>?>? screenWords = screen is null ? null : id => screen.GetValueOrDefault(id);

        var max = TimeSpan.FromSeconds(15);
        using var history = new HistoryStore(WorklapseDatabase.Open(folder!));
        var stored = new ScreenWordStore(history.Database);
        var projects = new ProjectStore(history.Database);
        var days = new List<(DateOnly Day, DayHistory History, List<ProjectSpan> Spans)>();
        foreach (var day in history.GetDays())
        {
            var (start, end) = history.DayRange(day);
            var spans = projects.GetSpans(start, end);
            if (spans.Count == 0)
                continue;
            days.Add((day, history.LoadDay(day), spans));
            if (source == "db")
                foreach (var (id, words) in stored.Get(start, end))
                    screen![id] = words;
        }
        var starters = TrainingSet.Starters(projects.GetProjects(), projects.GetTitleRules(), history.GetPrograms(max), SuggestionOptOuts.None).ToList();
        var examples = days.ToDictionary(d => d.Day, d => TrainingSet.FromDay(d.History.Captures, d.Spans, max, SuggestionOptOuts.None, screenWords).ToList());

        var report = new StringBuilder();
        report.AppendLine($"{"day",-10} {"min",5} {"rules",6} {"guess",6} {"filled",7}   (filled: links and rules kept, the rest guessed, scored where nothing was linked)");
        var totals = new Score[3];
        var offered = new (int Stretches, Score Adds)[CertaintyBands.Length];
        foreach (var (day, dayHistory, spans) in days)
        {
            var classifier = ProjectClassifier.Train(days.Where(d => d.Day != day).SelectMany(d => examples[d.Day]).Concat(starters));
            var captures = dayHistory.Captures;
            var truth = Truth(captures, spans, fullDays.Contains(day));
            var seconds = captures.Select((c, i) => i + 1 < captures.Count ? Math.Min((captures[i + 1].Time - c.Time).TotalSeconds, max.TotalSeconds) : max.TotalSeconds).ToArray();

            // Links and title rules alone; the classifier guessing everything; and as in use: the day's ranges not set yet,
            // links and rules kept, the classifier filling the rest (scored only where nothing was linked).
            var rules = captures.Select(c => c.IsActive ? c.LinkedProjectId : null).ToArray();
            var guessed = SuggestionBuilder.Guess(captures, spans, classifier, max, SuggestionOptOuts.None, screenWords, useAssignments: false);
            var filled = SuggestionBuilder.Guess(captures, [], classifier, max, SuggestionOptOuts.None, screenWords, useAssignments: true);
            var scores = new[]
            {
                Score.Of(truth, rules, seconds, _ => true),
                Score.Of(truth, guessed, seconds, _ => true),
                Score.Of(truth, filled, seconds, i => captures[i].LinkedProjectId is null),
            };
            for (var s = 0; s < scores.Length; s++)
                totals[s] = totals[s].Add(scores[s]);
            report.AppendLine($"{day:yyyy-MM-dd} {scores[0].Seconds / 60,5:F0} {scores[0].Accuracy,6:P0} {scores[1].Accuracy,6:P0} {scores[2].Accuracy,7:P0}");

            // The stretches as offered (the day's ranges not set yet), by their certainty: how much of what each adds is right.
            foreach (var suggestion in SuggestionBuilder.ForDay(captures, [], classifier, SuggestionLimits.Default, max, SuggestionOptOuts.None, screenWords).Stretches)
            {
                var band = Array.FindLastIndex(CertaintyBands, b => suggestion.Confidence >= b);
                var adds = Score.Of(truth, captures.Select(_ => (long?)suggestion.ProjectId).ToArray(), seconds,
                    i => captures[i].LinkedProjectId is null && captures[i].Time >= suggestion.Start && captures[i].Time < suggestion.End);
                offered[band] = (offered[band].Stretches + 1, offered[band].Adds.Add(adds));
            }
        }
        report.AppendLine($"{"all days",-10} {totals[0].Seconds / 60,5:F0} {totals[0].Accuracy,6:P1} {totals[1].Accuracy,6:P1} {totals[2].Accuracy,7:P1}");
        report.AppendLine();
        report.AppendLine($"{"certainty",-10} {"offered",7} {"min",6} {"right",6}   (offered stretches; min: what they add where the truth is known)");
        for (var b = 0; b < CertaintyBands.Length; b++)
        {
            var upper = b + 1 < CertaintyBands.Length ? $"{CertaintyBands[b + 1]:P0}" : "100%";
            report.AppendLine($"{$"{CertaintyBands[b]:P0}–{upper}",-10} {offered[b].Stretches,7} {offered[b].Adds.Seconds / 60,6:F0} {offered[b].Adds.Accuracy,6:P0}");
        }
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    /// <summary>Where the certainty bands of offered stretches start.</summary>
    private static readonly double[] CertaintyBands = [0, 0.3, 0.4, 0.51, 0.6, 0.7, 0.8, 0.9, 0.95, 0.99];

    /// <summary>
    /// What the developer says each active capture is: its range by hand (none if the range has none), else on a day
    /// labelled frame by frame its link or rule (none without one); null where unknown. Unproductive time is no project.
    /// </summary>
    private static Label?[] Truth(IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans, bool fullDay)
    {
        var assignments = TrainingSet.Assignments(captures, spans);
        var truth = new Label?[captures.Count];
        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            if (!capture.IsActive || capture.PendingDelete)
                continue;
            if (assignments[i].ByHand)
                truth[i] = new Label(capture.Productive ? assignments[i].ProjectId : null);
            else if (fullDay)
                truth[i] = new Label(capture.Productive ? capture.LinkedProjectId : null);
        }
        return truth;
    }

    private static Dictionary<long, IReadOnlyCollection<string>>? ReadScreenWords(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var words = new Dictionary<long, IReadOnlyCollection<string>>();
        foreach (var line in File.ReadLines(path))
        {
            using var json = JsonDocument.Parse(line);
            words[json.RootElement.GetProperty("id").GetInt64()] = ScreenWords.Of(json.RootElement.GetProperty("text").GetString() ?? "");
        }
        return words;
    }

    /// <summary>A project, or none: what a capture is, told apart from "not known".</summary>
    private readonly record struct Label(long? ProjectId);

    private readonly record struct Score(double Right, double Seconds)
    {
        public double Accuracy => Seconds > 0 ? Right / Seconds : double.NaN;

        public Score Add(Score other) => new(Right + other.Right, Seconds + other.Seconds);

        public static Score Of(Label?[] truth, long?[] guess, double[] seconds, Func<int, bool> counts)
        {
            var score = new Score();
            for (var i = 0; i < truth.Length; i++)
                if (truth[i] is { } label && counts(i))
                    score = score.Add(new Score(label.ProjectId == guess[i] ? seconds[i] : 0, seconds[i]));
            return score;
        }
    }
}
