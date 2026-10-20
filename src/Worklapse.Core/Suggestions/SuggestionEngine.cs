using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;

namespace Worklapse.Core.Suggestions;

/// <summary>
/// Works out a day's suggestions from what history teaches and keeps them (<see cref="SuggestionStore"/>). The classifier
/// is trained on every day with ranges set by hand and kept until something it learns from changes: the ranges, links,
/// title rules, projects and opt-outs (and, coarsely, the words read off screens). Training reads those days whole, so
/// call it off the UI thread. Not thread-safe.
/// </summary>
public sealed class SuggestionEngine
{
    /// <summary>Screen words are counted in steps of this many when telling whether to train again.</summary>
    private const int WordsStep = 1000;

    private (string Stamp, ProjectClassifier Classifier)? _trained;

    /// <summary>
    /// Works <paramref name="day"/>'s suggestions out again and returns them all, open and decided; none when suggestions
    /// are turned off.
    /// </summary>
    public List<StoredSuggestion> Refresh(HistoryStore history, DateOnly day, WorklapseSettings settings)
    {
        var store = new SuggestionStore(history.Database);
        var (start, end) = history.DayRange(day);
        if (!settings.SuggestProjects)
            return [];

        var optOuts = SuggestionOptOuts.Load(history.Database);
        var classifier = Classifier(history, optOuts, settings.MaxCaptureLength);
        var projects = new ProjectStore(history.Database);
        var words = new ScreenWordStore(history.Database).Get(start, end);
        var computed = SuggestionBuilder.ForDay(history.LoadDay(day).Captures, projects.GetSpans(start, end), classifier,
            new SuggestionLimits(TimeSpan.FromMinutes(settings.GlanceMinutes), TimeSpan.FromMinutes(settings.SplitMinutes)),
            settings.MaxCaptureLength, optOuts, id => words.GetValueOrDefault(id));
        store.Replace(start, end, computed);
        return store.Get(start, end);
    }

    /// <summary>The classifier for history as it is now, trained again only when what it learns from has changed.</summary>
    public ProjectClassifier Classifier(HistoryStore history, SuggestionOptOuts optOuts, TimeSpan maxCaptureLength)
    {
        var stamp = Stamp(history);
        if (_trained is { } trained && trained.Stamp == stamp)
            return trained.Classifier;

        var projects = new ProjectStore(history.Database);
        var screenWords = new ScreenWordStore(history.Database);
        var examples = new List<TrainingExample>(TrainingSet.Starters(projects.GetProjects(), projects.GetTitleRules(),
            history.GetPrograms(maxCaptureLength), optOuts));
        foreach (var day in LabelledDays(history))
        {
            var (start, end) = history.DayRange(day);
            var words = screenWords.Get(start, end);
            examples.AddRange(TrainingSet.FromDay(history.LoadDay(day).Captures, projects.GetSpans(start, end), maxCaptureLength,
                optOuts, id => words.GetValueOrDefault(id)));
        }
        var classifier = ProjectClassifier.Train(examples);
        _trained = (stamp, classifier);
        return classifier;
    }

    /// <summary>The local days with at least one range set by hand.</summary>
    private static List<DateOnly> LabelledDays(HistoryStore history)
    {
        var projects = new ProjectStore(history.Database);
        var days = new SortedSet<DateOnly>();
        foreach (var span in projects.GetSpans(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1)))
        {
            days.Add(LocalDay.Of(span.Start.LocalDateTime, history.DayStart));
            days.Add(LocalDay.Of(span.End.AddTicks(-1).LocalDateTime, history.DayStart));
        }
        return [.. days];
    }

    /// <summary>A summary of everything training learns from; equal summaries mean the same classifier.</summary>
    private static string Stamp(HistoryStore history)
    {
        using var command = history.Database.Command($"""
            SELECT (SELECT count(*) || ':' || coalesce(sum(start_ms % 1000003 + end_ms % 1000033 + coalesce(project_id, 0)), 0) FROM project_spans)
                || '|' || (SELECT coalesce(group_concat(id || ',' || project_id || ',' || text || ',' || coalesce(app, ''), ';'), '') FROM title_rules)
                || '|' || (SELECT coalesce(group_concat(exe_name || ',' || coalesce(project_id, '') || ',' || learn || ',' || productive, ';'), '') FROM apps
                           WHERE project_id IS NOT NULL OR learn = 0 OR productive = 1)
                || '|' || (SELECT coalesce(group_concat(domain || ',' || coalesce(project_id, '') || ',' || learn || ',' || productive, ';'), '') FROM sites
                           WHERE project_id IS NOT NULL OR learn = 0 OR productive = 1)
                || '|' || (SELECT coalesce(group_concat(id || ',' || name || ',' || archived || ',' || suggest, ';'), '') FROM projects)
                || '|' || (SELECT count(*) / {WordsStep} FROM screen_words);
            """);
        return (string)command.ExecuteScalar()!;
    }
}
