using System.Text.RegularExpressions;
using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// Removes the frames no capture refers to any more from disk, after captures are deleted. The point is that
/// deleted content is gone (nothing to show a client by accident); the space freed is a bonus. A segment with
/// no frames left in use is deleted. One with some is rebuilt from them: they are decoded into a new staged
/// segment, every reference moves over to it in one transaction, the old file is deleted, and the new segment
/// is then encoded like any other. Segments are never changed in place, so a reader with the old one open
/// keeps getting whole frames, and a crash at any point leaves the old segment or the new one in use, never
/// neither. Staged segments are left alone: encoding one leaves out the frames nothing refers to.
/// <para>
/// Only ever shrinks what is referenced: the recorder adds references only to the segment it is writing,
/// which is staged, so the frames found unreferenced here stay that way.
/// </para>
/// Runs on the batch encoder's thread; every file it lets go of goes through <c>dead_segment_files</c>.
/// </summary>
internal static partial class SpaceReclaimer
{
    /// <summary>The queued segment to look at next, leaving out <paramref name="skip"/>; null when there is none.</summary>
    public static (long SegmentId, long Generation)? NextQueued(WorklapseDatabase db, IReadOnlySet<long> skip)
    {
        using var command = db.Command("SELECT segment_id, generation FROM reclaim_queue ORDER BY segment_id;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!skip.Contains(id))
                return (id, reader.GetInt64(1));
        }
        return null;
    }

    /// <summary>
    /// Removes or rebuilds one queued segment if any of its frames is no longer used, however few (rebuilding
    /// costs a little quality, but deleted frames must not stay on disk), and takes it off the queue unless it
    /// was queued again meanwhile. Returns the rebuilt segment, still staged, for the caller to encode.
    /// </summary>
    /// <param name="afterStep">Called after each step that a crash could follow ("staged", "switched"); for tests.</param>
    public static long? Reclaim(WorklapseDatabase db, long segmentId, long generation, CancellationToken cancel,
        Action<string>? afterStep = null)
    {
        var segment = Segment.Load(db, segmentId);
        if (segment is null)
            return null; // deleted meanwhile; its queue entry went with it

        if (segment.State != SegmentState.Encoded)
        {
            Dequeue(db, segmentId, generation);
            return null;
        }

        var frames = segment.LoadFrames(db);
        var referenced = ReferencedFrames(db, segmentId);
        if (referenced.Count == 0)
        {
            Remove(db, segmentId, segment.Path);
            return null;
        }
        if (referenced.Count == frames.Count)
        {
            Dequeue(db, segmentId, generation);
            return null;
        }
        return Rebuild(db, segment, frames, referenced, generation, cancel, afterStep);
    }

    /// <summary>The frame indexes of a segment that some capture still shows.</summary>
    public static HashSet<int> ReferencedFrames(WorklapseDatabase db, long segmentId)
    {
        using var command = db.Command("SELECT DISTINCT frame_index FROM frames WHERE segment_id = $id;");
        command.Parameters.AddWithValue("$id", segmentId);
        using var reader = command.ExecuteReader();
        var indexes = new HashSet<int>();
        while (reader.Read())
            indexes.Add(reader.GetInt32(0));
        return indexes;
    }

    /// <summary>Deletes a segment nothing refers to, with its files.</summary>
    public static void Remove(WorklapseDatabase db, long segmentId, string relativePath)
    {
        using (var transaction = db.BeginTransaction())
        {
            using var dead = db.Command("INSERT OR IGNORE INTO dead_segment_files (path) VALUES ($path);");
            dead.Parameters.AddWithValue("$path", relativePath);
            dead.ExecuteNonQuery();

            // The foreign key from frames makes this fail, rather than lose anything, if a frame is still in use.
            using var delete = db.Command("DELETE FROM segment_frames WHERE segment_id = $id; DELETE FROM segments WHERE id = $id;");
            delete.Parameters.AddWithValue("$id", segmentId);
            delete.ExecuteNonQuery();
            transaction.Commit();
        }
        DeleteDeadFiles(db);
    }

