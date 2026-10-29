using Dapper;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Core.Projects;

public sealed record Category
{
    public long Id { get; init; }
    public required string Name { get; init; }
    /// <summary>0xRRGGBB.</summary>
    public int Color { get; init; }
}

public sealed record Project
{
    public long Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Null when its category was deleted (or it never had one).</summary>
    public long? CategoryId { get; init; }
    /// <summary>0xRRGGBB, or null to use the category's.</summary>
    public int? Color { get; init; }
    /// <summary>Hidden from lists, kept in history.</summary>
    public bool Archived { get; init; }
    /// <summary>Suggested for time without a project, and its time teaches suggestions (<see cref="ProjectStore.SetSuggestionOptOuts"/>).</summary>
    public bool Suggest { get; init; } = true;
    /// <summary>The screen is read during its time, for suggestions.</summary>
    public bool ReadScreen { get; init; } = true;
    /// <summary>Its first day; null for no limit (<see cref="ProjectStore.SetDates"/>).</summary>
    public DateOnly? StartsOn { get; init; }
    /// <summary>Its last day; null for no limit.</summary>
    public DateOnly? EndsOn { get; init; }

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

    public List<Category> GetCategories() =>
        db.Connection.Query<Category>("SELECT id, name, color FROM categories ORDER BY name COLLATE NOCASE, id;").AsList();

    public long AddCategory(string name, int color) =>
        db.Connection.ExecuteScalar<long>("INSERT INTO categories (name, color) VALUES ($name, $color) RETURNING id;",
            new { name = RequireName(name), color });

    public void UpdateCategory(Category category) =>
        db.Connection.Execute("UPDATE categories SET name = $name, color = $color WHERE id = $id;",
            new { id = category.Id, name = RequireName(category.Name), color = category.Color });

    /// <summary>Deletes a category; its projects stay, without one.</summary>
    public void DeleteCategory(long id) => db.Connection.Execute("DELETE FROM categories WHERE id = $id;", new { id });

    // ---- Projects ----

    public List<Project> GetProjects(bool includeArchived = true) =>
        db.Connection.Query<Project>($"""
            SELECT id, name, category_id, color, archived, suggest, read_screen, starts_on, ends_on FROM projects
            {(includeArchived ? "" : "WHERE archived = 0")}
            ORDER BY name COLLATE NOCASE, id;
            """).AsList();

    /// <summary>A project's first and last days (null: no limit). Suggestions don't offer it on days outside them.</summary>
    public void SetDates(long projectId, DateOnly? startsOn, DateOnly? endsOn) =>
        db.Connection.Execute("UPDATE projects SET starts_on = $startsOn, ends_on = $endsOn WHERE id = $projectId;",
            new { projectId, startsOn, endsOn });

