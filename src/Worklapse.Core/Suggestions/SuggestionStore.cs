using Worklapse.Core.History;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Suggestions;

public enum SuggestionState
{
    Open = 0,

    /// <summary>A stretch accepted; a break counted toward a project.</summary>
    Accepted = 1,

    /// <summary>A stretch rejected; a break left out.</summary>
    Rejected = 2,

    /// <summary>Open, with its project picked by the developer: kept when the day is worked out again, as decided ones are.</summary>
    Chosen = 3,
}

/// <summary>A suggested stretch as stored, with what became of it.</summary>
public sealed record StoredSuggestion(long Id, DateTimeOffset Start, DateTimeOffset End, long ProjectId, double Confidence,
    DateTimeOffset LeastSure, IReadOnlyList<string> Evidence, SuggestionState State)
{
    /// <summary>The classifier wasn't sure of its project; a project the developer chose is sure.</summary>
    public bool IsUnsure => State != SuggestionState.Chosen && Confidence < Suggestion.UnsureBelow;

    /// <summary>Still to be accepted or rejected.</summary>
    public bool IsOpen => State is SuggestionState.Open or SuggestionState.Chosen;
}

/// <summary>A suggested break as stored, with what became of it.</summary>
/// <param name="CountedTo">The project it was counted toward, once <see cref="SuggestionState.Accepted"/>.</param>
public sealed record StoredBreak(long Id, DateTimeOffset Start, DateTimeOffset End, long Before, long After, long? CountedTo,
    SuggestionState State)
{
    /// <summary>The project on both sides, when it's the same one.</summary>
    public long? Inside => Before == After ? Before : null;

    /// <summary>Still to be counted or left out.</summary>
    public bool IsOpen => State == SuggestionState.Open;
}

/// <summary>A day's suggestions as stored, open and decided, each kind in time order.</summary>
public sealed record StoredDay(IReadOnlyList<StoredSuggestion> Stretches, IReadOnlyList<StoredBreak> Breaks)
{
    public static StoredDay None { get; } = new([], []);

    /// <summary>The stretches and breaks still to decide.</summary>
    public int OpenCount => Stretches.Count(s => s.IsOpen) + Breaks.Count(b => b.IsOpen);
}

/// <summary>
/// The suggested stretches and breaks of each day and the developer's decisions on them. Open ones are replaced whenever
/// a day is worked out again; decided ones stay, so a rejected stretch or a break left out isn't offered again and an
/// accepted one isn't offered twice. Accepting writes ranges by hand, so everything else (the Timesheet, the Overview)
/// sees ordinary project time. Uses the connection it's given, from one thread at a time.
/// </summary>
public sealed class SuggestionStore(WorklapseDatabase db)
{
    /// <summary>
    /// A new suggestion counts as one already decided when they overlap by at least this much of the new one (of the
    /// shorter, a small rejected stretch would hide a much larger one around it); a stretch also needs the same project,
    /// unless the decided one's project was chosen, which is compared by the shorter.
    /// </summary>
    public const double SameOverlap = 0.5;

