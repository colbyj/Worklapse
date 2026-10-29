using Dapper;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Suggestions;

/// <summary>
/// The words read off captures' screens (<see cref="ScreenWords"/>), kept only where reading is allowed: not for an app or
/// site with reading off, nor for time whose project has reading off (by a range set by hand, a title rule or a link, in
/// that order, as <see cref="ProjectTimeline"/> decides). Words go with their capture when it's deleted.
/// Uses the connection it's given, from one thread at a time.
/// </summary>
public sealed class ScreenWordStore(WorklapseDatabase db)
{
    /// <summary>How many captures one delete names at most.</summary>
    private const int DeleteBatch = 500;

    /// <summary>
    /// Keeps the words read off a capture's screen, replacing any it had, unless reading isn't allowed for it (any more)
    /// or the capture is gone (deleted while it was being read). No words is kept too, so the capture counts as read and
    /// isn't read again.
    /// </summary>
    /// <returns>Whether they were kept.</returns>
    public bool Set(long captureId, IReadOnlyCollection<string> words)
    {
        // Checked and written in one (immediate) transaction: reading turned off in between would prune before the
        // words arrived, and they'd stay.
        using var transaction = db.BeginTransaction();
        if (Forbidden($"c.id = {captureId}").Count > 0)
            return false;
        var kept = db.Connection.Execute("""
            INSERT OR REPLACE INTO screen_words (capture_id, words)
            SELECT $id, $words WHERE EXISTS (SELECT 1 FROM captures WHERE id = $id);
            """, new { id = captureId, words = string.Join(' ', words) }) > 0;
        transaction.Commit();
        return kept;
    }

    /// <summary>Gives a capture the words of another, for a capture whose screen didn't change; the same rules apply.</summary>
    /// <returns>Whether the other was read (words or none) and they were kept.</returns>
    public bool Copy(long fromCaptureId, long toCaptureId) =>
        Get(fromCaptureId) is { } words && Set(toCaptureId, words);

    /// <summary>A capture's words (empty when its screen showed none), or null when it wasn't read (or they weren't kept).</summary>
    public string[]? Get(long captureId) =>
        db.Connection.ExecuteScalar<string?>("SELECT words FROM screen_words WHERE capture_id = $id;", new { id = captureId })
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The words of every capture from start up to (not including) end that was read, by capture id.</summary>
    public Dictionary<long, string[]> Get(DateTimeOffset start, DateTimeOffset end) =>
        db.Connection.Query<(long CaptureId, string Words)>("""
            SELECT w.capture_id, w.words FROM screen_words w JOIN captures c ON c.id = w.capture_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end;
            """,
            new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() })
        .ToDictionary(r => r.CaptureId, r => r.Words.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Deletes the words of every capture they're no longer allowed for: after reading was turned off for an app, site or
    /// project, or time was set to a project with reading off, by hand, a title rule or a link. The stores that make those
    /// changes call it (<see cref="ProjectStore.SetSpan"/> and the others), so words never outlive their permission.
    /// </summary>
    /// <returns>How many captures lost their words.</returns>
    public int Prune() => Delete(Forbidden("c.id IN (SELECT capture_id FROM screen_words)"));

    /// <summary>As <see cref="Prune()"/>, for the captures from start up to (not including) end only.</summary>
    public int Prune(DateTimeOffset start, DateTimeOffset end) => Delete(Forbidden(
        $"c.id IN (SELECT capture_id FROM screen_words) AND c.ts_ms >= {start.ToUnixTimeMilliseconds()} AND c.ts_ms < {end.ToUnixTimeMilliseconds()}"));

    /// <summary>Whether the screen is read during a project's time; false for a project that doesn't exist.</summary>
    public bool ReadsScreenFor(long projectId) =>
        db.Connection.ExecuteScalar<bool>("SELECT read_screen FROM projects WHERE id = $id;", new { id = projectId });

    private int Delete(List<long> captureIds)
    {
        for (var i = 0; i < captureIds.Count; i += DeleteBatch)
            db.Execute($"DELETE FROM screen_words WHERE capture_id IN ({string.Join(",", captureIds.Skip(i).Take(DeleteBatch))});");
        return captureIds.Count;
    }

    /// <summary>Whether the screen may be read for a capture, before reading it (the same rules as <see cref="Set"/>).</summary>
    public bool MayRead(long captureId) => Forbidden($"c.id = {captureId}").Count == 0;

    /// <summary>The captures matching <paramref name="where"/> (on captures c) whose screens may not be read.</summary>
    private List<long> Forbidden(string where)
    {
        var projects = new ProjectStore(db);
        var unread = projects.GetProjects().Where(p => !p.ReadScreen).Select(p => p.Id).ToHashSet();
        var rules = new TitleRuleSet(unread.Count > 0 ? projects.GetTitleRules() : []);

        var forbidden = new List<long>();
        var candidates = new List<(long Id, long Ms, long? Project)>();
        // Not buffered: a prune goes over every capture with words.
        foreach (var row in db.Connection.Query<ReadingRow>($"""
            SELECT c.id, c.ts_ms, a.exe_name, c.window_title, coalesce(a.read_screen, 1) AS app_reads, coalesce(s.read_screen, 1) AS site_reads,
                   a.project_id AS app_project, s.project_id AS site_project
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id LEFT JOIN sites s ON s.id = c.site_id
            WHERE {where}
            ORDER BY c.ts_ms;
            """, buffered: false))
        {
            if (row.AppReads == 0 || row.SiteReads == 0)
            {
                forbidden.Add(row.Id);
                continue;
            }
            if (unread.Count == 0)
                continue;
            // The project its links and title rules give it; a range set by hand may still say otherwise (below).
            var linked = rules.ProjectOf(row.ExeName, row.WindowTitle) ?? row.SiteProject ?? row.AppProject;
            candidates.Add((row.Id, row.TsMs, linked));
        }

        if (candidates.Count > 0)
        {
            var spans = projects.GetSpans(DateTimeOffset.FromUnixTimeMilliseconds(candidates[0].Ms),
                DateTimeOffset.FromUnixTimeMilliseconds(candidates[^1].Ms + 1));
            var s = 0;
            foreach (var (id, ms, linked) in candidates)
            {
                while (s < spans.Count && spans[s].End.ToUnixTimeMilliseconds() <= ms)
                    s++;
                var byHand = s < spans.Count && spans[s].Start.ToUnixTimeMilliseconds() <= ms;
                if ((byHand ? spans[s].ProjectId : linked) is { } project && unread.Contains(project))
                    forbidden.Add(id);
            }
        }
        return forbidden;
    }

    /// <summary>A capture with what decides whether its screen may be read, as <see cref="Forbidden"/> asks for it.</summary>
    private sealed record ReadingRow
    {
        public long Id { get; init; }
        public long TsMs { get; init; }
        public string? ExeName { get; init; }
        public string? WindowTitle { get; init; }
        public long AppReads { get; init; }
        public long SiteReads { get; init; }
        public long? AppProject { get; init; }
        public long? SiteProject { get; init; }
    }
}
