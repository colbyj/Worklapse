using System.Globalization;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Core.Projects;

/// <param name="Color">0xRRGGBB.</param>
public sealed record Category(long Id, string Name, int Color);

/// <param name="CategoryId">Null when its category was deleted (or it never had one).</param>
/// <param name="Color">0xRRGGBB, or null to use the category's.</param>
/// <param name="Archived">Hidden from lists, kept in history.</param>
/// <param name="Suggest">Suggested for time without a project, and its time teaches suggestions (<see cref="ProjectStore.SetSuggestionOptOuts"/>).</param>
/// <param name="ReadScreen">The screen is read during its time, for suggestions.</param>
/// <param name="StartsOn">Its first day; null for no limit (<see cref="ProjectStore.SetDates"/>).</param>
/// <param name="EndsOn">Its last day; null for no limit.</param>
public sealed record Project(long Id, string Name, long? CategoryId, int? Color, bool Archived, bool Suggest = true, bool ReadScreen = true,
    DateOnly? StartsOn = null, DateOnly? EndsOn = null)
{
    /// <summary>Whether a day is within its dates (both included): suggestions don't offer it on other days.</summary>
    public bool RunsOn(DateOnly day) => (StartsOn is not { } start || day >= start) && (EndsOn is not { } end || day <= end);
}

/// <summary>A range set by hand. <paramref name="ProjectId"/> null means "no project", overriding any link.</summary>
public sealed record ProjectSpan(DateTimeOffset Start, DateTimeOffset End, long? ProjectId);

/// <summary>What was done for a project on a day.</summary>
public sealed record ProjectNote(DateOnly Day, long ProjectId, string Text);

/// <summary>Time to bill for a project on a day.</summary>
public sealed record LoggedTime(DateOnly Day, long ProjectId, TimeSpan Logged);

/// <summary>
/// Projects and everything said about them: categories, window title rules, the ranges set by hand, notes, day notes and
/// the timesheet. Links from apps and sites to projects live with the apps and sites (<see cref="History.HistoryStore.SetProject"/>).
/// Uses the connection it's given, from one thread at a time.
/// </summary>
public sealed class ProjectStore(WorklapseDatabase db)
{
    /// <summary>The colour of a project with neither its own colour nor a category.</summary>
    public const int DefaultColor = 0x5D6D7E;

    // ---- Categories ----

