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
    /// <summary>
    /// Being recorded: the recorder may still add references to it. Video frames are in the segment's staging file,
    /// not encoded yet; an image is in its file already, but may still be shown again for a monitor that hasn't changed.
    /// </summary>
    Staged = 0,

    /// <summary>Finished: video frames are AV1 in the segment's IVF file; an image no longer gets new references.</summary>
    Encoded = 1,
}

/// <summary>How a segment's frames are stored (the number is what the database keeps).</summary>
public enum FrameFormat
{
    /// <summary>AV1 video: up to a segment's worth of frames, in a staging file and then an IVF file.</summary>
    Video = 0,

    /// <summary>One frame, a JPEG image file.</summary>
    Jpeg = 1,

    /// <summary>One frame, a WebP image file.</summary>
    WebP = 2,
}

/// <summary>Writes captures, segments and frame indexes. Used only from the recorder thread.</summary>
public sealed class RecordingStore(WorklapseDatabase db)
{
    // Monitors and layouts are never deleted, so their ids can be remembered (unlike apps and sites).
    private readonly Dictionary<MonitorInfo, long> _monitors = [];
    private readonly Dictionary<string, long> _layouts = [];

    public WorklapseDatabase Database => db;

    /// <summary>
    /// Looks the app up every time rather than caching its id: deleting captures deletes the apps left without
    /// any, so an id remembered from earlier may be gone. Call it inside the capture's transaction.
    /// </summary>
    public long GetOrAddApp(string exeName, string? exePath)
    {
        // exe_path can be NULL, which UNIQUE treats as distinct, so look up before inserting.
        using (var find = db.Command("SELECT id FROM apps WHERE exe_name = $name AND exe_path IS $path;"))
        {
            find.Parameters.AddWithValue("$name", exeName);
            find.Parameters.AddWithValue("$path", (object?)exePath ?? DBNull.Value);
            if (find.ExecuteScalar() is long existing)
                return existing;
        }

        // Apps are classified by name, so another copy of one already marked (another path) is marked too.
        using var insert = db.Command("""
            INSERT INTO apps (exe_name, exe_path, productive)
            VALUES ($name, $path, coalesce((SELECT max(productive) FROM apps WHERE exe_name = $name), 0))
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$name", exeName);
        insert.Parameters.AddWithValue("$path", (object?)exePath ?? DBNull.Value);
        return (long)insert.ExecuteScalar()!;
    }

    /// <summary>The site's id, adding it if new. Not cached, for the same reason as apps.</summary>
    public long GetOrAddSite(string domain)
    {
        using var upsert = db.Command("""
            INSERT INTO sites (domain) VALUES ($domain)
            ON CONFLICT (domain) DO UPDATE SET domain = domain
            RETURNING id;
            """);
        upsert.Parameters.AddWithValue("$domain", domain);
        return (long)upsert.ExecuteScalar()!;
    }

    /// <summary>
    /// Whether the app (any copy with that exe name) or the site is excluded from recording. Looked up every
    /// tick, so a change on the Programs page applies from the next capture.
    /// </summary>
    public bool IsExcluded(string exeName, string? domain)
    {
        using var find = db.Command("""
            SELECT EXISTS (SELECT 1 FROM apps WHERE exe_name = $name AND excluded = 1)
                OR EXISTS (SELECT 1 FROM sites WHERE domain = $domain AND excluded = 1);
            """);
        find.Parameters.AddWithValue("$name", exeName);
        find.Parameters.AddWithValue("$domain", (object?)domain ?? DBNull.Value);
        return (long)find.ExecuteScalar()! != 0;
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

    /// <summary>The id of the layout made of these monitors (attached at the same time), adding it if new.</summary>
    public long GetOrAddLayout(IEnumerable<long> monitorIds)
    {
        var key = string.Join(",", monitorIds.Order());
        if (_layouts.TryGetValue(key, out var id))
            return id;

        using var upsert = db.Command("""
            INSERT INTO layouts (monitors) VALUES ($monitors)
            ON CONFLICT (monitors) DO UPDATE SET monitors = monitors
            RETURNING id;
            """);
        upsert.Parameters.AddWithValue("$monitors", key);
        return _layouts[key] = (long)upsert.ExecuteScalar()!;
    }

    public long InsertCapture(DateTimeOffset timestamp, CaptureKind kind, long? appId, string? title, PixelRect? window, long? siteId = null,
        long? layoutId = null)
    {
        using var insert = db.Command("""
            INSERT INTO captures (ts_ms, utc_offset_min, kind, app_id, window_title, window_x, window_y, window_width, window_height, site_id, layout_id)
            VALUES ($ts, $offset, $kind, $app, $title, $wx, $wy, $ww, $wh, $site, $layout)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$offset", (int)timestamp.Offset.TotalMinutes);
        insert.Parameters.AddWithValue("$kind", (int)kind);
        insert.Parameters.AddWithValue("$app", (object?)appId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$site", (object?)siteId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$layout", (object?)layoutId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insert.Parameters.AddWithValue("$wx", (object?)window?.X ?? DBNull.Value);
        insert.Parameters.AddWithValue("$wy", (object?)window?.Y ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ww", (object?)window?.Width ?? DBNull.Value);
        insert.Parameters.AddWithValue("$wh", (object?)window?.Height ?? DBNull.Value);
        return (long)insert.ExecuteScalar()!;
    }

    public long InsertSegment(long monitorId, string relativePath, int width, int height, int quality, DateTimeOffset created,
        SegmentState state = SegmentState.Encoded, FrameFormat format = FrameFormat.Video)
    {
        using var insert = db.Command("""
            INSERT INTO segments (monitor_id, path, width, height, quality, created_ms, state, format)
            VALUES ($monitor, $path, $w, $h, $q, $created, $state, $format)
            RETURNING id;
            """);
        insert.Parameters.AddWithValue("$state", (int)state);
        insert.Parameters.AddWithValue("$format", (int)format);
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