    /// <summary>
    /// The stretches and breaks starting from start up to (not including) end, in time order: open ones and those decided.
    /// A day's are those starting in it, as <see cref="Replace"/> keeps them.
    /// </summary>
    public StoredDay Get(DateTimeOffset start, DateTimeOffset end)
    {
        var stretches = new List<StoredSuggestion>();
        using (var command = db.Command("""
            SELECT id, start_ms, end_ms, project_id, confidence, least_sure_ms, evidence, state FROM suggestions
            WHERE start_ms >= $start AND start_ms < $end ORDER BY start_ms, id;
            """))
        {
            Bind(command, start, end);
            using var rows = command.ExecuteReader();
            while (rows.Read())
                stretches.Add(new StoredSuggestion(rows.GetInt64(0), Time(rows.GetInt64(1)), Time(rows.GetInt64(2)), rows.GetInt64(3),
                    rows.GetDouble(4), Time(rows.IsDBNull(5) ? rows.GetInt64(1) : rows.GetInt64(5)),
                    rows.GetString(6).Split(' ', StringSplitOptions.RemoveEmptyEntries), (SuggestionState)rows.GetInt32(7)));
        }

        var breaks = new List<StoredBreak>();
        using (var command = db.Command("""
            SELECT id, start_ms, end_ms, before_project, after_project, counted_to, state FROM suggestion_breaks
            WHERE start_ms >= $start AND start_ms < $end ORDER BY start_ms, id;
            """))
        {
            Bind(command, start, end);
            using var rows = command.ExecuteReader();
            while (rows.Read())
                breaks.Add(new StoredBreak(rows.GetInt64(0), Time(rows.GetInt64(1)), Time(rows.GetInt64(2)), rows.GetInt64(3),
                    rows.GetInt64(4), rows.IsDBNull(5) ? null : rows.GetInt64(5), (SuggestionState)rows.GetInt32(6)));
        }
        return new StoredDay(stretches, breaks);
    }