    /// <summary>Whether the project is still there (it may have been deleted in Settings meanwhile).</summary>
    public bool ProjectExists(long id) =>
        db.Connection.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM projects WHERE id = $id);", new { id });

    public long AddProject(string name, long? categoryId, int? color = null) =>
        db.Connection.ExecuteScalar<long>("INSERT INTO projects (name, category_id, color) VALUES ($name, $categoryId, $color) RETURNING id;",
            new { name = RequireName(name), categoryId, color });

    /// <summary>
    /// Saves a project's name, category, colour and archived state (not its opt-outs, <see cref="SetSuggestionOptOuts"/>, nor
    /// its dates, <see cref="SetDates"/>).
    /// </summary>
    public void UpdateProject(Project project) =>
        db.Connection.Execute("""
            UPDATE projects SET name = $name, category_id = $categoryId, color = $color, archived = $archived WHERE id = $id;
            """,
            new { id = project.Id, name = RequireName(project.Name), categoryId = project.CategoryId, color = project.Color, archived = project.Archived });

    /// <summary>
    /// Whether a project is suggested (and its time teaches suggestions), and whether the screen is read during its time.
    /// Turning reading off deletes the words already read during its time.
    /// </summary>
    public void SetSuggestionOptOuts(long projectId, bool suggest, bool readScreen)
    {
        db.Connection.Execute("UPDATE projects SET suggest = $suggest, read_screen = $readScreen WHERE id = $projectId;",
            new { projectId, suggest, readScreen });
        if (!readScreen)
            new ScreenWordStore(db).Prune();
    }

    /// <summary>
    /// Deletes a project with its ranges set by hand, notes and logged time; apps and sites linked to it are
    /// unlinked. Archiving keeps all of that.
    /// </summary>
    public void DeleteProject(long id) => db.Connection.Execute("DELETE FROM projects WHERE id = $id;", new { id });

    /// <summary>The colour a project is drawn in: its own, else its category's, else <see cref="DefaultColor"/>.</summary>
    public static int ColorOf(Project project, IReadOnlyDictionary<long, Category> categories) =>
        project.Color ?? (project.CategoryId is { } c && categories.TryGetValue(c, out var category) ? category.Color : DefaultColor);

    // ---- Window title rules ----

    /// <summary>Every project's window title rules, each project's in its list's order.</summary>
    public List<TitleRule> GetTitleRules() =>
        db.Connection.Query<TitleRule>("SELECT id, project_id, text, app FROM title_rules ORDER BY project_id, position, id;").AsList();

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
        var owned = db.Connection.ExecuteScalar<long>(
            $"SELECT count(*) FROM title_rules WHERE project_id = $projectId AND id IN ({string.Join(",", kept)});", new { projectId });
        if (owned != kept.Count || kept.Count != rules.Count(r => r.Id is not null))
            throw new InvalidOperationException($"Some of these window title rules aren't project {projectId}'s.");

        db.Connection.Execute($"DELETE FROM title_rules WHERE project_id = $projectId AND id NOT IN ({string.Join(",", kept)});", new { projectId });

        var ids = new List<long>();
        for (var position = 0; position < rules.Count; position++)
        {
            var (id, text, app) = rules[position];
            var sql = id is null
                ? "INSERT INTO title_rules (project_id, text, app, position) VALUES ($projectId, $text, $app, $position) RETURNING id;"
                : "UPDATE title_rules SET text = $text, app = $app, position = $position WHERE id = $id AND project_id = $projectId RETURNING id;";
            ids.Add(db.Connection.ExecuteScalar<long>(sql,
                new { id, projectId, text = text.Trim(), app = string.IsNullOrWhiteSpace(app) ? null : app.Trim(), position }));
        }

        // New rules of a project whose screen isn't read may take time whose words were read.
        var words = new ScreenWordStore(db);
        if (!words.ReadsScreenFor(projectId))
            words.Prune();
        return ids;
    }

    // ---- Ranges set by hand ----

    /// <summary>The ranges set by hand that overlap start..end, in time order (not clipped).</summary>
    public List<ProjectSpan> GetSpans(DateTimeOffset start, DateTimeOffset end) =>
        db.Connection.Query<(long Start, long End, long? Project)>("""
            SELECT start_ms, end_ms, project_id FROM project_spans
            WHERE start_ms < $end AND end_ms > $start ORDER BY start_ms;
            """,
            new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() })
        .Select(r => new ProjectSpan(DateTimeOffset.FromUnixTimeMilliseconds(r.Start), DateTimeOffset.FromUnixTimeMilliseconds(r.End), r.Project))
        .ToList();

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
        s = db.Connection.ExecuteScalar<long?>("DELETE FROM project_spans WHERE end_ms = $s AND project_id IS $projectId RETURNING start_ms;",
            new { s, projectId }) ?? s;
        e = db.Connection.ExecuteScalar<long?>("DELETE FROM project_spans WHERE start_ms = $e AND project_id IS $projectId RETURNING end_ms;",
            new { e, projectId }) ?? e;
        Insert(s, e, projectId);
        // Time given to a project whose screen isn't read loses the words read during it.
        var words = new ScreenWordStore(db);
        if (projectId is { } project && !words.ReadsScreenFor(project))
            words.Prune(start, end);
    }

    /// <summary>Removes s..e from the ranges set by hand, keeping the parts of them outside it.</summary>
    private void CutOut(long s, long e)
    {
        var overlapping = db.Connection.Query<(long Start, long End, long? Project)>(
            "DELETE FROM project_spans WHERE start_ms < $e AND end_ms > $s RETURNING start_ms, end_ms, project_id;", new { s, e }).AsList();
        foreach (var span in overlapping)
        {
            if (span.Start < s)
                Insert(span.Start, s, span.Project);
            if (span.End > e)
                Insert(e, span.End, span.Project);
        }
    }

    private void Insert(long s, long e, long? projectId) =>
        db.Connection.Execute("INSERT INTO project_spans (start_ms, end_ms, project_id) VALUES ($s, $e, $projectId);", new { s, e, projectId });

    // ---- Notes ----

    /// <summary>The notes on projects from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public List<ProjectNote> GetNotes(DateOnly from, DateOnly to) =>
        db.Connection.Query<ProjectNote>("SELECT day, project_id, text FROM project_notes WHERE day >= $from AND day < $to ORDER BY day, project_id;",
            new { from, to }).AsList();

    /// <summary>Sets a project's note for a day; an empty text removes it.</summary>
    public void SetNote(DateOnly day, long projectId, string text) =>
        db.Connection.Execute(string.IsNullOrWhiteSpace(text)
            ? "DELETE FROM project_notes WHERE day = $day AND project_id = $projectId;"
            : """
              INSERT INTO project_notes (day, project_id, text) VALUES ($day, $projectId, $text)
              ON CONFLICT (day, project_id) DO UPDATE SET text = excluded.text;
              """,
            new { day, projectId, text = text.Trim() });

    // ---- Day notes ----

    public string GetDayNote(DateOnly day) =>
        db.Connection.ExecuteScalar<string?>("SELECT text FROM day_notes WHERE day = $day;", new { day }) ?? "";

    /// <summary>The day notes from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public Dictionary<DateOnly, string> GetDayNotes(DateOnly from, DateOnly to) =>
        db.Connection.Query<(DateOnly Day, string Text)>("SELECT day, text FROM day_notes WHERE day >= $from AND day < $to;", new { from, to })
            .ToDictionary(n => n.Day, n => n.Text);

    /// <summary>Sets a day's note; an empty text removes it.</summary>
    public void SetDayNote(DateOnly day, string text) =>
        db.Connection.Execute(string.IsNullOrWhiteSpace(text)
            ? "DELETE FROM day_notes WHERE day = $day;"
            : "INSERT INTO day_notes (day, text) VALUES ($day, $text) ON CONFLICT (day) DO UPDATE SET text = excluded.text;",
            new { day, text });

    // ---- Timesheet ----

    /// <summary>Time logged from <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public List<LoggedTime> GetLogged(DateOnly from, DateOnly to) =>
        db.Connection.Query<(DateOnly Day, long ProjectId, long Minutes)>(
            "SELECT day, project_id, logged_min FROM timesheet WHERE day >= $from AND day < $to ORDER BY day, project_id;", new { from, to })
        .Select(r => new LoggedTime(r.Day, r.ProjectId, TimeSpan.FromMinutes(r.Minutes)))
        .ToList();

    /// <summary>Sets the time to bill for a project on a day (whole minutes), or clears it (null).</summary>
    public void SetLogged(DateOnly day, long projectId, TimeSpan? logged)
    {
        if (logged is { } value && value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(logged), "Logged time can't be negative.");
        db.Connection.Execute(logged is null
            ? "DELETE FROM timesheet WHERE day = $day AND project_id = $projectId;"
            : """
              INSERT INTO timesheet (day, project_id, logged_min) VALUES ($day, $projectId, $minutes)
              ON CONFLICT (day, project_id) DO UPDATE SET logged_min = excluded.logged_min;
              """,
            new { day, projectId, minutes = logged is { } m ? (long)Math.Round(m.TotalMinutes) : 0 });
    }

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A name is required.", nameof(name)) : name.Trim();
}
