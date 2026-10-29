using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Suggestions;

/// <summary>A day's suggestions worked out again and not stored yet (<see cref="SuggestionEngine.Store"/>).</summary>
/// <param name="Stamp">What they were worked out from, so storing them saves working the day out again.</param>
public sealed record WorkedOutDay(DateOnly Day, DateTimeOffset Start, DateTimeOffset End, DaySuggestions Suggestions,
    string Stamp);

/// <summary>
/// Works out a day's suggestions from what history teaches and keeps them (<see cref="SuggestionStore"/>). The classifier
/// is trained on every day with ranges set by hand and kept until something it learns from changes: the ranges, links,
/// title rules, projects and opt-outs (and, coarsely, the words read off screens). A project is never suggested on a day
/// outside its dates (<see cref="Project.RunsOn"/>). A day is worked out again only when
/// the classifier, the day's captures or words, or the settings it uses have changed since; otherwise its suggestions
/// are read as stored. One engine serves the whole app (Day review and the Overview), so the classifier is trained and
/// held once; it takes one call at a time. Training reads the labelled days whole, so call it off the UI thread.
/// </summary>
public sealed class SuggestionEngine
{
    /// <summary>Screen words are counted in steps of this many when telling whether to train again.</summary>
    private const int WordsStep = 1000;

    private readonly SemaphoreSlim _turn = new(1, 1);
    private (string Stamp, ProjectClassifier Classifier)? _trained;

    /// <summary>
    /// What each day's stored suggestions were worked out from (<see cref="DayStamp"/>), by data folder and day. Locked:
    /// <see cref="Store"/> is called outside the turn.
    /// </summary>
    private readonly Dictionary<(string Folder, DateOnly Day), string> _workedOut = [];