    /// <summary>How many open stretches and breaks start on each local day of <paramref name="days"/>, for the Overview.</summary>
    public Dictionary<DateOnly, int> CountOpen(IEnumerable<(DateOnly Day, DateTimeOffset Start, DateTimeOffset End)> days)
    {
        var counts = new Dictionary<DateOnly, int>();
        using var command = db.Command("""
            SELECT (SELECT count(*) FROM suggestions WHERE state IN (0, 3) AND start_ms >= $start AND start_ms < $end)
                 + (SELECT count(*) FROM suggestion_breaks WHERE state = 0 AND start_ms >= $start AND start_ms < $end);
            """);
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
    /// Makes <paramref name="computed"/> the open stretches and breaks starting from start up to end: the open ones there
    /// before go (not stretches whose project was chosen), except that one computed again unchanged keeps its id, so a
    /// click on it made meanwhile still finds it; a computed one matching one decided already (<see cref="SameOverlap"/>)
    /// is left out.
    /// </summary>
    public void Replace(DateTimeOffset start, DateTimeOffset end, DaySuggestions computed)
    {
        using var transaction = db.BeginTransaction();
        var before = Get(start, end);

        var openStretches = before.Stretches.Where(s => s.State == SuggestionState.Open).ToList();
        var kept = new HashSet<long>();
        foreach (var stretch in OfferedStretches(before, computed))
        {
            if (openStretches.FirstOrDefault(o => !kept.Contains(o.Id) && o.Start == stretch.Start && o.End == stretch.End
                    && o.ProjectId == stretch.ProjectId) is { } same)
            {
                kept.Add(same.Id);
                using var update = db.Command("""
                    UPDATE suggestions SET confidence = $confidence, least_sure_ms = $leastSure, evidence = $evidence WHERE id = $id;
                    """);
                update.Parameters.AddWithValue("$id", same.Id);
                update.Parameters.AddWithValue("$confidence", stretch.Confidence);
                update.Parameters.AddWithValue("$leastSure", stretch.LeastSure.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$evidence", string.Join(' ', stretch.Evidence));
                update.ExecuteNonQuery();
                continue;
            }
            using var insert = db.Command("""
                INSERT INTO suggestions (start_ms, end_ms, project_id, confidence, least_sure_ms, evidence, state)
                VALUES ($start, $end, $project, $confidence, $leastSure, $evidence, 0);
                """);
            Bind(insert, stretch.Start, stretch.End);
            insert.Parameters.AddWithValue("$project", stretch.ProjectId);
            insert.Parameters.AddWithValue("$confidence", stretch.Confidence);
            insert.Parameters.AddWithValue("$leastSure", stretch.LeastSure.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$evidence", string.Join(' ', stretch.Evidence));
            insert.ExecuteNonQuery();
        }
        Delete("suggestions", openStretches.Where(o => !kept.Contains(o.Id)).Select(o => o.Id));

        var openBreaks = before.Breaks.Where(b => b.IsOpen).ToList();
        kept.Clear();
        foreach (var computedBreak in OfferedBreaks(before, computed))
        {
            if (openBreaks.FirstOrDefault(o => !kept.Contains(o.Id) && o.Start == computedBreak.Start && o.End == computedBreak.End
                    && o.Before == computedBreak.Before && o.After == computedBreak.After) is { } same)
            {
                kept.Add(same.Id);
                continue;
            }
            using var insert = db.Command("""
                INSERT INTO suggestion_breaks (start_ms, end_ms, before_project, after_project, state)
                VALUES ($start, $end, $before, $after, 0);
                """);
            Bind(insert, computedBreak.Start, computedBreak.End);
            insert.Parameters.AddWithValue("$before", computedBreak.Before);
            insert.Parameters.AddWithValue("$after", computedBreak.After);
            insert.ExecuteNonQuery();
        }
        Delete("suggestion_breaks", openBreaks.Where(o => !kept.Contains(o.Id)).Select(o => o.Id));
        transaction.Commit();
    }

    /// <summary>
    /// Whether <see cref="Replace"/> would change the open stretches or breaks starting from start up to end: offer another,
    /// take one away, or move one; not when only a stretch's certainty and evidence would change.
    /// </summary>
    public bool Changes(DateTimeOffset start, DateTimeOffset end, DaySuggestions computed)
    {
        var before = Get(start, end);
        var stretches = before.Stretches.Where(s => s.State == SuggestionState.Open).Select(s => (s.Start, s.End, s.ProjectId)).ToList();
        foreach (var stretch in OfferedStretches(before, computed))
            if (!stretches.Remove((stretch.Start, stretch.End, stretch.ProjectId)))
                return true;
        var breaks = before.Breaks.Where(b => b.IsOpen).Select(b => (b.Start, b.End, b.Before, b.After)).ToList();
        foreach (var computedBreak in OfferedBreaks(before, computed))
            if (!breaks.Remove((computedBreak.Start, computedBreak.End, computedBreak.Before, computedBreak.After)))
                return true;
        return stretches.Count > 0 || breaks.Count > 0;
    }

    /// <summary>The computed stretches offered: those not matching one decided or chosen already.</summary>
    private static IEnumerable<Suggestion> OfferedStretches(StoredDay before, DaySuggestions computed)
    {
        var decided = before.Stretches.Where(s => s.State != SuggestionState.Open).ToList(); // accepted, rejected or chosen
        return computed.Stretches.Where(stretch => !decided.Any(d => SameStretch(d, stretch)));
    }

    /// <summary>The computed breaks offered: those not overlapping one decided already by half of their own length.</summary>
    private static IEnumerable<SuggestedBreak> OfferedBreaks(StoredDay before, DaySuggestions computed)
    {
        var decided = before.Breaks.Where(b => !b.IsOpen).ToList();
        return computed.Breaks.Where(b => !decided.Any(d => Overlap(d.Start, d.End, b.Start, b.End) is var overlap
            && overlap > TimeSpan.Zero && overlap >= (b.End - b.Start) * SameOverlap));
    }

    /// <summary>
    /// Changes an open stretch's project before it's accepted. That's a decision: the stretch is kept as it is
    /// (<see cref="SuggestionState.Chosen"/>) when the day is worked out again.
    /// </summary>
    public void SetProject(long suggestionId, long projectId)
    {
        using var command = db.Command("UPDATE suggestions SET project_id = $project, state = 3 WHERE id = $id AND state IN (0, 3);");
        command.Parameters.AddWithValue("$id", suggestionId);
        command.Parameters.AddWithValue("$project", projectId);
        command.ExecuteNonQuery();
    }

    /// <summary>Rejects a stretch: it stays, so the same stretch isn't suggested again.</summary>
    public void Reject(long suggestionId) => SetState("suggestions", suggestionId, SuggestionState.Rejected);

    /// <summary>
    /// Accepts a stretch: its time becomes ranges set by hand. Time that already has another project (by hand, a link or a
    /// title rule), or was set to no project by hand, is left as it is.
    /// </summary>
    /// <param name="captures">The captures of the stretch's day, as <see cref="HistoryStore.LoadDay"/> reads them.</param>
    /// <param name="spans">The ranges set by hand over that day, in time order.</param>
    public void Accept(StoredSuggestion suggestion, IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        TimeSpan maxCaptureLength)
    {
        using var transaction = db.BeginTransaction();
        SetSpans(suggestion.Start, suggestion.End, suggestion.ProjectId, captures, spans, maxCaptureLength);
        SetState("suggestions", suggestion.Id, SuggestionState.Accepted);
        transaction.Commit();
    }

    /// <summary>Counts a break toward <paramref name="projectId"/>: its time becomes a range set by hand, as for a stretch.</summary>
    public void Count(StoredBreak suggestedBreak, long projectId, IReadOnlyList<CaptureRecord> captures, IReadOnlyList<ProjectSpan> spans,
        TimeSpan maxCaptureLength)
    {
        using var transaction = db.BeginTransaction();
        SetSpans(suggestedBreak.Start, suggestedBreak.End, projectId, captures, spans, maxCaptureLength);
        using var command = db.Command("UPDATE suggestion_breaks SET counted_to = $project, state = 1 WHERE id = $id;");
        command.Parameters.AddWithValue("$id", suggestedBreak.Id);
        command.Parameters.AddWithValue("$project", projectId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Leaves a break out of the projects around it: it stays, so it isn't offered again.</summary>
    public void LeaveOut(long breakId) => SetState("suggestion_breaks", breakId, SuggestionState.Rejected);

    /// <summary>
    /// Sets start..end to <paramref name="projectId"/> by hand, without the time that has another project of its own or was
    /// set to none by hand. Inside the caller's transaction.
    /// </summary>
    private void SetSpans(DateTimeOffset start, DateTimeOffset end, long projectId, IReadOnlyList<CaptureRecord> captures,
        IReadOnlyList<ProjectSpan> spans, TimeSpan maxCaptureLength)
    {
        var projects = new ProjectStore(db);
        foreach (var (from, to) in Subtract([(start, end)], Elsewhere(captures, spans, maxCaptureLength, projectId)))
            projects.SetSpanInTransaction(from, to, projectId);
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
    /// Whether a computed stretch is one already decided: the same project and overlapping enough. A stretch whose project
    /// the developer chose stands for its time whatever project is computed for it now.
    /// </summary>
    private static bool SameStretch(StoredSuggestion decided, Suggestion computed)
    {
        if (decided.ProjectId != computed.ProjectId && decided.State != SuggestionState.Chosen)
            return false;
        var overlap = Overlap(decided.Start, decided.End, computed.Start, computed.End);
        var (a, b) = (decided.End - decided.Start, computed.End - computed.Start);
        // Of the new stretch: one mostly inside a decided stretch is that stretch again, but one reaching well past a small
        // decided stretch is new time. A chosen stretch is still open, so nothing may be offered over it.
        var basis = decided.State == SuggestionState.Chosen && a < b ? a : b;
        return overlap > TimeSpan.Zero && overlap >= basis * SameOverlap;
    }

    /// <summary>How long two ranges overlap; zero or less when they don't.</summary>
    private static TimeSpan Overlap(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd) =>
        (aEnd < bEnd ? aEnd : bEnd) - (aStart > bStart ? aStart : bStart);

    /// <param name="table">suggestions or suggestion_breaks.</param>
    private void SetState(string table, long id, SuggestionState state)
    {
        using var command = db.Command($"UPDATE {table} SET state = $state WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$state", (int)state);
        command.ExecuteNonQuery();
    }

    /// <param name="table">suggestions or suggestion_breaks.</param>
    private void Delete(string table, IEnumerable<long> ids)
    {
        using var command = db.Command($"DELETE FROM {table} WHERE id = $id;");
        var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var each in ids)
        {
            id.Value = each;
            command.ExecuteNonQuery();
        }
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand command, DateTimeOffset start, DateTimeOffset end)
    {
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
    }

    private static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
