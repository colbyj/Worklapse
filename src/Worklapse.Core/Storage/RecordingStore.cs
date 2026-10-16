using Microsoft.Data.Sqlite;
using Worklapse.Core.Platform;

namespace Worklapse.Core.Storage;

public enum CaptureKind
{
    Active = 0,
    Idle = 1,
}

public readonly record struct FrameRef(long SegmentId, int FrameIndex);

public enum SegmentState
{
    /// <summary>Frames are in the segment's staging file; not yet encoded.</summary>
    Staged = 0,

    /// <summary>Frames are AV1 in the segment's IVF file.</summary>
    Encoded = 1,
}

/// <summary>Writes captures, segments and frame indexes. Used only from the recorder thread.</summary>
public sealed class RecordingStore(WorklapseDatabase db)
{
    private readonly Dictionary<(string Name, string? Path), long> _apps = [];
    private readonly Dictionary<MonitorInfo, long> _monitors = [];

    public WorklapseDatabase Database => db;

    public long GetOrAddApp(string exeName, string? exePath)
    {
        if (_apps.TryGetValue((exeName, exePath), out var id))
            return id;

        // exe_path can be NULL, which UNIQUE treats as distinct, so look up before inserting.
        using (var find = db.Command("SELECT id FROM apps WHERE exe_name = $name AND exe_path IS $path;"))
        {
            find.Parameters.AddWithValue("$name", exeName);
            find.Parameters.AddWithValue("$path", (object?)exePath ?? DBNull.Value);
            if (find.ExecuteScalar() is long existing)
                return _apps[(exeName, exePath)] = existing;
        }

        using var insert = db.Command("INSERT INTO apps (exe_name, exe_path) VALUES ($name, $path) RETURNING id;");
        insert.Parameters.AddWithValue("$name", exeName);
        insert.Parameters.AddWithValue("$path", (object?)exePath ?? DBNull.Value);
        return _apps[(exeName, exePath)] = (long)insert.ExecuteScalar()!;
    }

    public long GetOrAddMonitor(MonitorInfo monitor)
    {
        if (_monitors.TryGetValue(monitor, out var id))
            return id;

        using var upsert = db.Command("""
            INSERT INTO monitors (device_name, x, y, width, height, is_primary)
            VALUES ($name, $x, $y, $w, $h, $primary)
            ON CONFLICT DO UPDATE SET device_name = excluded.device_name
            RETURNING id;
            """);
        upsert.Parameters.AddWithValue("$name", monitor.DeviceName);
        upsert.Parameters.AddWithValue("$x", monitor.Bounds.X);
        upsert.Parameters.AddWithValue("$y", monitor.Bounds.Y);
        upsert.Parameters.AddWithValue("$w", monitor.Bounds.Width);
        upsert.Parameters.AddWithValue("$h", monitor.Bounds.Height);
        upsert.Parameters.AddWithValue("$primary", monitor.IsPrimary ? 1 : 0);
        return _monitors[monitor] = (long)upsert.ExecuteScalar()!;
    }

    public long InsertCapture(DateTimeOffset timestamp, CaptureKind kind, long? appId, string? title, PixelRect? window)
    {
        using var insert = db.Command("""
            INSERT INTO captures (ts_ms, utc_offset_min, kind, app_id, window_title, window_x, window_y, window_width, window_height)
            VALUES ($ts, $offset, $kind, $app, $title, $wx, $wy, $ww, $wh)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$offset", (int)timestamp.Offset.TotalMinutes);
        insert.Parameters.AddWithValue("$kind", (int)kind);
        insert.Parameters.AddWithValue("$app", (object?)appId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insert.Parameters.AddWithValue("$wx", (object?)window?.X ?? DBNull.Value);
        insert.Parameters.AddWithValue("$wy", (object?)window?.Y ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ww", (object?)window?.Width ?? DBNull.Value);
        insert.Parameters.AddWithValue("$wh", (object?)window?.Height ?? DBNull.Value);
        return (long)insert.ExecuteScalar()!;
    }

    public long InsertSegment(long monitorId, string relativePath, int width, int height, int quality, DateTimeOffset created,
        SegmentState state = SegmentState.Encoded)
    {
        using var insert = db.Command("""
            INSERT INTO segments (monitor_id, path, width, height, quality, created_ms, state)
            VALUES ($monitor, $path, $w, $h, $q, $created, $state)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$state", (int)state);
        insert.Parameters.AddWithValue("$monitor", monitorId);
        insert.Parameters.AddWithValue("$path", relativePath);
        insert.Parameters.AddWithValue("$w", width);
        insert.Parameters.AddWithValue("$h", height);
        insert.Parameters.AddWithValue("$q", quality);
        insert.Parameters.AddWithValue("$created", created.ToUnixTimeMilliseconds());
        return (long)insert.ExecuteScalar()!;
    }

    public void InsertSegmentFrame(long segmentId, int frameIndex, long fileOffset, int length, bool isKey)
    {
        using var insert = db.Command("""
            INSERT INTO segment_frames (segment_id, frame_index, file_offset, length, is_key)
            VALUES ($segment, $index, $offset, $length, $key);
            """);
        insert.Parameters.AddWithValue("$segment", segmentId);
        insert.Parameters.AddWithValue("$index", frameIndex);
        insert.Parameters.AddWithValue("$offset", fileOffset);
        insert.Parameters.AddWithValue("$length", length);
        insert.Parameters.AddWithValue("$key", isKey ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    public void InsertFrame(long captureId, long monitorId, FrameRef frame)
    {
        using var insert = db.Command("""
            INSERT INTO frames (capture_id, monitor_id, segment_id, frame_index)
            VALUES ($capture, $monitor, $segment, $index);
            """);
        insert.Parameters.AddWithValue("$capture", captureId);
        insert.Parameters.AddWithValue("$monitor", monitorId);
        insert.Parameters.AddWithValue("$segment", frame.SegmentId);
        insert.Parameters.AddWithValue("$index", frame.FrameIndex);
        insert.ExecuteNonQuery();
    }

    public SqliteTransaction BeginTransaction() => db.BeginTransaction();
}
