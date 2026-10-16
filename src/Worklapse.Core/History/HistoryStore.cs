using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.History;

/// <summary>
/// Reads recorded days and edits them (marking ranges for deletion). Uses its own connection; meant for
/// the UI thread, alongside the recorder writing through another connection.
/// </summary>
public sealed class HistoryStore(WorklapseDatabase db) : IDisposable
{
    public WorklapseDatabase Database => db;

    /// <summary>Local calendar days that have at least one capture, oldest first.</summary>
    public IReadOnlyList<DateOnly> GetDays()
    {
        // Local day of each capture, using the offset recorded with it.
        using var command = db.Command("""
            SELECT DISTINCT date((ts_ms / 1000) + utc_offset_min * 60, 'unixepoch') FROM captures ORDER BY 1;
            """);
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

        var captures = new List<CaptureRecord>();
        using (var command = db.Command("""
            SELECT c.id, c.ts_ms, c.utc_offset_min, c.kind, a.exe_name, c.window_title,
                   c.window_x, c.window_y, c.window_width, c.window_height, coalesce(a.productive, 0), c.pending_delete
            FROM captures c LEFT JOIN apps a ON a.id = c.app_id
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
                captures.Add(new CaptureRecord(
                    id,
                    time,
                    (CaptureKind)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    window,
                    reader.GetInt64(10) != 0,
                    reader.GetInt64(11) != 0,
                    frames.TryGetValue(id, out var list) ? list : []));
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

        return new DayHistory(day, captures, monitors);
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
    /// Deletes the captures marked on <paramref name="day"/> (their frame references go with them).
    /// The video stays on disk until the segments are rebuilt; returns how many captures were deleted.
    /// </summary>
    public int DeleteMarked(DateOnly day)
    {
        var (startMs, endMs) = DayBounds(day);
        using var command = db.Command("DELETE FROM captures WHERE pending_delete = 1 AND ts_ms >= $start AND ts_ms < $end;");
        command.Parameters.AddWithValue("$start", startMs);
        command.Parameters.AddWithValue("$end", endMs);
        return command.ExecuteNonQuery();
    }

    public void Dispose() => db.Dispose();

    /// <summary>
    /// UTC milliseconds bounding a local day. Captures store their own offset, but the day itself is the
    /// local day as this computer sees it now (a day recorded in another time zone can straddle two).
    /// </summary>
    private static (long Start, long End) DayBounds(DateOnly day)
    {
        var zone = TimeZoneInfo.Local;
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (new DateTimeOffset(start, zone.GetUtcOffset(start)).ToUnixTimeMilliseconds(),
                new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUnixTimeMilliseconds());
    }
}
