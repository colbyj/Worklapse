using System.Collections.Concurrent;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// Encodes finished staged segments to AV1 on a low-priority background thread, one at a time, so only
/// one encoder's memory is in use and only while it works. Staged segments left over from an earlier run
/// (a crash, or quitting before encoding finished) are queued when it starts. An image needs no encoding: it is
/// marked finished (or deleted, if its capture was deleted while the recorder could still show it).
/// <para>
/// The same thread removes deleted captures' frames from disk (see <see cref="SpaceReclaimer"/>), one segment
/// per job between encodes: on <see cref="RequestReclaim"/>, and when it starts, for deletes made before a
/// crash or quit.
/// </para>
/// </summary>
public sealed class BatchEncoder : IDisposable
{
    /// <summary>Threads per encoder. Few, so a batch doesn't compete much with what the user is doing.</summary>
    internal const int EncoderThreads = 2;

    /// <summary>How soon to try again when a file to delete was open in another program.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly string _dataFolder;
    private readonly Action<string>? _rebuildStepHook;
    private readonly BlockingCollection<Job> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly Timer _retry;
    private readonly Lock _gate = new();
    private readonly HashSet<long> _reclaimFailed = []; // encoder thread only
    private string? _firstFailure;                      // encoder thread only
    private string? _deletedContentLeft;
    private bool _reclaimQueued; // under _gate
    private bool _closed;        // under _gate

    // Jobs queued or running: counted from before a job is queued until after it has finished, so there is no
    // moment between the queue and the thread where the encoder looks idle with work still to do.
    private int _jobs;

    public BatchEncoder(string dataFolder)
        : this(dataFolder, rebuildStepHook: null)
    {
    }

