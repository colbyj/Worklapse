using Dapper;
using Microsoft.Data.Sqlite;

namespace Worklapse.Core.Storage;

/// <summary>
/// Undo for the edits made through one connection. While an edit runs (<see cref="Record"/>), triggers on the tables it
/// may change write the SQL that reverses each row change into a log; <see cref="Undo"/> runs the last edit's SQL backward.
/// The triggers are temporary, so they see only this connection's writes, and only inside <see cref="Record"/>: the
/// recorder's captures and suggestions worked out in the background are never undone. An update is reversed only in the
/// columns it changed, so what other connections write to the same rows meanwhile stays.
/// </summary>
public sealed class UndoHistory
{
    /// <summary>How many edits can be undone; older ones are forgotten.</summary>
    public const int MaxSteps = 100;

    private readonly WorklapseDatabase _db;
    private readonly List<(long Step, string Label)> _steps = [];
    private long _nextStep = 1;
    private bool _recording;

    /// <param name="tables">The tables edits change. Each needs a primary key (rowid alone isn't kept by VACUUM).</param>
    public UndoHistory(WorklapseDatabase db, IEnumerable<string> tables)
    {
        _db = db;
        _db.Execute("""
            CREATE TEMP TABLE IF NOT EXISTS undo_state (recording INTEGER NOT NULL, step INTEGER NOT NULL);
            DELETE FROM temp.undo_state;
            INSERT INTO temp.undo_state VALUES (0, 0);
            CREATE TEMP TABLE IF NOT EXISTS undo_log (id INTEGER PRIMARY KEY, step INTEGER NOT NULL, sql TEXT NOT NULL);
            DELETE FROM temp.undo_log;
            """);
        foreach (var table in tables)
            CreateTriggers(table);
    }

    /// <summary>What <see cref="Undo"/> would undo, as the edit was named; null when there's nothing to undo.</summary>
    public string? NextLabel => _steps.Count > 0 ? _steps[^1].Label : null;

    public bool CanUndo => _steps.Count > 0;

    /// <summary>
    /// Runs an edit, keeping what it changes so it can be undone as one step named <paramref name="label"/>. An edit
    /// inside another is part of it. An edit that changes nothing isn't kept.
    /// </summary>
    public void Record(string label, Action edit)
    {
        if (_recording)
        {
            edit();
            return;
        }
        var step = _nextStep++;
        SetState(recording: true, step);
        _recording = true;
        try
        {
            edit();
        }
        finally
        {
            _recording = false;
            SetState(recording: false, step);
        }
        if (Count(step) == 0)
            return;
        _steps.Add((step, label));
        if (_steps.Count > MaxSteps)
        {
            Forget(_steps[0].Step);
            _steps.RemoveAt(0);
        }
    }

    /// <summary>
    /// Undoes the last edit kept, in one transaction; false when there's none. When it can't be undone (what it changed
    /// is gone, say), nothing changes, the history is cleared, and the error is thrown.
    /// </summary>
    public bool Undo()
    {
        if (_steps.Count == 0)
            return false;
        var (step, _) = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        try
        {
            using var transaction = _db.BeginTransaction();
            var reversals = _db.Connection.Query<string>("SELECT sql FROM temp.undo_log WHERE step = $step ORDER BY id DESC;", new { step }).AsList();
            foreach (var sql in reversals)
                _db.Execute(sql);
            Forget(step);
            transaction.Commit();
        }
        catch (SqliteException)
        {
            Clear();
            throw;
        }
        return true;
    }

    /// <summary>Forgets every edit kept: they can't be undone any more.</summary>
    public void Clear()
    {
        _steps.Clear();
        _db.Execute("DELETE FROM temp.undo_log;");
    }

    private void SetState(bool recording, long step) =>
        _db.Connection.Execute("UPDATE temp.undo_state SET recording = $recording, step = $step;", new { recording, step });

    private long Count(long step) =>
        _db.Connection.ExecuteScalar<long>("SELECT count(*) FROM temp.undo_log WHERE step = $step;", new { step });

    private void Forget(long step) =>
        _db.Connection.Execute("DELETE FROM temp.undo_log WHERE step = $step;", new { step });

    /// <summary>
    /// Triggers that log, for each row inserted, updated or deleted in <paramref name="table"/> while recording, the SQL
    /// putting it back: delete what was inserted, set back what was updated (the columns that changed), insert what was
    /// deleted. Rows are found by the table's primary key.
    /// </summary>
    private void CreateTriggers(string table)
    {
        var columns = new List<(string Name, int Key)>();
        using (var info = _db.Command($"PRAGMA main.table_info({Name(table)});"))
        using (var reader = info.ExecuteReader())
            while (reader.Read())
                columns.Add((reader.GetString(1), reader.GetInt32(5)));
        var key = columns.Where(c => c.Key > 0).OrderBy(c => c.Key).Select(c => c.Name).ToList();
        if (key.Count == 0)
            throw new ArgumentException($"{table} has no primary key to undo by.", nameof(table));

        // SQL expressions building the reversing SQL from the row before (old) or after (new) the change.
        string Where(string row) => "' WHERE " + string.Join(" || ' AND ", key.Select(k => $"{Name(k)} = ' || quote({row}.{Name(k)})")) + " || ';'";
        var inserted = $"'DELETE FROM main.{Name(table)}' || {Where("new")}";
        // The key set to itself first, so the list of changed columns that follows never starts with a comma.
        var updated = $"'UPDATE main.{Name(table)} SET {Name(key[0])} = {Name(key[0])}'"
            + string.Concat(columns.Select(c => $" || CASE WHEN old.{Name(c.Name)} IS NOT new.{Name(c.Name)} THEN ', {Name(c.Name)} = ' || quote(old.{Name(c.Name)}) ELSE '' END"))
            + $" || {Where("new")}";
        var deleted = $"'INSERT INTO main.{Name(table)} ({string.Join(", ", columns.Select(c => Name(c.Name)))}) VALUES (' || "
            + string.Join(" || ', ' || ", columns.Select(c => $"quote(old.{Name(c.Name)})")) + " || ');'";

        foreach (var (operation, reversal) in new[] { ("INSERT", inserted), ("UPDATE", updated), ("DELETE", deleted) })
            _db.Execute($"""
                CREATE TEMP TRIGGER IF NOT EXISTS {Name($"undo_{table}_{operation.ToLowerInvariant()}")} AFTER {operation} ON main.{Name(table)}
                WHEN (SELECT recording FROM temp.undo_state)
                BEGIN
                    INSERT INTO temp.undo_log (step, sql) VALUES ((SELECT step FROM temp.undo_state), {reversal});
                END;
                """);
    }

    /// <summary>A table or column name, quoted for SQL.</summary>
    private static string Name(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
