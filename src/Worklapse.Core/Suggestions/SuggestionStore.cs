using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Suggestions;

public enum SuggestionState
{
    Open = 0,
    Accepted = 1,
    Rejected = 2,

    /// <summary>Open, with its project picked by the developer: kept when the day is worked out again, as decided ones are.</summary>
    Chosen = 3,
}

/// <summary>A break or switch inside a stored suggestion, with the developer's decision on it.</summary>
/// <param name="Decided">A break: counted toward the block. A switch: kept in the block rather than split out.</param>
public sealed record StoredPart(DateTimeOffset Start, DateTimeOffset End, SuggestionPartKind Kind, long? ProjectId, bool Decided);

/// <summary>A suggested block as stored, with what became of it.</summary>
public sealed record StoredSuggestion(long Id, DateTimeOffset Start, DateTimeOffset End, long ProjectId, double Confidence,
    IReadOnlyList<string> Evidence, SuggestionState State, IReadOnlyList<StoredPart> Parts)
{
    /// <summary>The classifier wasn't sure of its project; a project the developer chose is sure.</summary>
    public bool IsUnsure => State != SuggestionState.Chosen && Confidence < Suggestion.UnsureBelow;

    /// <summary>Still to be accepted or rejected.</summary>
    public bool IsOpen => State is SuggestionState.Open or SuggestionState.Chosen;
}

/// <summary>
/// The suggested blocks of each day and the developer's decisions on them. Open suggestions are replaced whenever a day is
/// worked out again (keeping the decisions on breaks and switches that are still there); accepted and rejected ones stay,
/// so a rejected block isn't offered again and an accepted one isn't offered twice. Accepting writes ranges by hand, so
/// everything else (the Timesheet, the Overview) sees ordinary project time. Uses the connection it's given, from one
/// thread at a time.
/// </summary>
public sealed class SuggestionStore(WorklapseDatabase db)
{
    /// <summary>
    /// A new suggestion counts as one already decided when it has the same project and they overlap by at least this much
    /// of the new one (of the shorter, a small rejected block would hide a much larger one around it); a chosen block, of
    /// the shorter.
    /// </summary>
    public const double SameBlockOverlap = 0.5;