    /// <param name="rebuildStepHook">Called after each step of a rebuild that a crash could follow; lets tests stop there.</param>
    internal BatchEncoder(string dataFolder, Action<string>? rebuildStepHook)
    {
        _dataFolder = dataFolder;
        _rebuildStepHook = rebuildStepHook;
        _retry = new Timer(_ => RequestReclaim());
        using (var db = WorklapseDatabase.Open(dataFolder))
        using (var pending = db.Command("SELECT id FROM segments WHERE state = $staged ORDER BY id;"))
        {
            pending.Parameters.AddWithValue("$staged", (int)SegmentState.Staged);
            using var reader = pending.ExecuteReader();
            while (reader.Read())
                Add(new Job(JobKind.Encode, reader.GetInt64(0)));
        }
        RequestReclaim();

        _thread = new Thread(Run) { Name = "Worklapse batch encoder", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Raised on the encoder thread after a segment has been encoded and its staging file removed.</summary>
    public event EventHandler<long>? SegmentEncoded;

    /// <summary>Raised on the encoder thread when a segment could not be encoded; it stays staged.</summary>
    public event EventHandler<(long SegmentId, Exception Error)>? EncodingFailed;

    /// <summary>
    /// Raised on the encoder thread when a segment's space could not be reclaimed; it stays as it was and is
    /// tried again the next time an encoder starts on this folder.
    /// </summary>
    public event EventHandler<(long SegmentId, Exception Error)>? ReclaimFailed;

    /// <summary>
    /// What keeps deleted captures' video on disk after the last pass, for the user (null when nothing does):
    /// a segment that couldn't be rebuilt (tried again at the next start), or a file another program has
    /// open (tried again every minute).
    /// </summary>
    public string? DeletedContentLeft => Volatile.Read(ref _deletedContentLeft);

    /// <summary>Raised on the encoder thread when <see cref="DeletedContentLeft"/> changes.</summary>
    public event EventHandler? DeletedContentLeftChanged;

    public void Enqueue(long segmentId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            Add(new Job(JobKind.Encode, segmentId));
        }
    }

    /// <summary>
    /// Frees the space of frames no capture refers to any more (after deletes), between encodes. Safe to call
    /// from any thread, also once the encoder is disposed (the next one to start does it then).
    /// </summary>
    public void RequestReclaim()
    {
        lock (_gate)
        {
            if (_closed || _reclaimQueued)
                return;
            _reclaimQueued = true;
            Add(new Job(JobKind.Reclaim, 0));
        }
    }

    /// <summary>True while segments are queued or being encoded or reclaimed.</summary>
    public bool IsBusy => Volatile.Read(ref _jobs) > 0;

    /// <summary>Waits until the queue is empty. For tests and orderly shutdown.</summary>
    public bool WaitUntilIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy)
        {
            if (DateTime.UtcNow > deadline)
                return false;
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>
    /// Stops after the frame being encoded. Segments not yet encoded stay staged and are picked up the
    /// next time a batch encoder starts on this folder; so is space not yet reclaimed.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            _queue.CompleteAdding();
        }
        _stop.Cancel();
        _thread.Join();
        // After the thread, which may still set it; a retry firing now finds the encoder closed.
        _retry.Dispose();
        _queue.Dispose();
        _stop.Dispose();
    }

    private enum JobKind
    {
        Encode,
        Reclaim,
    }

    private readonly record struct Job(JobKind Kind, long SegmentId);

    private void Add(Job job)
    {
        Interlocked.Increment(ref _jobs);
        _queue.Add(job);
    }

    private void Run()
    {
        using var db = WorklapseDatabase.Open(_dataFolder);
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable(_stop.Token))
            {
                try
                {
                    if (job.Kind == JobKind.Encode)
                        EncodeAndReport(db, job.SegmentId);
                    else
                        ReclaimOne(db);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    // After any job this one queued (another reclaim pass, a rebuilt segment) has been counted.
                    Interlocked.Decrement(ref _jobs);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EncodeAndReport(WorklapseDatabase db, long segmentId)
    {
        try
        {
            if (Encode(db, segmentId, _stop.Token))
                SegmentEncoded?.Invoke(this, segmentId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EncodingFailed?.Invoke(this, (segmentId, ex));
        }
    }

    /// <summary>
    /// Reclaims one queued segment and queues another pass if there was one, so encodes queued meanwhile
    /// don't wait for a long series of rebuilds.
    /// </summary>
    private void ReclaimOne(WorklapseDatabase db)
    {
        lock (_gate)
            _reclaimQueued = false; // a request from now on queues another pass

        // Left by a crash or by a file another program had open.
        var locked = SpaceReclaimer.DeleteDeadFiles(db);
        if (SpaceReclaimer.NextQueued(db, _reclaimFailed) is not { } next)
        {
            // All done for now. Empty the database log, which still holds the pages as they were before the
            // deletes, and say what (if anything) is left.
            var logEmptied = SpaceReclaimer.TruncateLog(db);
            SetDeletedContentLeft(_firstFailure ?? (locked.Count > 0 ? $"{locked[0]} is open in another program; trying again every minute" : null));
            if (locked.Count > 0 || !logEmptied)
                _retry.Change(RetryDelay, Timeout.InfiniteTimeSpan);
            return;
        }

        long? rebuilt;
        try
        {
            rebuilt = SpaceReclaimer.Reclaim(db, next.SegmentId, next.Generation, _stop.Token, _rebuildStepHook);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not again in this run, so a segment that fails every time doesn't keep the thread busy.
            _reclaimFailed.Add(next.SegmentId);
            _firstFailure ??= $"{SegmentPath(db, next.SegmentId)}: {ex.Message} (tried again when Worklapse next starts)";
            ReclaimFailed?.Invoke(this, (next.SegmentId, ex));
            RequestReclaim();
            return;
        }

        if (rebuilt is { } id)
            EncodeAndReport(db, id);
        RequestReclaim();
    }

    private void SetDeletedContentLeft(string? text)
    {
        if (Interlocked.Exchange(ref _deletedContentLeft, text) != text)
            DeletedContentLeftChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string SegmentPath(WorklapseDatabase db, long segmentId)
    {
        using var command = db.Command("SELECT path FROM segments WHERE id = $id;");
        command.Parameters.AddWithValue("$id", segmentId);
        return command.ExecuteScalar() as string ?? $"segment {segmentId}";
    }

    /// <summary>
    /// Encodes one staged segment. Frames whose captures were all deleted while it was staged are left out,
    /// and a segment with none left is deleted. Returns false if nothing was encoded.
    /// </summary>
    internal static bool Encode(WorklapseDatabase db, long segmentId, CancellationToken cancel)
    {
        string relative;
        int width, height, quality;
        FrameFormat format;
        using (var segment = db.Command("SELECT path, width, height, quality, state, format FROM segments WHERE id = $id;"))
        {
            segment.Parameters.AddWithValue("$id", segmentId);
            using var reader = segment.ExecuteReader();
            if (!reader.Read())
                return false; // deleted meanwhile
            if (reader.GetInt32(4) != (int)SegmentState.Staged)
                return false;
            (relative, width, height, quality, format) =
                (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), (FrameFormat)reader.GetInt32(5));
        }
        if (format != FrameFormat.Video)
            return FinishImage(db, segmentId, relative);

        var frames = new List<(int Index, long Offset, int Length)>();
        using (var list = db.Command("SELECT frame_index, file_offset, length FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;"))
        {
            list.Parameters.AddWithValue("$id", segmentId);
            using var reader = list.ExecuteReader();
            while (reader.Read())
                frames.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)));
        }

        // The recorder only adds references to the segment it is writing, and a segment is encoded once it is
        // closed, so frames unreferenced now stay that way and can be left out. Deletes queued it for that;
        // the queue entry is read first, so one made after the references are read keeps it queued.
        long? queued;
        using (var generation = db.Command("SELECT generation FROM reclaim_queue WHERE segment_id = $id;"))
        {
            generation.Parameters.AddWithValue("$id", segmentId);
            queued = generation.ExecuteScalar() as long?;
        }
        var referenced = SpaceReclaimer.ReferencedFrames(db, segmentId);
        if (referenced.Count == 0)
        {
            SpaceReclaimer.Remove(db, segmentId, relative);
            return false;
        }
        var last = frames.FindLastIndex(f => referenced.Contains(f.Index));

        var finalPath = Path.Combine(db.DataFolder, relative);
        var stagingPath = StagingFile.PathFor(finalPath);
        var tempPath = finalPath + ".tmp";
        File.Delete(tempPath);

        var encoded = new List<(int Index, long Offset, int Length, bool IsKey)>(referenced.Count);
        try
        {
            using (var staging = new StagingReader(stagingPath))
            using (var encoder = new Av1Encoder(width, height, quality, threads: EncoderThreads))
            using (var writer = new IvfWriter(tempPath, width, height))
            {
                for (var i = 0; i <= last; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var (index, offset, length) = frames[i];
                    // Staged frames are deltas of the one before, so the ones left out are applied too.
                    staging.Apply(offset, length);
                    if (!referenced.Contains(index))
                        continue;
                    var frame = encoder.Encode(staging.Frame, staging.FramePtsMs);
                    var position = writer.WriteFrame(staging.FramePtsMs, frame.Data.Span);
                    encoded.Add((index, position, frame.Data.Length, frame.IsKey));
                }
            }

            // The finished file replaces any partial one from an interrupted earlier attempt.
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }

        using (var transaction = db.BeginTransaction())
        {
            using var update = db.Command("""
                UPDATE segment_frames SET file_offset = $offset, length = $length, is_key = $key
                WHERE segment_id = $id AND frame_index = $index;
                """);
            var pOffset = update.Parameters.Add("$offset", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pLength = update.Parameters.Add("$length", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pKey = update.Parameters.Add("$key", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pIndex = update.Parameters.Add("$index", Microsoft.Data.Sqlite.SqliteType.Integer);
            update.Parameters.AddWithValue("$id", segmentId);
            foreach (var (index, offset, length, isKey) in encoded)
            {
                pOffset.Value = offset;
                pLength.Value = length;
                pKey.Value = isKey ? 1 : 0;
                pIndex.Value = index;
                update.ExecuteNonQuery();
            }

            // Frames left out keep no row. The foreign key from frames makes this fail, rather than lose a
            // frame, if one is in use after all.
            using var drop = db.Command("DELETE FROM segment_frames WHERE segment_id = $id AND frame_index = $index;");
            drop.Parameters.AddWithValue("$id", segmentId);
            var pDrop = drop.Parameters.Add("$index", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var (index, _, _) in frames)
            {
                if (referenced.Contains(index))
                    continue;
                pDrop.Value = index;
                drop.ExecuteNonQuery();
            }

            using var done = db.Command("""
                UPDATE segments SET state = $encoded WHERE id = $id;
                DELETE FROM reclaim_queue WHERE segment_id = $id AND generation = $generation;
                """);
            done.Parameters.AddWithValue("$encoded", (int)SegmentState.Encoded);
            done.Parameters.AddWithValue("$id", segmentId);
            done.Parameters.AddWithValue("$generation", (object?)queued ?? DBNull.Value);
            done.ExecuteNonQuery();
            transaction.Commit();
        }

        // Readers open staging files with delete sharing, so this succeeds even while one is being read.
        File.Delete(stagingPath);
        return true;
    }

    /// <summary>
    /// Marks an image finished once the recorder has moved on from it (it no longer shows it again for an unchanged
    /// monitor), so deletes can remove it from then on; or deletes it now if its captures were deleted meanwhile.
    /// </summary>
    private static bool FinishImage(WorklapseDatabase db, long segmentId, string relative)
    {
        // As for video: the queue entry is read before the references.
        long? queued;
        using (var generation = db.Command("SELECT generation FROM reclaim_queue WHERE segment_id = $id;"))
        {
            generation.Parameters.AddWithValue("$id", segmentId);
            queued = generation.ExecuteScalar() as long?;
        }
        if (SpaceReclaimer.ReferencedFrames(db, segmentId).Count == 0)
        {
            SpaceReclaimer.Remove(db, segmentId, relative);
            return false;
        }

        using var transaction = db.BeginTransaction();
        using var done = db.Command("""
            UPDATE segments SET state = $encoded WHERE id = $id;
            DELETE FROM reclaim_queue WHERE segment_id = $id AND generation = $generation;
            """);
        done.Parameters.AddWithValue("$encoded", (int)SegmentState.Encoded);
        done.Parameters.AddWithValue("$id", segmentId);
        done.Parameters.AddWithValue("$generation", (object?)queued ?? DBNull.Value);
        done.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }
}
