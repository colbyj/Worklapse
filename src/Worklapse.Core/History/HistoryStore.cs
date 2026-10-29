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
    public IReadOnlyList<DateOnly> GetDays()
    {
        // Local day of each capture, using the offset recorded with it.
        using var command = db.Command("""
            SELECT DISTINCT date((ts_ms / 1000) + utc_offset_min * 60 - $dayStart, 'unixepoch') FROM captures ORDER BY 1;
            """);
        command.Parameters.AddWithValue("$dayStart", (long)dayStart.ToTimeSpan().TotalSeconds);
        using var reader = command.ExecuteReader();
        var days = new List<DateOnly>();
        while (reader.Read())
            days.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd"));
        return days;
    }

    public DayHistory LoadDay(DateOnly day)
    {
        var (startMs, endMs) = DayBounds(day);

        var frames = new Dictionary<long, List<MonitorFrame>>();
        var monitorIds = new HashSet<long>();
        using (var command = db.Command("""
            SELECT f.capture_id, f.monitor_id, f.segment_id, f.frame_index
            FROM frames f JOIN captures c ON c.id = f.capture_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end;
            """))
        {
            command.Parameters.AddWithValue("$start", startMs);
            command.Parameters.AddWithValue("$end", endMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var captureId = reader.GetInt64(0);
                if (!frames.TryGetValue(captureId, out var list))
                    frames[captureId] = list = [];
                var monitorId = reader.GetInt64(1);
                list.Add(new MonitorFrame(monitorId, new FrameRef(reader.GetInt64(2), reader.GetInt32(3))));
                monitorIds.Add(monitorId);
            }
        }

        // Layouts are shared: captures with the same monitors attached get the same list.
        var layouts = new Dictionary<long, IReadOnlyList<long>>();
        using (var command = db.Command("""
            SELECT id, monitors FROM layouts
            WHERE id IN (SELECT layout_id FROM captures WHERE ts_ms >= $start AND ts_ms < $end);
            """))
        {
            command.Parameters.AddWithValue("$start", startMs);
            command.Parameters.AddWithValue("$end", endMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var ids = reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
                layouts[reader.GetInt64(0)] = ids;
                monitorIds.UnionWith(ids);
            }
        }

        var rules = new TitleRuleSet(new ProjectStore(db).GetTitleRules());
        var captures = new List<CaptureRecord>();
        var unrecorded = new List<int>(); // captures from before layouts were recorded
        using (var command = db.Command("""
            SELECT c.id, c.ts_ms, c.utc_offset_min, c.kind, a.exe_name, c.window_title,
                   c.window_x, c.window_y, c.window_width, c.window_height, coalesce(a.productive, 0), c.pending_delete,
                   s.domain, s.productive, c.layout_id, a.project_id, s.project_id, c.task_mark
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id LEFT JOIN sites s ON s.id = c.site_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end
            ORDER BY c.ts_ms, c.id;
            """))
        {
            command.Parameters.AddWithValue("$start", startMs);
            command.Parameters.AddWithValue("$end", endMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var time = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToOffset(TimeSpan.FromMinutes(reader.GetInt32(2)));
                PixelRect? window = reader.IsDBNull(6) ? null
                    : new PixelRect(reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9));
                IReadOnlyList<long>? layout = reader.IsDBNull(14) ? null : layouts[reader.GetInt64(14)];
                if (layout is null)
                    unrecorded.Add(captures.Count);
                var appName = reader.IsDBNull(4) ? null : reader.GetString(4);
                var title = reader.IsDBNull(5) ? null : reader.GetString(5);
                captures.Add(new CaptureRecord(
                    id,
                    time,
                    (CaptureKind)reader.GetInt32(3),
                    AppName: appName,
                    Site: reader.IsDBNull(12) ? null : reader.GetString(12),
                    WindowTitle: title,
                    window,
                    AppOnTask: reader.GetInt64(10) != 0,
                    SiteOnTask: reader.IsDBNull(13) ? null : reader.GetInt64(13) != 0,
                    PendingDelete: reader.GetInt64(11) != 0,
                    frames.TryGetValue(id, out var list) ? list : [],
                    MonitorIds: layout ?? [],
                    AppProjectId: reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    SiteProjectId: reader.IsDBNull(16) ? null : reader.GetInt64(16),
                    RuleProjectId: rules.ProjectOf(appName, title),
                    TaskMark: reader.IsDBNull(17) ? null : reader.GetInt64(17) != 0));
            }
        }

        var monitors = new List<MonitorRecord>();
        if (monitorIds.Count > 0)
        {
            using var command = db.Command($"SELECT id, device_name, x, y, width, height, is_primary FROM monitors WHERE id IN ({string.Join(",", monitorIds)}) ORDER BY id;");
            using var reader = command.ExecuteReader();
            while (reader.Read())
                monitors.Add(new MonitorRecord(reader.GetInt64(0), reader.GetString(1),
                    new PixelRect(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)), reader.GetInt64(6) != 0));
        }

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
        using var command = db.Command($"""
            SELECT c.ts_ms, c.utc_offset_min, c.kind, c.pending_delete,
                   coalesce(c.task_mark, s.productive, a.productive, 0), coalesce(s.project_id, a.project_id),
                   {(rules.IsEmpty ? "NULL, NULL" : "a.exe_name, c.window_title")}
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id LEFT JOIN sites s ON s.id = c.site_id
            WHERE c.ts_ms >= $start AND c.ts_ms < $end
            ORDER BY c.ts_ms, c.id;
            """);
        command.Parameters.AddWithValue("$start", startMs);
        command.Parameters.AddWithValue("$end", endMs);
        using var reader = command.ExecuteReader();
        var samples = new List<ActivitySample>();
        while (reader.Read())
            samples.Add(new ActivitySample(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToOffset(TimeSpan.FromMinutes(reader.GetInt32(1))),
                (CaptureKind)reader.GetInt32(2),
                OnTask: reader.GetInt64(4) != 0,
                PendingDelete: reader.GetInt64(3) != 0,
                LinkedProjectId: rules.ProjectOf(reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))
                    ?? (reader.IsDBNull(5) ? null : reader.GetInt64(5))));
        return samples;
    }

    /// <summary>Marks (or unmarks) every capture with start ≤ time &lt; end for deletion.</summary>
    public int SetPendingDelete(DateTimeOffset start, DateTimeOffset end, bool pending)
    {
        using var command = db.Command("UPDATE captures SET pending_delete = $pending WHERE ts_ms >= $start AND ts_ms < $end;");
        command.Parameters.AddWithValue("$pending", pending ? 1 : 0);
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks every capture with start ≤ time &lt; end on-task (true) or off-task (false), over what its app or site says,
    /// or leaves it to them again (null).
    /// </summary>
    public int SetTaskMark(DateTimeOffset start, DateTimeOffset end, bool? onTask)
    {
        using var command = db.Command("UPDATE captures SET task_mark = $mark WHERE ts_ms >= $start AND ts_ms < $end;");
        command.Parameters.AddWithValue("$mark", onTask is { } on ? on ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }

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

        bool staged;
        using (var touched = db.Command("""
            SELECT EXISTS (
                SELECT 1 FROM frames f JOIN captures c ON c.id = f.capture_id JOIN segments s ON s.id = f.segment_id
                WHERE c.pending_delete = 1 AND c.ts_ms >= $start AND c.ts_ms < $end AND s.state = $staged);
            """))
        {
            touched.Parameters.AddWithValue("$start", startMs);
            touched.Parameters.AddWithValue("$end", endMs);
            touched.Parameters.AddWithValue("$staged", (int)SegmentState.Staged);
            staged = (long)touched.ExecuteScalar()! != 0;
        }

        using (var queue = db.Command("""
            INSERT INTO reclaim_queue (segment_id)
            SELECT DISTINCT f.segment_id FROM frames f JOIN captures c ON c.id = f.capture_id
            WHERE c.pending_delete = 1 AND c.ts_ms >= $start AND c.ts_ms < $end
            ON CONFLICT (segment_id) DO UPDATE SET generation = generation + 1;
            """))
        {
            queue.Parameters.AddWithValue("$start", startMs);
            queue.Parameters.AddWithValue("$end", endMs);
            queue.ExecuteNonQuery();
        }

        using var command = db.Command("DELETE FROM captures WHERE pending_delete = 1 AND ts_ms >= $start AND ts_ms < $end;");
        command.Parameters.AddWithValue("$start", startMs);
        command.Parameters.AddWithValue("$end", endMs);
        var deleted = command.ExecuteNonQuery();

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
    public void SetOnTask(ProgramKey program, bool onTask)
    {
        using var command = db.Command(program.Kind == ProgramKind.App
            ? "UPDATE apps SET productive = $productive WHERE exe_name = $name;"
            : "UPDATE sites SET productive = $productive WHERE domain = $name;");
        command.Parameters.AddWithValue("$productive", onTask ? 1 : 0);
        command.Parameters.AddWithValue("$name", program.Name);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Excludes an app (every copy with that exe name) or a site from recording, or includes it again. The
    /// recorder looks it up every tick; history already recorded stays as it is.
    /// </summary>
    public void SetExcluded(ProgramKey program, bool excluded)
    {
        using var command = db.Command(program.Kind == ProgramKind.App
            ? "UPDATE apps SET excluded = $excluded WHERE exe_name = $name;"
            : "UPDATE sites SET excluded = $excluded WHERE domain = $name;");
        command.Parameters.AddWithValue("$excluded", excluded ? 1 : 0);
        command.Parameters.AddWithValue("$name", program.Name);
        command.ExecuteNonQuery();
    }

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

    private void SetFlag(ProgramKey program, string column, bool on)
    {
        using var command = db.Command(program.Kind == ProgramKind.App
            ? $"UPDATE apps SET {column} = $on WHERE exe_name = $name;"
            : $"UPDATE sites SET {column} = $on WHERE domain = $name;");
        command.Parameters.AddWithValue("$on", on ? 1 : 0);
        command.Parameters.AddWithValue("$name", program.Name);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Links an app (every copy with that exe name) or a site to a project, or unlinks it (null). Its active time is
    /// marked with the project wherever no range set by hand says otherwise; links are looked up when a day is read,
    /// so this applies to all history at once.
    /// </summary>
    public void SetProject(ProgramKey program, long? projectId)
    {
        using var command = db.Command(program.Kind == ProgramKind.App
            ? "UPDATE apps SET project_id = $project WHERE exe_name = $name;"
            : "UPDATE sites SET project_id = $project WHERE domain = $name;");
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", program.Name);
        command.ExecuteNonQuery();

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
    public List<ProgramUsage> GetPrograms(TimeSpan maxCaptureLength)
    {
        using var command = db.Command("""
            WITH spans AS (
                SELECT app_id, site_id,
                       CASE WHEN kind = 0 AND pending_delete = 0
                            THEN min(coalesce(lead(ts_ms) OVER (ORDER BY ts_ms, id) - ts_ms, $max), $max) ELSE 0 END AS ms
                FROM captures)
            SELECT 0, a.exe_name, max(a.productive), max(a.excluded), coalesce(sum(s.ms), 0), max(a.project_id), min(a.read_screen), min(a.learn)
            FROM apps a LEFT JOIN spans s ON s.app_id = a.id
            GROUP BY a.exe_name
            UNION ALL
            SELECT 1, t.domain, t.productive, t.excluded, coalesce(sum(s.ms), 0), t.project_id, t.read_screen, t.learn
            FROM sites t LEFT JOIN spans s ON s.site_id = t.id
            GROUP BY t.id;
            """);
        command.Parameters.AddWithValue("$max", (long)maxCaptureLength.TotalMilliseconds);
        using var reader = command.ExecuteReader();
        var programs = new List<ProgramUsage>();
        while (reader.Read())
            programs.Add(new ProgramUsage(
                new ProgramKey(reader.GetInt32(0) == 0 ? ProgramKind.App : ProgramKind.Site, reader.GetString(1)),
                OnTask: reader.GetInt64(2) != 0,
                Excluded: reader.GetInt64(3) != 0,
                TimeSpan.FromMilliseconds(reader.GetInt64(4)),
                ProjectId: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ReadScreen: reader.GetInt64(6) != 0,
                Learn: reader.GetInt64(7) != 0));
        return programs;
    }

    /// <summary>
    /// Every window title recorded with active time, per app (exe name), with that time (marked captures left out),
    /// counted as in <see cref="GetPrograms"/>: what window title rules can match.
    /// </summary>
    public List<TitleUsage> GetTitles(TimeSpan maxCaptureLength)
    {
        using var command = db.Command("""
            WITH spans AS (
                SELECT app_id, window_title, kind, pending_delete,
                       min(coalesce(lead(ts_ms) OVER (ORDER BY ts_ms, id) - ts_ms, $max), $max) AS ms
                FROM captures)
            SELECT a.exe_name, s.window_title, sum(s.ms)
            FROM spans s JOIN apps a ON a.id = s.app_id
            WHERE s.kind = 0 AND s.pending_delete = 0 AND s.window_title <> ''
            GROUP BY a.exe_name, s.window_title;
            """);
        command.Parameters.AddWithValue("$max", (long)maxCaptureLength.TotalMilliseconds);
        using var reader = command.ExecuteReader();
        var titles = new List<TitleUsage>();
        while (reader.Read())
            titles.Add(new TitleUsage(reader.GetString(0), reader.GetString(1), TimeSpan.FromMilliseconds(reader.GetInt64(2))));
        return titles;
    }

    public void Dispose() => db.Dispose();

    /// <summary>The start and end of a local day, as <see cref="LoadDay"/> bounds it (<see cref="LocalDay.Range"/>).</summary>
    public (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly day) => LocalDay.Range(day, dayStart);

    /// <summary>UTC milliseconds bounding a local day.</summary>
    private (long Start, long End) DayBounds(DateOnly day)
    {
        var (start, end) = DayRange(day);
        return (start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds());
    }
}