    /// <summary>
    /// Deletes the files listed in <c>dead_segment_files</c>. Ones that can't be deleted yet (another program
    /// has them open) stay listed, to be tried again on the next pass; returns their paths.
    /// </summary>
    public static List<string> DeleteDeadFiles(WorklapseDatabase db)
    {
        var failed = new List<string>();
        var paths = new List<string>();
        using (var list = db.Command("SELECT path FROM dead_segment_files;"))
        using (var reader = list.ExecuteReader())
        {
            while (reader.Read())
                paths.Add(reader.GetString(0));
        }

        foreach (var relative in paths)
        {
            // Never delete the files of a segment in use, whatever the list says.
            using (var live = db.Command("SELECT 1 FROM segments WHERE path = $path;"))
            {
                live.Parameters.AddWithValue("$path", relative);
                if (live.ExecuteScalar() is null && !TryDeleteFiles(Path.Combine(db.DataFolder, relative)))
                {
                    failed.Add(relative);
                    continue;
                }
            }

            using var done = db.Command("DELETE FROM dead_segment_files WHERE path = $path;");
            done.Parameters.AddWithValue("$path", relative);
            done.ExecuteNonQuery();
        }
        return failed;
    }

    /// <summary>
    /// Copies everything in the write-ahead log into the database file and empties the log. With
    /// secure_delete the database file no longer holds what was deleted, but the log still has the pages
    /// as they were before. Returns false if another connection was in the way (try again later).
    /// </summary>
    public static bool TruncateLog(WorklapseDatabase db)
    {
        using var command = db.Command("PRAGMA wal_checkpoint(TRUNCATE);");
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) == 0; // the first column is 1 when it couldn't finish
    }

    private static long Rebuild(WorklapseDatabase db, Segment segment, List<SegmentFrame> frames, HashSet<int> referenced,
        long generation, CancellationToken cancel, Action<string>? afterStep)
    {
        // Listed as dead until the switch below, so a crash while it's being written leaves nothing behind.
        var relative = RebuiltPath(db, segment.Path);
        using (var dead = db.Command("INSERT OR IGNORE INTO dead_segment_files (path) VALUES ($path);"))
        {
            dead.Parameters.AddWithValue("$path", relative);
            dead.ExecuteNonQuery();
        }

        // Decode into a staging file first and encode it afterwards, so the decoder (up to ~145 MB) and the
        // encoder (~200 MB) are never open at the same time. Staging is lossless: the one re-encode is the only loss.
        var written = new List<(int Index, long Offset, int Length)>(referenced.Count);
        var last = frames.FindLastIndex(f => referenced.Contains(f.Index));
        using (var ivf = new FileStream(Path.Combine(db.DataFolder, segment.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var decoder = new Av1Decoder(threads: 1))
        using (var writer = new StagingWriter(StagingFile.PathFor(Path.Combine(db.DataFolder, relative)), segment.Width, segment.Height))
        {
            var frame = new byte[I420.Size(segment.Width, segment.Height)];
            var buffer = Array.Empty<byte>();
            for (var i = 0; i <= last; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var (index, offset, length) = (frames[i].Index, frames[i].Offset, frames[i].Length);
                // Frames depend on the ones before them, so the ones being dropped are decoded too.
                decoder.Decode(IvfReader.ReadFrame(ivf, offset, length, ref buffer));
                if (!referenced.Contains(index))
                    continue;
                if ((decoder.Width, decoder.Height) != (segment.Width, segment.Height))
                    throw new InvalidDataException($"Segment {segment.Id} frame {index} is {decoder.Width}×{decoder.Height}, not {segment.Width}×{segment.Height}.");
                decoder.CopyI420(frame);
                var (stagedOffset, stagedLength) = writer.WriteFrame(frame, IvfReader.ReadTimestamp(ivf, offset));
                written.Add((index, stagedOffset, stagedLength));
            }
        }
        afterStep?.Invoke("staged");

        long rebuiltId;
        using (var transaction = db.BeginTransaction())
        {
            // Frame indexes stay as they were, so moving the references over is a change of segment only.
            var store = new RecordingStore(db);
            rebuiltId = store.InsertSegment(segment.MonitorId, relative, segment.Width, segment.Height, segment.Quality,
                DateTimeOffset.FromUnixTimeMilliseconds(segment.CreatedMs), SegmentState.Staged);
            for (var i = 0; i < written.Count; i++)
                store.InsertSegmentFrame(rebuiltId, written[i].Index, written[i].Offset, written[i].Length, isKey: i == 0);

            using var switchOver = db.Command("""
                UPDATE frames SET segment_id = $new WHERE segment_id = $old;
                -- Queued again while this ran (more of it deleted): the rebuilt one may have frames to drop too.
                INSERT INTO reclaim_queue (segment_id)
                SELECT $new WHERE EXISTS (SELECT 1 FROM reclaim_queue WHERE segment_id = $old AND generation <> $generation);
                DELETE FROM segment_frames WHERE segment_id = $old;
                DELETE FROM segments WHERE id = $old;
                DELETE FROM dead_segment_files WHERE path = $newPath;
                INSERT OR IGNORE INTO dead_segment_files (path) VALUES ($oldPath);
                """);
            switchOver.Parameters.AddWithValue("$new", rebuiltId);
            switchOver.Parameters.AddWithValue("$old", segment.Id);
            switchOver.Parameters.AddWithValue("$generation", generation);
            switchOver.Parameters.AddWithValue("$newPath", relative);
            switchOver.Parameters.AddWithValue("$oldPath", segment.Path);
            switchOver.ExecuteNonQuery();
            transaction.Commit();
        }
        afterStep?.Invoke("switched");

        DeleteDeadFiles(db);
        return rebuiltId;
    }

    private static void Dequeue(WorklapseDatabase db, long segmentId, long generation)
    {
        using var command = db.Command("DELETE FROM reclaim_queue WHERE segment_id = $id AND generation = $generation;");
        command.Parameters.AddWithValue("$id", segmentId);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A name for the rebuild of a segment, next to it: "153000-m3.ivf" → "153000-m3-r1.ivf" (and a rebuild of
    /// that "153000-m3-r2.ivf"), skipping names in use by a file, a segment or a pending delete.
    /// </summary>
    private static string RebuiltPath(WorklapseDatabase db, string relative)
    {
        var stem = RebuildSuffix().Replace(relative[..^".ivf".Length], "");
        for (var n = 1; ; n++)
        {
            var candidate = $"{stem}-r{n}.ivf";
            var full = Path.Combine(db.DataFolder, candidate);
            if (File.Exists(full) || File.Exists(StagingFile.PathFor(full)) || File.Exists(full + ".tmp"))
                continue;
            using var taken = db.Command("SELECT 1 FROM segments WHERE path = $path UNION ALL SELECT 1 FROM dead_segment_files WHERE path = $path;");
            taken.Parameters.AddWithValue("$path", candidate);
            if (taken.ExecuteScalar() is null)
                return candidate;
        }
    }

    [GeneratedRegex(@"-r\d+$")]
    private static partial Regex RebuildSuffix();

    /// <summary>Deletes a segment's files; false if one of them is in use by another program.</summary>
    private static bool TryDeleteFiles(string segmentPath)
    {
        try
        {
            File.Delete(segmentPath);
            File.Delete(StagingFile.PathFor(segmentPath));
            File.Delete(segmentPath + ".tmp");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct SegmentFrame(int Index, long Offset, int Length);

    private sealed record Segment(long Id, long MonitorId, string Path, int Width, int Height, int Quality, long CreatedMs, SegmentState State)
    {
        public static Segment? Load(WorklapseDatabase db, long id)
        {
            using var command = db.Command("SELECT monitor_id, path, width, height, quality, created_ms, state FROM segments WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new Segment(id, reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.GetInt64(5), (SegmentState)reader.GetInt32(6))
                : null;
        }

        public List<SegmentFrame> LoadFrames(WorklapseDatabase db)
        {
            using var command = db.Command("SELECT frame_index, file_offset, length FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;");
            command.Parameters.AddWithValue("$id", Id);
            using var reader = command.ExecuteReader();
            var frames = new List<SegmentFrame>();
            while (reader.Read())
                frames.Add(new SegmentFrame(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)));
            return frames;
        }
    }
}
