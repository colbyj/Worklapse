using System.Collections.Concurrent;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// Encodes finished staged segments to AV1 on a low-priority background thread, one at a time, so only
/// one encoder's memory is in use and only while it works. Staged segments left over from an earlier run
/// (a crash, or quitting before encoding finished) are queued when it starts.
/// </summary>
public sealed class BatchEncoder : IDisposable
{
    /// <summary>Threads per encoder. Few, so a batch doesn't compete much with what the user is doing.</summary>
    private const int EncoderThreads = 2;

    private readonly string _dataFolder;
    private readonly BlockingCollection<long> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private int _busy;

    public BatchEncoder(string dataFolder)
    {
        _dataFolder = dataFolder;
        using (var db = WorklapseDatabase.Open(dataFolder))
        using (var pending = db.Command("SELECT id FROM segments WHERE state = $staged ORDER BY id;"))
        {
            pending.Parameters.AddWithValue("$staged", (int)SegmentState.Staged);
            using var reader = pending.ExecuteReader();
            while (reader.Read())
                _queue.Add(reader.GetInt64(0));
        }

        _thread = new Thread(Run) { Name = "Worklapse batch encoder", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Raised on the encoder thread after a segment has been encoded and its staging file removed.</summary>
    public event EventHandler<long>? SegmentEncoded;

    /// <summary>Raised on the encoder thread when a segment could not be encoded; it stays staged.</summary>
    public event EventHandler<(long SegmentId, Exception Error)>? EncodingFailed;

    public void Enqueue(long segmentId) => _queue.Add(segmentId);

    /// <summary>True while segments are queued or being encoded.</summary>
    public bool IsBusy => _queue.Count > 0 || Volatile.Read(ref _busy) == 1;

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
    /// next time a batch encoder starts on this folder.
    /// </summary>
    public void Dispose()
    {
        _stop.Cancel();
        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
        _stop.Dispose();
    }

    private void Run()
    {
        using var db = WorklapseDatabase.Open(_dataFolder);
        try
        {
            foreach (var segmentId in _queue.GetConsumingEnumerable(_stop.Token))
            {
                Volatile.Write(ref _busy, 1);
                try
                {
                    if (Encode(db, segmentId, _stop.Token))
                        SegmentEncoded?.Invoke(this, segmentId);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    EncodingFailed?.Invoke(this, (segmentId, ex));
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Encodes one staged segment. Returns false if it was already encoded.</summary>
    internal static bool Encode(WorklapseDatabase db, long segmentId, CancellationToken cancel)
    {
        string relative;
        int width, height, quality;
        using (var segment = db.Command("SELECT path, width, height, quality, state FROM segments WHERE id = $id;"))
        {
            segment.Parameters.AddWithValue("$id", segmentId);
            using var reader = segment.ExecuteReader();
            if (!reader.Read())
                return false; // deleted meanwhile
            if (reader.GetInt32(4) != (int)SegmentState.Staged)
                return false;
            (relative, width, height, quality) = (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }

        var frames = new List<(int Index, long Offset, int Length)>();
        using (var list = db.Command("SELECT frame_index, file_offset, length FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;"))
        {
            list.Parameters.AddWithValue("$id", segmentId);
            using var reader = list.ExecuteReader();
            while (reader.Read())
                frames.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)));
        }

        var finalPath = Path.Combine(db.DataFolder, relative);
        var stagingPath = StagingFile.PathFor(finalPath);
        var tempPath = finalPath + ".tmp";
        File.Delete(tempPath);

        var encoded = new List<(int Index, long Offset, int Length, bool IsKey)>(frames.Count);
        try
        {
            using (var staging = new StagingReader(stagingPath))
            using (var encoder = new Av1Encoder(width, height, quality, threads: EncoderThreads))
            using (var writer = new IvfWriter(tempPath, width, height))
            {
                foreach (var (index, offset, length) in frames)
                {
                    cancel.ThrowIfCancellationRequested();
                    staging.Apply(offset, length);
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

            using var done = db.Command("UPDATE segments SET state = $encoded WHERE id = $id;");
            done.Parameters.AddWithValue("$encoded", (int)SegmentState.Encoded);
            done.Parameters.AddWithValue("$id", segmentId);
            done.ExecuteNonQuery();
            transaction.Commit();
        }

        // Readers open staging files with delete sharing, so this succeeds even while one is being read.
        File.Delete(stagingPath);
        return true;
    }
}
