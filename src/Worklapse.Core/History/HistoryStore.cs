using Dapper;
using Worklapse.Core.Platform;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Core.History;

/// <param name="Captures">How many captures were deleted.</param>
/// <param name="StagedSegmentsAffected">
/// Some of them used a segment not encoded yet, possibly the one being recorded, or an image the recorder may still
/// show again: their deleted frames leave the disk once it is closed and finished (<see cref="Recording.Recorder.CloseSegmentsSoon"/>).
/// </param>
public readonly record struct DeleteResult(int Captures, bool StagedSegmentsAffected);

/// <summary>
/// Reads recorded days and edits them (marking ranges for deletion). Uses its own connection; meant for
/// the UI thread, alongside the recorder writing through another connection.
/// </summary>
/// <param name="dayStart">When each day begins (<see cref="LocalDay"/>); midnight by default.</param>
public sealed class HistoryStore(WorklapseDatabase db, TimeOnly dayStart = default) : IDisposable
{
    public WorklapseDatabase Database => db;

    public TimeOnly DayStart => dayStart;

    public DateOnly Today => LocalDay.Today(dayStart);

    /// <summary>Local days that have at least one capture, oldest first.</summary>
    public IReadOnlyList<DateOnly> GetDays() =>
        // Local day of each capture, using the offset recorded with it.
        db.Connection.Query<DateOnly>("""
            SELECT DISTINCT date((ts_ms / 1000) + utc_offset_min * 60 - $dayStart, 'unixepoch') FROM captures ORDER BY 1;
            """, new { dayStart = (long)dayStart.ToTimeSpan().TotalSeconds }).AsList();

    public DayHistory LoadDay(DateOnly day)
    {
        var (startMs, endMs) = DayBounds(day);

        var frames = new Dictionary<long, List<MonitorFrame>>();
        var monitorIds = new HashSet<long>();
        var frameRows = db.Connection.Query<(long CaptureId, long MonitorId, long SegmentId, int FrameIndex)>("""
            SELECT f.capture_id, f.monitor_id, f.segment_id, f.frame_index
            FROM frames f JOIN captures c ON c.id = f.capture_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end;
            """, new { start = startMs, end = endMs });
        foreach (var row in frameRows)
        {
            if (!frames.TryGetValue(row.CaptureId, out var list))
                frames[row.CaptureId] = list = [];
            list.Add(new MonitorFrame(row.MonitorId, new FrameRef(row.SegmentId, row.FrameIndex)));
            monitorIds.Add(row.MonitorId);
        }

        // Layouts are shared: captures with the same monitors attached get the same list.
        var layouts = new Dictionary<long, IReadOnlyList<long>>();
        var layoutRows = db.Connection.Query<(long Id, string Monitors)>("""
            SELECT id, monitors FROM layouts
            WHERE id IN (SELECT layout_id FROM captures WHERE ts_ms >= $start AND ts_ms < $end);
            """, new { start = startMs, end = endMs });
        foreach (var row in layoutRows)
        {
            var ids = row.Monitors.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
            layouts[row.Id] = ids;
            monitorIds.UnionWith(ids);
        }

        var rules = new TitleRuleSet(new ProjectStore(db).GetTitleRules());
        var captures = new List<CaptureRecord>();
        var unrecorded = new List<int>(); // captures from before layouts were recorded
        var captureRows = db.Connection.Query<CaptureRow>("""
            SELECT c.id, c.ts_ms, c.utc_offset_min, c.kind, a.exe_name, c.window_title,
                   c.window_x, c.window_y, c.window_width, c.window_height, coalesce(a.productive, 0) AS app_on_task, c.pending_delete,
                   s.domain AS site, s.productive AS site_on_task, c.layout_id, a.project_id AS app_project_id, s.project_id AS site_project_id, c.task_mark
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id LEFT JOIN sites s ON s.id = c.site_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end
            ORDER BY c.ts_ms, c.id;
            """, new { start = startMs, end = endMs });
        foreach (var row in captureRows)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(row.TsMs).ToOffset(TimeSpan.FromMinutes(row.UtcOffsetMin));
            PixelRect? window = row.WindowX is { } x
                ? new PixelRect(x, row.WindowY!.Value, row.WindowWidth!.Value, row.WindowHeight!.Value)
                : null;
            IReadOnlyList<long>? layout = row.LayoutId is { } layoutId ? layouts[layoutId] : null;
            if (layout is null)
                unrecorded.Add(captures.Count);
            captures.Add(new CaptureRecord(
                row.Id,
                time,
                row.Kind,
                AppName: row.ExeName,
                Site: row.Site,
                WindowTitle: row.WindowTitle,
                window,
                AppOnTask: row.AppOnTask,
                SiteOnTask: row.SiteOnTask,
                PendingDelete: row.PendingDelete,
                frames.TryGetValue(row.Id, out var list) ? list : [],
                MonitorIds: layout ?? [],
                AppProjectId: row.AppProjectId,
                SiteProjectId: row.SiteProjectId,
                RuleProjectId: rules.ProjectOf(row.ExeName, row.WindowTitle),
                TaskMark: row.TaskMark));
        }

