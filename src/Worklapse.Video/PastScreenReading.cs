using System.Runtime.ExceptionServices;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.History;
using Worklapse.Core.Import;
using Worklapse.Core.Platform;
using Worklapse.Core.Projects;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Video;

/// <summary>
/// Reads the screens of captures recorded before reading was on, or imported, for suggestions: the active window of each
/// capture without words, from its stored frame (decoded as Day review decodes it), on as many low-priority threads as asked.
/// First the segments holding anything training learns from (a video whole, since its frames decode in order anyway; a
/// picture is its own segment), then the rest; oldest first, each segment's frames in order. Captures showing the same
/// frame and window are read once. Slow (a picture takes ~100 ms on one thread), so it reports progress and can be cancelled; what it
/// has read stays.
/// </summary>
public static class PastScreenReading
{
    /// <summary>
    /// Reads every active capture from <paramref name="from"/> up to <paramref name="to"/> that has a frame and no words yet,
    /// where allowed, on <paramref name="threads"/> threads (each with its own decoder and OCR engine).
    /// </summary>
    /// <param name="dayStart">When a day starts (settings), for finding the days training learns from.</param>
    /// <returns>How many captures got words.</returns>
    public static int Run(string dataFolder, IScreenTextReader reader, DateTimeOffset from, DateTimeOffset to, TimeOnly dayStart,
        int threads, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        List<Unread[]> segments;
        using (var history = new HistoryStore(WorklapseDatabase.Open(dataFolder), dayStart))
            segments = InReadingOrder(FindUnread(history.Database, from, to), Training(history));
        var total = segments.Sum(s => s.Length);
        progress?.Report((0, total));

        // A thread failing stops the others, and its exception is the run's.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var run = new RunState();
        var next = -1;
        Exception? failure = null;
        var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(n => new Thread(() =>
        {
            try
            {
                using var db = WorklapseDatabase.Open(dataFolder);
                using var frames = new FrameProvider(dataFolder, cacheBudgetBytes: 0); // read once, in order: nothing to cache
                var store = new ScreenWordStore(db);
                int i;
                while ((i = Interlocked.Increment(ref next)) < segments.Count)
                {
                    var kept = ReadSegment(segments[i], frames, store, reader, run, stop.Token);
                    run.Finished(segments[i].Length, kept, total, progress);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
            }
        })
        { Name = $"Worklapse past screen reading {n + 1}", IsBackground = true, Priority = ThreadPriority.Lowest }).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        cancel.ThrowIfCancellationRequested();
        return run.Kept;
    }

    /// <summary>Reads one segment's captures, in frame order.</summary>
    /// <returns>How many got words.</returns>
    private static int ReadSegment(Unread[] captures, FrameProvider frames, ScreenWordStore store, IScreenTextReader reader,
        RunState run, CancellationToken cancel)
    {
        var kept = 0;
        (FrameRef Frame, PixelRect Window, string[] Words)? last = null;
        foreach (var capture in captures)
        {
            cancel.ThrowIfCancellationRequested();
            if (!store.MayRead(capture.Id))
                continue;

            string[] words;
            if (last is { } previous && previous.Frame == capture.Frame && previous.Window == capture.Window)
                words = previous.Words;
            else
            {
                words = Read(frames, reader, capture, run);
                last = (capture.Frame, capture.Window, words);
            }
            if (store.Set(capture.Id, words))
                kept++;
        }
        return kept;
    }

    private static string[] Read(FrameProvider frames, IScreenTextReader reader, Unread capture, RunState run)
    {
        DecodedFrame frame;
        try
        {
            frame = frames.Get(capture.Frame);
        }
        catch (Exception ex) when (ex is FrameUnavailableException or IOException or InvalidDataException or InvalidOperationException)
        {
            // Its file is gone, moved (an imported picture) or damaged: that capture goes without words, the rest are read.
            if (run.FirstUnreadable())
                Log.Error($"Reading past screens: frame {capture.Frame.FrameIndex} of segment {capture.Frame.SegmentId} can't be decoded; such captures are skipped.", ex);
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
            if (run.FirstFailure())
                Log.Error("Reading past screens: reading the screen failed; captures where it fails go without screen words.", ex);
            return [];
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>
    /// The captures in the order they're read: grouped by segment, those with a <paramref name="training"/> capture first,
    /// then oldest segment first (as <paramref name="oldestFirst"/> has them), each segment's in frame order.
    /// </summary>
    private static List<Unread[]> InReadingOrder(List<Unread> oldestFirst, HashSet<long> training) =>
        oldestFirst.GroupBy(c => c.Frame.SegmentId)
            .Select(s => s.OrderBy(c => c.Frame.FrameIndex).ToArray())
            .OrderBy(s => s.Any(c => training.Contains(c.Id)) ? 0 : 1) // stable: oldest first within each
            .ToList();

    /// <summary>
    /// The captures training learns from: those of the labelled days with a project set by hand (or none), a link or a
    /// title rule (<see cref="TrainingSet.Assignments"/>).
    /// </summary>
    private static HashSet<long> Training(HistoryStore history)
    {
        var projects = new ProjectStore(history.Database);
        var training = new HashSet<long>();
        foreach (var day in SuggestionEngine.LabelledDays(history))
        {
            var (start, end) = history.DayRange(day);
            var captures = history.LoadDay(day).Captures;
            var assignments = TrainingSet.Assignments(captures, projects.GetSpans(start, end));
            for (var i = 0; i < captures.Count; i++)
            {
                if (assignments[i].IsAssigned)
                    training.Add(captures[i].Id);
            }
        }
        return training;
    }

    /// <summary>What the reading threads share: the counts, and what was logged already (each kind once per run).</summary>
    private sealed class RunState
    {
        private readonly Lock _gate = new();
        private int _done;
        private int _reportedUnreadable;
        private int _reportedFailure;

        public int Kept { get; private set; }

        /// <summary>Counts a segment read. Reported under the lock, so progress never goes back.</summary>
        public void Finished(int captures, int kept, int total, IProgress<(int Done, int Total)>? progress)
        {
            lock (_gate)
            {
                _done += captures;
                Kept += kept;
                progress?.Report((_done, total));
            }
        }

        /// <summary>True the first time a frame can't be decoded.</summary>
        public bool FirstUnreadable() => Interlocked.Exchange(ref _reportedUnreadable, 1) == 0;

        /// <summary>True the first time the reader fails.</summary>
        public bool FirstFailure() => Interlocked.Exchange(ref _reportedFailure, 1) == 0;
    }

    /// <param name="Window">The active window on the frame, in its pixels.</param>
    private readonly record struct Unread(long Id, FrameRef Frame, PixelRect Window);

    /// <summary>
    /// The active captures from <paramref name="from"/> up to <paramref name="to"/> without words, oldest first, each with the frame of the monitor
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
            ORDER BY c.ts_ms, c.id;
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
