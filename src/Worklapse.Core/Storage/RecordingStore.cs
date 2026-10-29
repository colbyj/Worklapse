using Dapper;
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
        if (db.Connection.ExecuteScalar<long?>("SELECT id FROM apps WHERE exe_name = $name AND exe_path IS $path;",
                new { name = exeName, path = exePath }) is { } existing)
            return existing;

        // Apps are classified and linked by name, so another copy of one already marked or linked (another path) is
        // marked and linked too, and opted out of suggestions alike.
        return db.Connection.ExecuteScalar<long>("""
            INSERT INTO apps (exe_name, exe_path, productive, project_id, read_screen, learn)
            VALUES ($name, $path, coalesce((SELECT max(productive) FROM apps WHERE exe_name = $name), 0),
                    (SELECT max(project_id) FROM apps WHERE exe_name = $name),
                    coalesce((SELECT min(read_screen) FROM apps WHERE exe_name = $name), 1),
                    coalesce((SELECT min(learn) FROM apps WHERE exe_name = $name), 1))
            RETURNING id;
            """,
            new { name = exeName, path = exePath });
    }

    /// <summary>The site's id, adding it if new. Not cached, for the same reason as apps.</summary>
    public long GetOrAddSite(string domain) =>
        db.Connection.ExecuteScalar<long>("""
            INSERT INTO sites (domain) VALUES ($domain)
            ON CONFLICT (domain) DO UPDATE SET domain = domain
            RETURNING id;
            """,
            new { domain });

    /// <summary>
    /// Whether the app (any copy with that exe name) or the site is excluded from recording. Looked up every
    /// tick, so a change on the Applications page applies from the next capture.
    /// </summary>
    public bool IsExcluded(string exeName, string? domain) =>
        db.Connection.ExecuteScalar<bool>("""
            SELECT EXISTS (SELECT 1 FROM apps WHERE exe_name = $name AND excluded = 1)
                OR EXISTS (SELECT 1 FROM sites WHERE domain = $domain AND excluded = 1);
            """,
            new { name = exeName, domain });

    public long GetOrAddMonitor(MonitorInfo monitor)
    {
        if (_monitors.TryGetValue(monitor, out var id))
            return id;

        return _monitors[monitor] = db.Connection.ExecuteScalar<long>("""
            INSERT INTO monitors (device_name, x, y, width, height, is_primary)
            VALUES ($name, $x, $y, $w, $h, $primary)
            ON CONFLICT DO UPDATE SET device_name = excluded.device_name
            RETURNING id;
            """,
            new
            {
                name = monitor.DeviceName,
                x = monitor.Bounds.X,
                y = monitor.Bounds.Y,
                w = monitor.Bounds.Width,
                h = monitor.Bounds.Height,
                primary = monitor.IsPrimary,
            });
    }

    /// <summary>The id of the layout made of these monitors (attached at the same time), adding it if new.</summary>
    public long GetOrAddLayout(IEnumerable<long> monitorIds)
    {
        var key = string.Join(",", monitorIds.Order());
        if (_layouts.TryGetValue(key, out var id))
            return id;

        return _layouts[key] = db.Connection.ExecuteScalar<long>("""
            INSERT INTO layouts (monitors) VALUES ($monitors)
            ON CONFLICT (monitors) DO UPDATE SET monitors = monitors
            RETURNING id;
            """,
            new { monitors = key });
    }

    public long InsertCapture(DateTimeOffset timestamp, CaptureKind kind, long? appId, string? title, PixelRect? window, long? siteId = null,
        long? layoutId = null) =>
        db.Connection.ExecuteScalar<long>("""
            INSERT INTO captures (ts_ms, utc_offset_min, kind, app_id, window_title, window_x, window_y, window_width, window_height, site_id, layout_id)
            VALUES ($ts, $offset, $kind, $app, $title, $wx, $wy, $ww, $wh, $site, $layout)
            RETURNING id;
            """,
            new
            {
                ts = timestamp.ToUnixTimeMilliseconds(),
                offset = (int)timestamp.Offset.TotalMinutes,
                kind = (int)kind,
                app = appId,
                site = siteId,
                layout = layoutId,
                title,
                wx = window?.X,
                wy = window?.Y,
                ww = window?.Width,
                wh = window?.Height,
            });

    public long InsertSegment(long monitorId, string relativePath, int width, int height, int quality, DateTimeOffset created,
        SegmentState state = SegmentState.Encoded, FrameFormat format = FrameFormat.Video) =>
        db.Connection.ExecuteScalar<long>("""
            INSERT INTO segments (monitor_id, path, width, height, quality, created_ms, state, format)
            VALUES ($monitor, $path, $w, $h, $q, $created, $state, $format)
            RETURNING id;
            """,
            new
            {
                state = (int)state,
                format = (int)format,
                monitor = monitorId,
                path = relativePath,
                w = width,
                h = height,
                q = quality,
                created = created.ToUnixTimeMilliseconds(),
            });

    public void InsertSegmentFrame(long segmentId, int frameIndex, long fileOffset, int length, bool isKey) =>
        db.Connection.Execute("""
            INSERT INTO segment_frames (segment_id, frame_index, file_offset, length, is_key)
            VALUES ($segment, $index, $offset, $length, $key);
            """,
            new { segment = segmentId, index = frameIndex, offset = fileOffset, length, key = isKey });

    public void InsertFrame(long captureId, long monitorId, FrameRef frame) =>
        db.Connection.Execute("""
            INSERT INTO frames (capture_id, monitor_id, segment_id, frame_index)
            VALUES ($capture, $monitor, $segment, $index);
            """,
            new { capture = captureId, monitor = monitorId, segment = frame.SegmentId, index = frame.FrameIndex });

    public SqliteTransaction BeginTransaction() => db.BeginTransaction();
}