        var monitors = monitorIds.Count == 0 ? [] : db.Connection.Query<(long Id, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)>(
                $"SELECT id, device_name, x, y, width, height, is_primary FROM monitors WHERE id IN ({string.Join(",", monitorIds)}) ORDER BY id;")
            .Select(m => new MonitorRecord(m.Id, m.DeviceName, new PixelRect(m.X, m.Y, m.Width, m.Height), m.IsPrimary))
            .ToList();

        if (unrecorded.Count > 0)
        {
            var guessed = MonitorLayouts.Guess(captures, monitors.ToDictionary(m => m.Id));
            foreach (var i in unrecorded)
                captures[i] = captures[i] with { MonitorIds = guessed[i] };
        }

        return new DayHistory(day, captures, monitors);
    }

    /// <summary>
    /// The captures of the local days from <paramref name="from"/> up to (not including) <paramref name="to"/>, in
    /// time order, as just what totals per project need (no names, titles or frames): one query for a week or a month.
    /// On-task and the linked project are worked out as <see cref="CaptureRecord.OnTask"/> and
    /// <see cref="CaptureRecord.LinkedProjectId"/> do.
    /// </summary>
    public List<ActivitySample> LoadSamples(DateOnly from, DateOnly to)
    {
        var startMs = DayBounds(from).Start;
        var endMs = DayBounds(to).Start; // the end of the day before
        var rules = new TitleRuleSet(new ProjectStore(db).GetTitleRules());
        // Names and titles only when there are rules to match them against.
        return db.Connection.Query<SampleRow>($"""
            SELECT c.ts_ms, c.utc_offset_min, c.kind, c.pending_delete,
                   coalesce(c.task_mark, s.productive, a.productive, 0) AS on_task, coalesce(s.project_id, a.project_id) AS project_id,
                   {(rules.IsEmpty ? "NULL AS exe_name, NULL AS window_title" : "a.exe_name, c.window_title")}
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id LEFT JOIN sites s ON s.id = c.site_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end
            ORDER BY c.ts_ms, c.id;
            """, new { start = startMs, end = endMs })
            .Select(r => new ActivitySample(
                DateTimeOffset.FromUnixTimeMilliseconds(r.TsMs).ToOffset(TimeSpan.FromMinutes(r.UtcOffsetMin)),
                r.Kind,
                OnTask: r.OnTask,
                PendingDelete: r.PendingDelete,
                LinkedProjectId: rules.ProjectOf(r.ExeName, r.WindowTitle) ?? r.ProjectId))
            .ToList();
    }

    /// <summary>Marks (or unmarks) every capture with start ≤ time &lt; end for deletion.</summary>
    public int SetPendingDelete(DateTimeOffset start, DateTimeOffset end, bool pending) =>
        db.Connection.Execute("UPDATE captures SET pending_delete = $pending WHERE ts_ms >= $start AND ts_ms < $end;",
            new { pending, start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });

    /// <summary>
    /// Marks every capture with start ≤ time &lt; end on-task (true) or off-task (false), over what its app or site says,
    /// or leaves it to them again (null).
    /// </summary>
    public int SetTaskMark(DateTimeOffset start, DateTimeOffset end, bool? onTask) =>
        db.Connection.Execute("UPDATE captures SET task_mark = $mark WHERE ts_ms >= $start AND ts_ms < $end;",
            new { mark = onTask, start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });

    /// <summary>
    /// Deletes the captures marked on <paramref name="day"/> (their frame references and screen words go with them), and the
    /// apps and sites left without captures unless marked on-task or excluded, linked to a project or opted out of
    /// suggestions, so their names go too. Queues
    /// the segments the captures used, for the batch encoder to remove the frames nothing refers to any more from
    /// disk; the video stays there until it does.
    /// </summary>
    public DeleteResult DeleteMarked(DateOnly day)
    {
        var (startMs, endMs) = DayBounds(day);
        using var transaction = db.BeginTransaction();

        var staged = db.Connection.ExecuteScalar<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM frames f JOIN captures c ON c.id = f.capture_id JOIN segments s ON s.id = f.segment_id
                WHERE c.pending_delete = 1 AND c.ts_ms >= $start AND c.ts_ms < $end AND s.state = $staged);
            """, new { start = startMs, end = endMs, staged = (int)SegmentState.Staged });

        db.Connection.Execute("""
            INSERT INTO reclaim_queue (segment_id)
            SELECT DISTINCT f.segment_id FROM frames f JOIN captures c ON c.id = f.capture_id
            WHERE c.pending_delete = 1 AND c.ts_ms >= $start AND c.ts_ms < $end
            ON CONFLICT (segment_id) DO UPDATE SET generation = generation + 1;
            """, new { start = startMs, end = endMs });

        var deleted = db.Connection.Execute("DELETE FROM captures WHERE pending_delete = 1 AND ts_ms >= $start AND ts_ms < $end;",
            new { start = startMs, end = endMs });

        db.Execute("""
            DELETE FROM apps WHERE productive = 0 AND excluded = 0 AND project_id IS NULL AND read_screen = 1 AND learn = 1
                AND NOT EXISTS (SELECT 1 FROM captures WHERE app_id = apps.id);
            DELETE FROM sites WHERE productive = 0 AND excluded = 0 AND project_id IS NULL AND read_screen = 1 AND learn = 1
                AND NOT EXISTS (SELECT 1 FROM captures WHERE site_id = sites.id);
            """);
        transaction.Commit();
        return new DeleteResult(deleted, staged);
    }

    /// <summary>
    /// Marks an app (every copy with that exe name) or a site on-task or not. Classification is looked up
    /// when a day is read, so this applies to all history at once.
    /// </summary>
    public void SetOnTask(ProgramKey program, bool onTask) =>
        db.Connection.Execute(program.Kind == ProgramKind.App
            ? "UPDATE apps SET productive = $productive WHERE exe_name = $name;"
            : "UPDATE sites SET productive = $productive WHERE domain = $name;",
            new { productive = onTask, name = program.Name });

    /// <summary>
    /// Excludes an app (every copy with that exe name) or a site from recording, or includes it again. The
    /// recorder looks it up every tick; history already recorded stays as it is.
    /// </summary>
    public void SetExcluded(ProgramKey program, bool excluded) =>
        db.Connection.Execute(program.Kind == ProgramKind.App
            ? "UPDATE apps SET excluded = $excluded WHERE exe_name = $name;"
            : "UPDATE sites SET excluded = $excluded WHERE domain = $name;",
            new { excluded, name = program.Name });

    /// <summary>
    /// Whether the text in an app's (every copy with that exe name) or a site's windows is read, for suggestions. Turning it
    /// off deletes the words already read off them.
    /// </summary>
    public void SetReadScreen(ProgramKey program, bool read)
    {
        SetFlag(program, "read_screen", read);
        if (!read)
            new ScreenWordStore(db).Prune();
    }

    /// <summary>Whether an app's (every copy) or a site's windows teach and sway suggestions.</summary>
    public void SetLearn(ProgramKey program, bool learn) => SetFlag(program, "learn", learn);

    private void SetFlag(ProgramKey program, string column, bool on) =>
        db.Connection.Execute(program.Kind == ProgramKind.App
            ? $"UPDATE apps SET {column} = $on WHERE exe_name = $name;"
            : $"UPDATE sites SET {column} = $on WHERE domain = $name;",
            new { on, name = program.Name });

    /// <summary>
    /// Links an app (every copy with that exe name) or a site to a project, or unlinks it (null). Its active time is
    /// marked with the project wherever no range set by hand says otherwise; links are looked up when a day is read,
    /// so this applies to all history at once.
    /// </summary>
    public void SetProject(ProgramKey program, long? projectId)
    {
        db.Connection.Execute(program.Kind == ProgramKind.App
            ? "UPDATE apps SET project_id = $project WHERE exe_name = $name;"
            : "UPDATE sites SET project_id = $project WHERE domain = $name;",
            new { project = projectId, name = program.Name });

        // Its time now belongs to the project: if the screen isn't read during that project's time, its words go.
        var words = new ScreenWordStore(db);
        if (projectId is { } project && !words.ReadsScreenFor(project))
            words.Prune();
    }

    /// <summary>
    /// Every app and site recorded, with its classification, whether it's excluded, its suggestion opt-outs, and its active time (marked
    /// captures left out), counted as in <see cref="SpanSummary"/>: each capture until the next, at most
    /// <paramref name="maxCaptureLength"/>.
    /// </summary>
    public List<ProgramUsage> GetPrograms(TimeSpan maxCaptureLength) =>
        db.Connection.Query<ProgramRow>("""
            WITH spans AS (
                SELECT app_id, site_id,
                       CASE WHEN kind = 0 AND pending_delete = 0
                            THEN min(coalesce(lead(ts_ms) OVER (ORDER BY ts_ms, id) - ts_ms, $max), $max) ELSE 0 END AS ms
                FROM captures)
            SELECT 0 AS kind, a.exe_name AS name, max(a.productive) AS on_task, max(a.excluded) AS excluded, coalesce(sum(s.ms), 0) AS ms,
                   max(a.project_id) AS project_id, min(a.read_screen) AS read_screen, min(a.learn) AS learn
            FROM apps a LEFT JOIN spans s ON s.app_id = a.id
            GROUP BY a.exe_name
            UNION ALL
            SELECT 1, t.domain, t.productive, t.excluded, coalesce(sum(s.ms), 0), t.project_id, t.read_screen, t.learn
            FROM sites t LEFT JOIN spans s ON s.site_id = t.id
            GROUP BY t.id;
            """, new { max = (long)maxCaptureLength.TotalMilliseconds })
            .Select(r => new ProgramUsage(
                new ProgramKey(r.Kind == 0 ? ProgramKind.App : ProgramKind.Site, r.Name),
                OnTask: r.OnTask,
                Excluded: r.Excluded,
                TimeSpan.FromMilliseconds(r.Ms),
                ProjectId: r.ProjectId,
                ReadScreen: r.ReadScreen,
                Learn: r.Learn))
            .ToList();

    /// <summary>
    /// Every window title recorded with active time, per app (exe name), with that time (marked captures left out),
    /// counted as in <see cref="GetPrograms"/>: what window title rules can match.
    /// </summary>
    public List<TitleUsage> GetTitles(TimeSpan maxCaptureLength) =>
        db.Connection.Query<(string App, string Title, long Ms)>("""
            WITH spans AS (
                SELECT app_id, window_title, kind, pending_delete,
                       min(coalesce(lead(ts_ms) OVER (ORDER BY ts_ms, id) - ts_ms, $max), $max) AS ms
                FROM captures)
            SELECT a.exe_name, s.window_title, sum(s.ms)
            FROM spans s JOIN apps a ON a.id = s.app_id
            WHERE s.kind = 0 AND s.pending_delete = 0 AND s.window_title <> ''
            GROUP BY a.exe_name, s.window_title;
            """, new { max = (long)maxCaptureLength.TotalMilliseconds })
            .Select(r => new TitleUsage(r.App, r.Title, TimeSpan.FromMilliseconds(r.Ms)))
            .ToList();

    public void Dispose() => db.Dispose();

    /// <summary>The start and end of a local day, as <see cref="LoadDay"/> bounds it (<see cref="LocalDay.Range"/>).</summary>
    public (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly day) => LocalDay.Range(day, dayStart);

    /// <summary>UTC milliseconds bounding a local day.</summary>
    private (long Start, long End) DayBounds(DateOnly day)
    {
        var (start, end) = DayRange(day);
        return (start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds());
    }

    // Rows as the queries above return them; Dapper fills these by column name.
    private sealed record CaptureRow
    {
        public long Id { get; init; }
        public long TsMs { get; init; }
        public int UtcOffsetMin { get; init; }
        public CaptureKind Kind { get; init; }
        public string? ExeName { get; init; }
        public string? WindowTitle { get; init; }
        public int? WindowX { get; init; }
        public int? WindowY { get; init; }
        public int? WindowWidth { get; init; }
        public int? WindowHeight { get; init; }
        public bool AppOnTask { get; init; }
        public bool PendingDelete { get; init; }
        public string? Site { get; init; }
        public bool? SiteOnTask { get; init; }
        public long? LayoutId { get; init; }
        public long? AppProjectId { get; init; }
        public long? SiteProjectId { get; init; }
        public bool? TaskMark { get; init; }
    }

    private sealed record SampleRow
    {
        public long TsMs { get; init; }
        public int UtcOffsetMin { get; init; }
        public CaptureKind Kind { get; init; }
        public bool PendingDelete { get; init; }
        public bool OnTask { get; init; }
        public long? ProjectId { get; init; }
        public string? ExeName { get; init; }
        public string? WindowTitle { get; init; }
    }

    private sealed record ProgramRow
    {
        public int Kind { get; init; }
        public required string Name { get; init; }
        public bool OnTask { get; init; }
        public bool Excluded { get; init; }
        public long Ms { get; init; }
        public long? ProjectId { get; init; }
        public bool ReadScreen { get; init; }
        public bool Learn { get; init; }
    }
}
