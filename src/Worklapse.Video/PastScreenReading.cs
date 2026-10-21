using Worklapse.Core.Diagnostics;
using Worklapse.Core.Import;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Video;

/// <summary>
/// Reads the screens of captures recorded before reading was on, or imported, for suggestions: the active window of each
/// capture without words, newest first, from its stored frame (decoded as Day review decodes it). Captures showing the same
/// frame and window are read once. Slow (a picture takes ~80 ms), so it reports progress and can be cancelled; what it has
/// read stays.
/// </summary>
public static class PastScreenReading
{
    /// <summary>Reads every active capture from <paramref name="from"/> up to <paramref name="to"/> that has a frame and no words yet, where allowed.</summary>
    /// <returns>How many captures got words.</returns>
    public static int Run(string dataFolder, IScreenTextReader reader, DateTimeOffset from, DateTimeOffset to,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        using var frames = new FrameProvider(dataFolder);
        var store = new ScreenWordStore(db);
        var captures = FindUnread(db, from, to);
        var kept = 0;
        var (reportedUnreadable, reportedFailure) = (false, false);
        (FrameRef Frame, PixelRect Window, string[] Words)? last = null;
        for (var i = 0; i < captures.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, captures.Count));
            var capture = captures[i];
            if (!store.MayRead(capture.Id))
                continue;

            string[] words;
            if (last is { } previous && previous.Frame == capture.Frame && previous.Window == capture.Window)
                words = previous.Words;
            else
            {
                words = Read(frames, reader, capture, ref reportedUnreadable, ref reportedFailure);
                last = (capture.Frame, capture.Window, words);
            }
            if (store.Set(capture.Id, words))
                kept++;
        }
        progress?.Report((captures.Count, captures.Count));
        return kept;
    }

    /// <param name="reportedUnreadable">Whether an undecodable frame was logged already (once per run).</param>
    /// <param name="reportedFailure">Whether the reader failing was logged already (once per run).</param>
    private static string[] Read(FrameProvider frames, IScreenTextReader reader, Unread capture, ref bool reportedUnreadable,
        ref bool reportedFailure)
    {
        DecodedFrame frame;
        try
        {
            frame = frames.Get(capture.Frame);
        }
        catch (Exception ex) when (ex is FrameUnavailableException or IOException or InvalidDataException or InvalidOperationException)
        {
            // Its file is gone, moved (an imported picture) or damaged: that capture goes without words, the rest are read.
            if (!reportedUnreadable)
                Log.Error($"Reading past screens: frame {capture.Frame.FrameIndex} of segment {capture.Frame.SegmentId} can't be decoded; such captures are skipped.", ex);
            reportedUnreadable = true;
            return [];
        }
        try
        {
            if (ScreenReadingQueue.CropLuma(frame.I420, frame.Width, frame.Height, capture.Window) is not { } crop)
                return [];
            return ScreenWords.Of(reader.Read(crop.Gray, crop.Width, crop.Height));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // As on the recorder's queue: that capture goes without words, the rest are still tried.
            if (!reportedFailure)
                Log.Error("Reading past screens: reading the screen failed; captures where it fails go without screen words.", ex);
            reportedFailure = true;
            return [];
        }
        finally
        {
            frame.Release();
        }
    }

    /// <param name="Window">The active window on the frame, in its pixels.</param>
    private readonly record struct Unread(long Id, FrameRef Frame, PixelRect Window);

    /// <summary>
    /// The active captures from <paramref name="from"/> up to <paramref name="to"/> without words, newest first, each with the frame of the monitor
    /// showing most of its window and the window on that frame. An imported TimeSnapper picture is the window itself.
    /// </summary>
    private static List<Unread> FindUnread(WorklapseDatabase db, DateTimeOffset from, DateTimeOffset to)
    {
        using var command = db.Command("""
            SELECT c.id, f.segment_id, f.frame_index, s.width, s.height, m.device_name, m.x, m.y,
                   c.window_x, c.window_y, c.window_width, c.window_height
            FROM captures c
            JOIN frames f ON f.capture_id = c.id
            JOIN segments s ON s.id = f.segment_id
            JOIN monitors m ON m.id = f.monitor_id
            WHERE c.kind = $active AND c.pending_delete = 0 AND c.ts_ms >= $from AND c.ts_ms < $to
              AND NOT EXISTS (SELECT 1 FROM screen_words w WHERE w.capture_id = c.id)
            ORDER BY c.ts_ms DESC, c.id;
            """);
        command.Parameters.AddWithValue("$active", (int)CaptureKind.Active);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();

        var captures = new List<Unread>();
        var best = (Id: -1L, Area: -1L);
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var frame = new FrameRef(reader.GetInt64(1), reader.GetInt32(2));
            var picture = new PixelRect(0, 0, reader.GetInt32(3), reader.GetInt32(4));
            PixelRect window;
            if (reader.GetString(5) == TimeSnapperImport.MonitorName)
                window = picture;
            else if (reader.IsDBNull(8))
                continue; // no window recorded: nothing to read
            else
                window = new PixelRect(reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11))
                    .Offset(-reader.GetInt32(6), -reader.GetInt32(7)).Intersect(picture);

            // One row per monitor captured: keep the one showing the most of the window.
            var area = (long)window.Width * window.Height;
            if (area <= 0)
                continue;
            if (best.Id == id)
            {
                if (area <= best.Area)
                    continue;
                captures[^1] = new Unread(id, frame, window);
            }
            else
                captures.Add(new Unread(id, frame, window));
            best = (id, area);
        }
        return captures;
    }
}