    /// <summary>
    /// Works <paramref name="day"/>'s suggestions out again (when anything they rest on changed) and returns them all,
    /// open and decided; none when suggestions are turned off.
    /// </summary>
    public StoredDay Refresh(HistoryStore history, DateOnly day, WorklapseSettings settings)
    {
        _turn.Wait();
        try
        {
            return RefreshInTurn(history, day, settings);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// As <see cref="Refresh"/>, on a background thread with a connection of its own, after the calls already waiting;
    /// null when, by its turn, <paramref name="stillWanted"/> says the result isn't needed any more.
    /// </summary>
    public Task<StoredDay?> RefreshAsync(string dataFolder, TimeOnly dayStart, DateOnly day,
        WorklapseSettings settings, Func<bool> stillWanted) =>
        InTurnAsync(dataFolder, dayStart, stillWanted, history => RefreshInTurn(history, day, settings));

    /// <summary>
    /// Works <paramref name="day"/>'s suggestions out again as <see cref="RefreshAsync"/> does, without storing them, so
    /// they can be shown later (<see cref="Store"/>); null when nothing they rest on changed since they were stored, when
    /// suggestions are turned off, or when by its turn <paramref name="stillWanted"/> says they aren't needed any more.
    /// </summary>
    public Task<WorkedOutDay?> WorkOutAsync(string dataFolder, TimeOnly dayStart, DateOnly day, WorklapseSettings settings,
        Func<bool> stillWanted) =>
        InTurnAsync(dataFolder, dayStart, stillWanted, history => settings.SuggestProjects ? WorkOutInTurn(history, day, settings) : null);

    /// <summary>Stores a day's suggestions as worked out (<see cref="SuggestionStore.Replace"/>), keeping its decisions.</summary>
    public void Store(HistoryStore history, WorkedOutDay worked)
    {
        new SuggestionStore(history.Database).Replace(worked.Start, worked.End, worked.Suggestions);
        lock (_workedOut)
            _workedOut[(history.Database.DataFolder, worked.Day)] = worked.Stamp;
    }

    private async Task<T?> InTurnAsync<T>(string dataFolder, TimeOnly dayStart, Func<bool> stillWanted, Func<HistoryStore, T?> work)
        where T : class
    {
        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!stillWanted())
                return null;
            return await Task.Run(() =>
            {
                using var history = new HistoryStore(WorklapseDatabase.Open(dataFolder), dayStart);
                return work(history);
            }).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The classifier for history as it is now, trained again only when what it learns from has changed.</summary>
    public ProjectClassifier Classifier(HistoryStore history, SuggestionOptOuts optOuts, TimeSpan maxCaptureLength)
    {
        _turn.Wait();
        try
        {
            return Trained(history, Stamp(history), optOuts, maxCaptureLength);
        }
        finally
        {
            _turn.Release();
        }
    }

    private StoredDay RefreshInTurn(HistoryStore history, DateOnly day, WorklapseSettings settings)
    {
        if (!settings.SuggestProjects)
            return StoredDay.None;
        if (WorkOutInTurn(history, day, settings) is { } worked)
            Store(history, worked);
        var (start, end) = history.DayRange(day);
        return new SuggestionStore(history.Database).Get(start, end);
    }

    /// <summary>The day's suggestions worked out again; null when nothing they rest on changed since they were stored.</summary>
    private WorkedOutDay? WorkOutInTurn(HistoryStore history, DateOnly day, WorklapseSettings settings)
    {
        var (start, end) = history.DayRange(day);
        var classifierStamp = Stamp(history);
        var dayStamp = DayStamp(history, start, end, settings, classifierStamp);
        lock (_workedOut)
            if (_workedOut.GetValueOrDefault((history.Database.DataFolder, day)) == dayStamp)
                return null;

        var optOuts = SuggestionOptOuts.Load(history.Database);
        var classifier = Trained(history, classifierStamp, optOuts, settings.MaxCaptureLength);
        var projects = new ProjectStore(history.Database);
        var words = new ScreenWordStore(history.Database).Get(start, end);
        var computed = SuggestionBuilder.ForDay(history.LoadDay(day).Captures, projects.GetSpans(start, end), classifier,
            new SuggestionLimits(TimeSpan.FromMinutes(settings.GlanceMinutes), TimeSpan.FromMinutes(settings.SplitMinutes))
            {
                MinimumCertainty = settings.MinimumCertainty / 100.0,
            },
            settings.MaxCaptureLength, optOuts, id => words.GetValueOrDefault(id),
            projects.GetProjects().Where(p => !p.RunsOn(day)).Select(p => p.Id).ToHashSet());
        return new WorkedOutDay(day, start, end, computed, dayStamp);
    }

    private ProjectClassifier Trained(HistoryStore history, string stamp, SuggestionOptOuts optOuts, TimeSpan maxCaptureLength)
    {
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

    /// <summary>
    /// A summary of what a day's suggestions rest on besides the classifier's history: the day's bounds, its captures (new
    /// ones, idle marking, deletions), the words read off them, and the settings used; with the classifier's stamp.
    /// </summary>
    private static string DayStamp(HistoryStore history, DateTimeOffset start, DateTimeOffset end, WorklapseSettings settings,
        string classifierStamp)
    {
        using var command = history.Database.Command("""
            SELECT count(*) || ':' || coalesce(max(c.id), 0) || ':' || coalesce(sum(c.kind), 0) || ':' || coalesce(sum(c.pending_delete), 0)
                || ':' || (SELECT count(*) FROM screen_words w JOIN captures d ON d.id = w.capture_id WHERE d.ts_ms >= $start AND d.ts_ms < $end)
            FROM captures c WHERE c.ts_ms >= $start AND c.ts_ms < $end;
            """);
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        return string.Join('|', start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), (string)command.ExecuteScalar()!,
            settings.GlanceMinutes, settings.SplitMinutes, settings.MinimumCertainty, settings.MaxCaptureLength.Ticks, classifierStamp);
    }

    /// <summary>The local days with at least one range set by hand.</summary>
    public static List<DateOnly> LabelledDays(HistoryStore history)
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
                || '|' || (SELECT coalesce(group_concat(id || ',' || name || ',' || archived || ',' || suggest || ',' || coalesce(starts_on, '') || ',' || coalesce(ends_on, ''), ';'), '') FROM projects)
                || '|' || (SELECT count(*) / {WordsStep} FROM screen_words)
                || '|' || (SELECT count(*) || ':' || coalesce(sum(id % 1000003 * (task_mark + 1)), 0) FROM captures WHERE task_mark IS NOT NULL);
            """);
        return (string)command.ExecuteScalar()!;
    }
}