    /// <summary>
    /// The suggestions starting from start up to (not including) end, in time order: open ones and those decided. A day's
    /// are those starting in it, as <see cref="Replace"/> keeps them.
    /// </summary>
    public List<StoredSuggestion> Get(DateTimeOffset start, DateTimeOffset end)
    {
        var parts = new Dictionary<long, List<StoredPart>>();
        using (var command = db.Command("""
            SELECT p.suggestion_id, p.start_ms, p.end_ms, p.kind, p.project_id, p.decided
            FROM suggestion_parts p JOIN suggestions s ON s.id = p.suggestion_id
            WHERE s.start_ms >= $start AND s.start_ms < $end
            ORDER BY p.suggestion_id, p.start_ms;
            """))
        {
            Bind(command, start, end);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!parts.TryGetValue(reader.GetInt64(0), out var list))
                    parts[reader.GetInt64(0)] = list = [];
                list.Add(new StoredPart(Time(reader.GetInt64(1)), Time(reader.GetInt64(2)), (SuggestionPartKind)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5) != 0));
            }
        }

        using var suggestions = db.Command("""
            SELECT id, start_ms, end_ms, project_id, confidence, evidence, state FROM suggestions
            WHERE start_ms >= $start AND start_ms < $end ORDER BY start_ms, id;
            """);
        Bind(suggestions, start, end);
        using var rows = suggestions.ExecuteReader();
        var result = new List<StoredSuggestion>();
        while (rows.Read())
        {
            var id = rows.GetInt64(0);
            result.Add(new StoredSuggestion(id, Time(rows.GetInt64(1)), Time(rows.GetInt64(2)), rows.GetInt64(3), rows.GetDouble(4),
                rows.GetString(5).Split(' ', StringSplitOptions.RemoveEmptyEntries), (SuggestionState)rows.GetInt32(6),
                parts.TryGetValue(id, out var list) ? list : []));
        }
        return result;
    }

    /// <summary>How many open suggestions start on each local day of <paramref name="days"/>, for the Overview.</summary>
    public Dictionary<DateOnly, int> CountOpen(IEnumerable<(DateOnly Day, DateTimeOffset Start, DateTimeOffset End)> days)
    {
        var counts = new Dictionary<DateOnly, int>();
        using var command = db.Command("SELECT count(*) FROM suggestions WHERE state IN (0, 3) AND start_ms >= $start AND start_ms < $end;");
        var start = command.Parameters.Add("$start", Microsoft.Data.Sqlite.SqliteType.Integer);
        var end = command.Parameters.Add("$end", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var (day, from, to) in days)
        {
            start.Value = from.ToUnixTimeMilliseconds();
            end.Value = to.ToUnixTimeMilliseconds();
            counts[day] = Convert.ToInt32(command.ExecuteScalar());
        }
        return counts;
    }

    /// <summary>
    /// Makes <paramref name="computed"/> the open suggestions starting from start up to end: the open ones there before go
    /// (not those whose project was chosen), except that one computed again unchanged (the same time and project) keeps its
    /// id, so a click on it made meanwhile still finds it, and a break or switch overlapping one decided before keeps that
    /// decision; a computed block matching one decided or chosen already (the same project, overlapping by
    /// <see cref="SameBlockOverlap"/>) is left out.
    /// </summary>
    public void Replace(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<Suggestion> computed)
    {
        using var transaction = db.BeginTransaction();
        var before = Get(start, end);
        var decided = before.Where(s => s.State != SuggestionState.Open).ToList(); // accepted, rejected or chosen
        var open = before.Where(s => s.State == SuggestionState.Open).ToList();
        var decisions = open.SelectMany(s => s.Parts).Where(p => p.Decided).ToList();
        var kept = new HashSet<long>();

        foreach (var suggestion in computed)
        {
            if (decided.Any(d => SameBlock(d, suggestion)))
                continue;
            long id;
            if (open.FirstOrDefault(o => !kept.Contains(o.Id) && o.Start == suggestion.Start && o.End == suggestion.End
                    && o.ProjectId == suggestion.ProjectId) is { } same)
            {
                id = same.Id;
                kept.Add(id);
                using var update = db.Command("UPDATE suggestions SET confidence = $confidence, evidence = $evidence WHERE id = $id;");
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$confidence", suggestion.Confidence);
                update.Parameters.AddWithValue("$evidence", string.Join(' ', suggestion.Evidence));
                update.ExecuteNonQuery();
                using var deleteParts = db.Command("DELETE FROM suggestion_parts WHERE suggestion_id = $id;");
                deleteParts.Parameters.AddWithValue("$id", id);
                deleteParts.ExecuteNonQuery();
            }
            else
            {
                using var insert = db.Command("""
                    INSERT INTO suggestions (start_ms, end_ms, project_id, confidence, evidence, state)
                    VALUES ($start, $end, $project, $confidence, $evidence, 0) RETURNING id;
                    """);
                Bind(insert, suggestion.Start, suggestion.End);
                insert.Parameters.AddWithValue("$project", suggestion.ProjectId);
                insert.Parameters.AddWithValue("$confidence", suggestion.Confidence);
                insert.Parameters.AddWithValue("$evidence", string.Join(' ', suggestion.Evidence));
                id = (long)insert.ExecuteScalar()!;
            }
            foreach (var part in suggestion.Parts)
            {
                using var insert = db.Command("""
                    INSERT INTO suggestion_parts (suggestion_id, start_ms, end_ms, kind, project_id, decided)
                    VALUES ($id, $start, $end, $kind, $project, $decided);
                    """);
                insert.Parameters.AddWithValue("$id", id);
                Bind(insert, part.Start, part.End);
                insert.Parameters.AddWithValue("$kind", (int)part.Kind);
                insert.Parameters.AddWithValue("$project", (object?)part.ProjectId ?? DBNull.Value);
                // Matched by overlap, not exact ends: a capture arriving, or an edge moving a few seconds, keeps the decision.
                var wasDecided = decisions.Any(d => d.Kind == part.Kind && d.ProjectId == part.ProjectId && d.Start < part.End && part.Start < d.End);
                insert.Parameters.AddWithValue("$decided", wasDecided ? 1 : 0);
                insert.ExecuteNonQuery();
            }
        }

        foreach (var gone in open.Where(o => !kept.Contains(o.Id)))
        {
            using var delete = db.Command("DELETE FROM suggestions WHERE id = $id;");
            delete.Parameters.AddWithValue("$id", gone.Id);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Records the decision on a break (counted) or a switch (kept in the block) of an open suggestion.</summary>
    public void SetPart(long suggestionId, DateTimeOffset partStart, bool decided)
    {
        using var command = db.Command("UPDATE suggestion_parts SET decided = $decided WHERE suggestion_id = $id AND start_ms = $start;");
        command.Parameters.AddWithValue("$id", suggestionId);
        command.Parameters.AddWithValue("$start", partStart.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$decided", decided ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Changes an open suggestion's project before it's accepted. That's a decision: the suggestion is kept as it is
    /// (<see cref="SuggestionState.Chosen"/>) when the day is worked out again.
    /// </summary>
    public void SetProject(long suggestionId, long projectId)
    {
        using var command = db.Command("UPDATE suggestions SET project_id = $project, state = 3 WHERE id = $id AND state IN (0, 3);");
        command.Parameters.AddWithValue("$id", suggestionId);
        command.Parameters.AddWithValue("$project", projectId);
        command.ExecuteNonQuery();
    }

    /// <summary>Rejects a suggestion: it stays, so the same block isn't suggested again.</summary>
    public void Reject(long suggestionId) => SetState(suggestionId, SuggestionState.Rejected);

    /// <summary>
    /// Accepts a suggestion: its block becomes ranges set by hand. Time that already has a project of its own (by hand, a
    /// link or a title rule), or was set to no project by hand, is left as it is; breaks not counted are left out; switches
    /// not kept are set to their own project.
    /// </summary>
    /// <param name="captures">The captures of the suggestion's day, as <see cref="HistoryStore.LoadDay"/> reads them.</param>
    /// <param name="spans">The ranges set by hand over that day, in time order.</param>
    public void Accept(StoredSuggestion suggestion, IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        TimeSpan maxCaptureLength)
    {
        var projects = new ProjectStore(db);
        using var transaction = db.BeginTransaction();

        // Where the block's time already belongs elsewhere (another project, or none by hand): kept out of every range written.
        var keep = Elsewhere(captures, spans, maxCaptureLength, suggestion.ProjectId);
        // A break not counted stays out of the block; so does a switch not kept in it, which goes to its own project.
        var left = Subtract(Subtract([(suggestion.Start, suggestion.End)], keep),
            suggestion.Parts.Where(p => !p.Decided).Select(p => (p.Start, p.End)));
        foreach (var (start, end) in left)
            projects.SetSpanInTransaction(start, end, suggestion.ProjectId);

        foreach (var part in suggestion.Parts.Where(p => p is { Kind: SuggestionPartKind.Switch, Decided: false, ProjectId: not null }))
            foreach (var (start, end) in Subtract([(part.Start, part.End)], keep))
                projects.SetSpanInTransaction(start, end, part.ProjectId);

        SetState(suggestion.Id, SuggestionState.Accepted);
        transaction.Commit();
    }

    /// <summary>
    /// Accepts one piece of a suggestion (a stretch drawn on its own) as <paramref name="projectId"/>, leaving the rest of it
    /// open: start..end becomes ranges set by hand, without the time that already has a project of its own or was set to
    /// none by hand.
    /// </summary>
    /// <param name="captures">The captures of the piece's day, as <see cref="HistoryStore.LoadDay"/> reads them.</param>
    /// <param name="spans">The ranges set by hand over that day, in time order.</param>
    public void AcceptPiece(DateTimeOffset start, DateTimeOffset end, long projectId, IReadOnlyList<CaptureRecord> captures,
        IReadOnlyList<ProjectSpan> spans, TimeSpan maxCaptureLength)
    {
        var projects = new ProjectStore(db);
        using var transaction = db.BeginTransaction();
        foreach (var (from, to) in Subtract([(start, end)], Elsewhere(captures, spans, maxCaptureLength, projectId)))
            projects.SetSpanInTransaction(from, to, projectId);
        transaction.Commit();
    }

    /// <summary>The day's time that belongs to another project than <paramref name="projectId"/>, or to none by hand.</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Elsewhere(IReadOnlyList<CaptureRecord> captures,
        IReadOnlyList<ProjectSpan> spans, TimeSpan maxCaptureLength, long projectId) =>
        Assigned(captures, spans, maxCaptureLength).Where(a => a.ProjectId != projectId).Select(a => (a.Start, a.End)).ToList();

    /// <summary>
    /// The stretches of the day's time a range by hand, a link or a title rule decides, with the project (null: none, by
    /// hand), each capture lasting until the next one at most <paramref name="maxCaptureLength"/>.
    /// </summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End, long? ProjectId)> Assigned(
        IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans, TimeSpan maxCaptureLength)
    {
        var assignments = TrainingSet.Assignments(captures, spans);
        var lengths = CaptureLengths.Spans(captures, maxCaptureLength);
        var stretches = new List<(DateTimeOffset, DateTimeOffset, long?)>();
        for (var i = 0; i < captures.Count; i++)
            if (assignments[i].IsAssigned)
                stretches.Add((captures[i].Time, captures[i].Time + lengths[i], assignments[i].ProjectId));
        // Ranges by hand cover time without captures too.
        foreach (var span in spans)
            stretches.Add((span.Start, span.End, span.ProjectId));
        return stretches;
    }

    /// <summary><paramref name="ranges"/> without <paramref name="holes"/>, in time order, empty pieces dropped.</summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Subtract(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> holes)
    {
        var sortedHoles = holes.Where(h => h.End > h.Start).OrderBy(h => h.Start).ToList();
        var result = new List<(DateTimeOffset, DateTimeOffset)>();
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            var from = start;
            foreach (var hole in sortedHoles)
            {
                if (hole.End <= from || hole.Start >= end)
                    continue;
                if (hole.Start > from)
                    result.Add((from, hole.Start));
                if (hole.End > from)
                    from = hole.End;
                if (from >= end)
                    break;
            }
            if (from < end)
                result.Add((from, end));
        }
        return result;
    }

    /// <summary>
    /// Whether a computed block is one already decided: the same project and overlapping enough. A block whose project the
    /// developer chose stands for its time whatever project is computed for it now.
    /// </summary>
    private static bool SameBlock(StoredSuggestion decided, Suggestion computed)
    {
        if (decided.ProjectId != computed.ProjectId && decided.State != SuggestionState.Chosen)
            return false;
        var overlap = (decided.End < computed.End ? decided.End : computed.End) - (decided.Start > computed.Start ? decided.Start : computed.Start);
        var (a, b) = (decided.End - decided.Start, computed.End - computed.Start);
        // Of the new block: one mostly inside a decided block is that block again, but one reaching well past a small
        // decided block is new time. A chosen block is still open, so nothing may be offered over it.
        var basis = decided.State == SuggestionState.Chosen && a < b ? a : b;
        return overlap > TimeSpan.Zero && overlap >= basis * SameBlockOverlap;
    }

    private void SetState(long suggestionId, SuggestionState state)
    {
        using var command = db.Command("UPDATE suggestions SET state = $state WHERE id = $id;");
        command.Parameters.AddWithValue("$id", suggestionId);
        command.Parameters.AddWithValue("$state", (int)state);
        command.ExecuteNonQuery();
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand command, DateTimeOffset start, DateTimeOffset end)
    {
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
    }

    private static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