    public List<Category> GetCategories()
    {
        using var command = db.Command("SELECT id, name, color FROM categories ORDER BY name COLLATE NOCASE, id;");
        using var reader = command.ExecuteReader();
        var categories = new List<Category>();
        while (reader.Read())
            categories.Add(new Category(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return categories;
    }

    public long AddCategory(string name, int color)
    {
        using var command = db.Command("INSERT INTO categories (name, color) VALUES ($name, $color) RETURNING id;");
        command.Parameters.AddWithValue("$name", RequireName(name));
        command.Parameters.AddWithValue("$color", color);
        return (long)command.ExecuteScalar()!;
    }

    public void UpdateCategory(Category category)
    {
        using var command = db.Command("UPDATE categories SET name = $name, color = $color WHERE id = $id;");
        command.Parameters.AddWithValue("$id", category.Id);
        command.Parameters.AddWithValue("$name", RequireName(category.Name));
        command.Parameters.AddWithValue("$color", category.Color);
        command.ExecuteNonQuery();
    }

    /// <summary>Deletes a category; its projects stay, without one.</summary>
    public void DeleteCategory(long id)
    {
        using var command = db.Command("DELETE FROM categories WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // ---- Projects ----

    public List<Project> GetProjects(bool includeArchived = true)
    {
        using var command = db.Command($"""
            SELECT id, name, category_id, color, archived, suggest, read_screen, starts_on, ends_on FROM projects
            {(includeArchived ? "" : "WHERE archived = 0")}
            ORDER BY name COLLATE NOCASE, id;
            """);
        using var reader = command.ExecuteReader();
        var projects = new List<Project>();
        while (reader.Read())
            projects.Add(new Project(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetInt64(4) != 0,
                Suggest: reader.GetInt64(5) != 0,
                ReadScreen: reader.GetInt64(6) != 0,
                StartsOn: reader.IsDBNull(7) ? null : ParseDay(reader.GetString(7)),
                EndsOn: reader.IsDBNull(8) ? null : ParseDay(reader.GetString(8))));
        return projects;
    }

    /// <summary>A project's first and last days (null: no limit). Suggestions don't offer it on days outside them.</summary>
    public void SetDates(long projectId, DateOnly? startsOn, DateOnly? endsOn)
    {
        using var command = db.Command("UPDATE projects SET starts_on = $starts, ends_on = $ends WHERE id = $id;");
        command.Parameters.AddWithValue("$id", projectId);
        command.Parameters.AddWithValue("$starts", startsOn is { } starts ? DayKey(starts) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$ends", endsOn is { } ends ? DayKey(ends) : (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Whether the project is still there (it may have been deleted in Settings meanwhile).</summary>
    public bool ProjectExists(long id)
    {
        using var command = db.Command("SELECT EXISTS (SELECT 1 FROM projects WHERE id = $id);");
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! != 0;
    }

    public long AddProject(string name, long? categoryId, int? color = null)
    {
        using var command = db.Command("INSERT INTO projects (name, category_id, color) VALUES ($name, $category, $color) RETURNING id;");
        command.Parameters.AddWithValue("$name", RequireName(name));
        command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Saves a project's name, category, colour and archived state (not its opt-outs, <see cref="SetSuggestionOptOuts"/>, nor
    /// its dates, <see cref="SetDates"/>).
    /// </summary>
    public void UpdateProject(Project project)
    {
        using var command = db.Command("""
            UPDATE projects SET name = $name, category_id = $category, color = $color, archived = $archived WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", RequireName(project.Name));
        command.Parameters.AddWithValue("$category", (object?)project.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$color", (object?)project.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", project.Archived ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether a project is suggested (and its time teaches suggestions), and whether the screen is read during its time.
    /// Turning reading off deletes the words already read during its time.
    /// </summary>
    public void SetSuggestionOptOuts(long projectId, bool suggest, bool readScreen)
    {
        using (var command = db.Command("UPDATE projects SET suggest = $suggest, read_screen = $read WHERE id = $id;"))
        {
            command.Parameters.AddWithValue("$id", projectId);
            command.Parameters.AddWithValue("$suggest", suggest ? 1 : 0);
            command.Parameters.AddWithValue("$read", readScreen ? 1 : 0);
            command.ExecuteNonQuery();
        }
        if (!readScreen)
            new ScreenWordStore(db).Prune();
    }

    /// <summary>
    /// Deletes a project with its ranges set by hand, notes and logged time; apps and sites linked to it are
    /// unlinked. Archiving keeps all of that.
    /// </summary>
    public void DeleteProject(long id)
    {
        using var command = db.Command("DELETE FROM projects WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>The colour a project is drawn in: its own, else its category's, else <see cref="DefaultColor"/>.</summary>
    public static int ColorOf(Project project, IReadOnlyDictionary<long, Category> categories) =>
        project.Color ?? (project.CategoryId is { } c && categories.TryGetValue(c, out var category) ? category.Color : DefaultColor);

    // ---- Window title rules ----

    /// <summary>Every project's window title rules, each project's in its list's order.</summary>
    public List<TitleRule> GetTitleRules()
    {
        using var command = db.Command("SELECT id, project_id, text, app FROM title_rules ORDER BY project_id, position, id;");
        using var reader = command.ExecuteReader();
        var rules = new List<TitleRule>();
        while (reader.Read())
            rules.Add(new TitleRule(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rules;
    }

    /// <summary>
    /// Makes <paramref name="rules"/> a project's window title rules, in that order: rules with an id are kept (so they
    /// stay older than rules added since, see <see cref="TitleRule.Precedence"/>), the project's others are deleted.
    /// </summary>
    /// <returns>Each rule's id, new ones included, in the same order.</returns>
    /// <remarks>Checks everything before changing anything, so it can run inside the caller's transaction or alone.</remarks>
    public List<long> SetTitleRules(long projectId, IReadOnlyList<(long? Id, string Text, string? App)> rules)
    {
        if (rules.Any(r => string.IsNullOrWhiteSpace(r.Text)))
            throw new ArgumentException("A window title rule needs text to look for.", nameof(rules));
        var kept = rules.Where(r => r.Id is not null).Select(r => r.Id!.Value).Distinct().ToList();
        using (var owned = db.Command($"SELECT count(*) FROM title_rules WHERE project_id = $project AND id IN ({string.Join(",", kept)});"))
        {
            owned.Parameters.AddWithValue("$project", projectId);
            if ((long)owned.ExecuteScalar()! != kept.Count || kept.Count != rules.Count(r => r.Id is not null))
                throw new InvalidOperationException($"Some of these window title rules aren't project {projectId}'s.");
        }

        using (var delete = db.Command($"DELETE FROM title_rules WHERE project_id = $project AND id NOT IN ({string.Join(",", kept)});"))
        {
            delete.Parameters.AddWithValue("$project", projectId);
            delete.ExecuteNonQuery();
        }

        var ids = new List<long>();
        for (var position = 0; position < rules.Count; position++)
        {
            var (id, text, app) = rules[position];
            using var command = id is null
                ? db.Command("INSERT INTO title_rules (project_id, text, app, position) VALUES ($project, $text, $app, $position) RETURNING id;")
                : db.Command("UPDATE title_rules SET text = $text, app = $app, position = $position WHERE id = $id AND project_id = $project RETURNING id;");
            command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$text", text.Trim());
            command.Parameters.AddWithValue("$app", string.IsNullOrWhiteSpace(app) ? DBNull.Value : app.Trim());
            command.Parameters.AddWithValue("$position", position);
            if (id is { } existing)
                command.Parameters.AddWithValue("$id", existing);
            ids.Add((long)command.ExecuteScalar()!);
        }

        // New rules of a project whose screen isn't read may take time whose words were read.
        var words = new ScreenWordStore(db);
        if (!words.ReadsScreenFor(projectId))
            words.Prune();
        return ids;
    }

    // ---- Ranges set by hand ----

    /// <summary>The ranges set by hand that overlap start..end, in time order (not clipped).</summary>
    public List<ProjectSpan> GetSpans(DateTimeOffset start, DateTimeOffset end)
    {
        using var command = db.Command("""
            SELECT start_ms, end_ms, project_id FROM project_spans
            WHERE start_ms < $end AND end_ms > $start ORDER BY start_ms;
            """);
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var spans = new List<ProjectSpan>();
        while (reader.Read())
            spans.Add(new ProjectSpan(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        return spans;
    }

    /// <summary>
    /// Sets start..end to a project by hand, or to "no project" (null), replacing whatever was set there before;
    /// links no longer apply inside it.
    /// </summary>
    public void SetSpan(DateTimeOffset start, DateTimeOffset end, long? projectId)
    {
        if (end <= start)
            throw new ArgumentException("A range must end after it starts.", nameof(end));
        using var transaction = db.BeginTransaction();
        SetSpanInTransaction(start, end, projectId);
        transaction.Commit();
    }

    /// <summary>As <see cref="SetSpan"/>, inside a transaction the caller holds (accepting a suggestion writes several).</summary>
    internal void SetSpanInTransaction(DateTimeOffset start, DateTimeOffset end, long? projectId)
    {
        if (end <= start)
            throw new ArgumentException("A range must end after it starts.", nameof(end));
        var (s, e) = (start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds());
        CutOut(s, e);

        // Join the neighbours with the same project, so the table doesn't fill with fragments.
        using (var before = db.Command("DELETE FROM project_spans WHERE end_ms = $s AND project_id IS $project RETURNING start_ms;"))
        {
            before.Parameters.AddWithValue("$s", s);
            before.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
            if (before.ExecuteScalar() is long joined)
                s = joined;
        }
        using (var after = db.Command("DELETE FROM project_spans WHERE start_ms = $e AND project_id IS $project RETURNING end_ms;"))
        {
            after.Parameters.AddWithValue("$e", e);
            after.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
            if (after.ExecuteScalar() is long joined)
                e = joined;
        }
        Insert(s, e, projectId);
        // Time given to a project whose screen isn't read loses the words read during it.
        var words = new ScreenWordStore(db);
        if (projectId is { } project && !words.ReadsScreenFor(project))
            words.Prune(start, end);
    }

    /// <summary>Removes s..e from the ranges set by hand, keeping the parts of them outside it.</summary>
    private void CutOut(long s, long e)
    {
        var overlapping = new List<(long Start, long End, long? Project)>();
        using (var find = db.Command("DELETE FROM project_spans WHERE start_ms < $e AND end_ms > $s RETURNING start_ms, end_ms, project_id;"))
        {
            find.Parameters.AddWithValue("$s", s);
            find.Parameters.AddWithValue("$e", e);
            using var reader = find.ExecuteReader();
            while (reader.Read())
                overlapping.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }
        foreach (var span in overlapping)
        {
            if (span.Start < s)
                Insert(span.Start, s, span.Project);
            if (span.End > e)
                Insert(e, span.End, span.Project);
        }
    }

    private void Insert(long s, long e, long? projectId)
    {
        using var insert = db.Command("INSERT INTO project_spans (start_ms, end_ms, project_id) VALUES ($s, $e, $project);");
        insert.Parameters.AddWithValue("$s", s);
        insert.Parameters.AddWithValue("$e", e);
        insert.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    // ---- Notes ----

    /// <summary>The notes on projects from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public List<ProjectNote> GetNotes(DateOnly from, DateOnly to)
    {
        using var command = db.Command("SELECT day, project_id, text FROM project_notes WHERE day >= $from AND day < $to ORDER BY day, project_id;");
        command.Parameters.AddWithValue("$from", DayKey(from));
        command.Parameters.AddWithValue("$to", DayKey(to));
        using var reader = command.ExecuteReader();
        var notes = new List<ProjectNote>();
        while (reader.Read())
            notes.Add(new ProjectNote(ParseDay(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2)));
        return notes;
    }

    /// <summary>Sets a project's note for a day; an empty text removes it.</summary>
    public void SetNote(DateOnly day, long projectId, string text)
    {
        using var command = string.IsNullOrWhiteSpace(text)
            ? db.Command("DELETE FROM project_notes WHERE day = $day AND project_id = $project;")
            : db.Command("""
                INSERT INTO project_notes (day, project_id, text) VALUES ($day, $project, $text)
                ON CONFLICT (day, project_id) DO UPDATE SET text = excluded.text;
                """);
        command.Parameters.AddWithValue("$day", DayKey(day));
        command.Parameters.AddWithValue("$project", projectId);
        if (!string.IsNullOrWhiteSpace(text))
            command.Parameters.AddWithValue("$text", text.Trim());
        command.ExecuteNonQuery();
    }

    // ---- Day notes ----

    public string GetDayNote(DateOnly day)
    {
        using var command = db.Command("SELECT text FROM day_notes WHERE day = $day;");
        command.Parameters.AddWithValue("$day", DayKey(day));
        return command.ExecuteScalar() as string ?? "";
    }

    /// <summary>The day notes from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public Dictionary<DateOnly, string> GetDayNotes(DateOnly from, DateOnly to)
    {
        using var command = db.Command("SELECT day, text FROM day_notes WHERE day >= $from AND day < $to;");
        command.Parameters.AddWithValue("$from", DayKey(from));
        command.Parameters.AddWithValue("$to", DayKey(to));
        using var reader = command.ExecuteReader();
        var notes = new Dictionary<DateOnly, string>();
        while (reader.Read())
            notes[ParseDay(reader.GetString(0))] = reader.GetString(1);
        return notes;
    }

    /// <summary>Sets a day's note; an empty text removes it.</summary>
    public void SetDayNote(DateOnly day, string text)
    {
        using var command = string.IsNullOrWhiteSpace(text)
            ? db.Command("DELETE FROM day_notes WHERE day = $day;")
            : db.Command("INSERT INTO day_notes (day, text) VALUES ($day, $text) ON CONFLICT (day) DO UPDATE SET text = excluded.text;");
        command.Parameters.AddWithValue("$day", DayKey(day));
        if (!string.IsNullOrWhiteSpace(text))
            command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    // ---- Timesheet ----

    /// <summary>Time logged from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public List<LoggedTime> GetLogged(DateOnly from, DateOnly to)
    {
        using var command = db.Command("SELECT day, project_id, logged_min FROM timesheet WHERE day >= $from AND day < $to ORDER BY day, project_id;");
        command.Parameters.AddWithValue("$from", DayKey(from));
        command.Parameters.AddWithValue("$to", DayKey(to));
        using var reader = command.ExecuteReader();
        var logged = new List<LoggedTime>();
        while (reader.Read())
            logged.Add(new LoggedTime(ParseDay(reader.GetString(0)), reader.GetInt64(1), TimeSpan.FromMinutes(reader.GetInt64(2))));
        return logged;
    }

    /// <summary>Sets the time to bill for a project on a day (whole minutes), or clears it (null).</summary>
    public void SetLogged(DateOnly day, long projectId, TimeSpan? logged)
    {
        if (logged is { } value && value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(logged), "Logged time can't be negative.");
        using var command = logged is null
            ? db.Command("DELETE FROM timesheet WHERE day = $day AND project_id = $project;")
            : db.Command("""
                INSERT INTO timesheet (day, project_id, logged_min) VALUES ($day, $project, $minutes)
                ON CONFLICT (day, project_id) DO UPDATE SET logged_min = excluded.logged_min;
                """);
        command.Parameters.AddWithValue("$day", DayKey(day));
        command.Parameters.AddWithValue("$project", projectId);
        if (logged is { } minutes)
            command.Parameters.AddWithValue("$minutes", (long)Math.Round(minutes.TotalMinutes));
        command.ExecuteNonQuery();
    }

    private static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string key) => DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A name is required.", nameof(name)) : name.Trim();
}
