using Microsoft.Data.Sqlite;
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
    /// or the capture is gone (deleted while it was being read).
    /// </summary>
    /// <returns>Whether they were kept.</returns>
    public bool Set(long captureId, IReadOnlyCollection<string> words)
    {
        if (words.Count == 0)
            return false;
        // Checked and written in one (immediate) transaction: reading turned off in between would prune before the
        // words arrived, and they'd stay.
        using var transaction = db.BeginTransaction();
        if (Forbidden($"c.id = {captureId}").Count > 0)
            return false;
        using var command = db.Command("""
            INSERT OR REPLACE INTO screen_words (capture_id, words)
            SELECT $id, $words WHERE EXISTS (SELECT 1 FROM captures WHERE id = $id);
            """);
        command.Parameters.AddWithValue("$id", captureId);
        command.Parameters.AddWithValue("$words", string.Join(' ', words));
        var kept = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return kept;
    }

    /// <summary>Gives a capture the words of another, for a capture whose screen didn't change; the same rules apply.</summary>
    /// <returns>Whether there were words to copy and they were kept.</returns>
    public bool Copy(long fromCaptureId, long toCaptureId) =>
        Get(fromCaptureId) is { } words && Set(toCaptureId, words);

    /// <summary>A capture's words, or null when none were read (or kept).</summary>
    public string[]? Get(long captureId)
    {
        using var command = db.Command("SELECT words FROM screen_words WHERE capture_id = $id;");
        command.Parameters.AddWithValue("$id", captureId);
        return command.ExecuteScalar() is string words ? words.Split(' ', StringSplitOptions.RemoveEmptyEntries) : null;
    }

    /// <summary>The words of every capture from start up to (not including) end that has some, by capture id.</summary>
    public Dictionary<long, string[]> Get(DateTimeOffset start, DateTimeOffset end)
    {
        using var command = db.Command("""
            SELECT w.capture_id, w.words FROM screen_words w JOIN captures c ON c.id = w.capture_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end;
            """);
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var words = new Dictionary<long, string[]>();
        while (reader.Read())
            words[reader.GetInt64(0)] = reader.GetString(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words;
    }

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
    public bool ReadsScreenFor(long projectId)
    {
        using var command = db.Command("SELECT read_screen FROM projects WHERE id = $id;");
        command.Parameters.AddWithValue("$id", projectId);
        return command.ExecuteScalar() is long read && read != 0;
    }

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
        using (var command = db.Command($"""
            SELECT c.id, c.ts_ms, a.exe_name, c.window_title, coalesce(a.read_screen, 1), coalesce(s.read_screen, 1),
                   a.project_id, s.project_id
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id LEFT JOIN sites s ON s.id = c.site_id
            WHERE {where}
            ORDER BY c.ts_ms;
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (reader.GetInt64(4) == 0 || reader.GetInt64(5) == 0)
                {
                    forbidden.Add(id);
                    continue;
                }
                if (unread.Count == 0)
                    continue;
                // The project its links and title rules give it; a range set by hand may still say otherwise (below).
                var linked = rules.ProjectOf(Text(reader, 2), Text(reader, 3)) ?? Id(reader, 7) ?? Id(reader, 6);
                candidates.Add((id, reader.GetInt64(1), linked));
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

        static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        static long? Id(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
    }
}
