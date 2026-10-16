using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// Decodes recorded frames for playback, whether their segment is still staged or already encoded.
/// Keeps one decoder per monitor positioned in the segment last used, so stepping forward decodes one
/// frame, and a small byte-limited cache of recent frames so stepping back a little is usually free.
/// Not thread-safe: use it from one thread (playback decodes on a background thread).
/// </summary>
/// <remarks>
/// Memory is the constraint here. An open libaom decoder holds its reference frames and working buffers:
/// about 60 MB for a 1920×1080 monitor and 145 MB for 3440×1440 with one thread (each extra thread adds
/// another 30–40 MB for little speed: inter frames take ~12 ms instead of ~8). So there is at most one open
/// decoder per monitor, single-threaded, and <see cref="CloseIdle"/> closes the ones not used lately.
/// </remarks>
public sealed class FrameProvider : IDisposable
{
    public const long DefaultCacheBudgetBytes = 64L * 1024 * 1024;
    private const int DecoderThreads = 1;
    private const int FramesKeptWhenSteppingBack = 2;

    private readonly WorklapseDatabase _db;
    private readonly long _cacheBudgetBytes;
    private readonly FramePool _pool = new();
    private readonly Dictionary<long, SegmentCursor> _cursorsByMonitor = [];
    private readonly LinkedList<(FrameRef Key, DecodedFrame Frame)> _cacheOrder = new();
    private readonly Dictionary<FrameRef, LinkedListNode<(FrameRef Key, DecodedFrame Frame)>> _cache = [];
    private long _cacheBytes;

    public FrameProvider(string dataFolder, long cacheBudgetBytes = DefaultCacheBudgetBytes)
    {
        _db = WorklapseDatabase.Open(dataFolder);
        _cacheBudgetBytes = cacheBudgetBytes;
    }

    /// <summary>Decoders currently open (one per monitor at most).</summary>
    public int OpenDecoders => _cursorsByMonitor.Count;

    /// <summary>
    /// Returns the frame with a reference the caller owns: call <see cref="DecodedFrame.Release"/> when done.
    /// </summary>
    public DecodedFrame Get(FrameRef frame)
    {
        if (_cache.TryGetValue(frame, out var node))
        {
            _cacheOrder.Remove(node);
            _cacheOrder.AddFirst(node);
            return node.Value.Frame.AddReference();
        }

        DecodedFrame decoded;
        try
        {
            decoded = Decode(frame);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            // The segment was encoded (its staging file removed) since the cursor was opened; reopen once.
            DropCursor(frame.SegmentId);
            decoded = Decode(frame);
        }

        Remember(frame, decoded);
        return decoded.AddReference();
    }

    private DecodedFrame Decode(FrameRef frame)
    {
        var cursor = Cursor(frame.SegmentId);
        var index = frame.FrameIndex;
        var restarted = cursor.MoveBefore(index);

        // Going back means decoding again from the key frame. Keep the frames just before the one wanted
        // on the way, since the next steps back are likely to need them.
        var keepFrom = restarted ? index - FramesKeptWhenSteppingBack : index;
        while (cursor.Position < index)
        {
            cursor.Advance();
            var passing = frame with { FrameIndex = cursor.Position };
            if (cursor.Position >= keepFrom && cursor.Position < index && !_cache.ContainsKey(passing))
            {
                MakeRoom(cursor.FrameBytes);
                Remember(passing, cursor.Copy(_pool));
            }
        }
        MakeRoom(cursor.FrameBytes);
        return cursor.Copy(_pool);
    }

    /// <summary>Closes the decoders not used for <paramref name="idleFor"/>, freeing their memory.</summary>
    public void CloseIdle(TimeSpan idleFor)
    {
        var cutoff = Environment.TickCount64 - (long)idleFor.TotalMilliseconds;
        foreach (var (monitorId, cursor) in _cursorsByMonitor.Where(c => c.Value.LastUsed <= cutoff).ToList())
        {
            _cursorsByMonitor.Remove(monitorId);
            cursor.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var cursor in _cursorsByMonitor.Values)
            cursor.Dispose();
        _cursorsByMonitor.Clear();
        foreach (var (_, frame) in _cacheOrder)
            frame.Release();
        _cacheOrder.Clear();
        _cache.Clear();
        _cacheBytes = 0;
        _pool.Clear();
        _db.Dispose();
    }

    private SegmentCursor Cursor(long segmentId)
    {
        foreach (var open in _cursorsByMonitor.Values)
        {
            if (open.SegmentId == segmentId)
            {
                open.LastUsed = Environment.TickCount64;
                return open;
            }
        }

        // A monitor's frames are decoded by one decoder: opening another of its segments closes the last one.
        var cursor = SegmentCursor.Open(_db, segmentId, DecoderThreads);
        if (_cursorsByMonitor.Remove(cursor.MonitorId, out var previous))
            previous.Dispose();
        _cursorsByMonitor[cursor.MonitorId] = cursor;
        return cursor;
    }

    private void DropCursor(long segmentId)
    {
        foreach (var (monitorId, cursor) in _cursorsByMonitor)
        {
            if (cursor.SegmentId == segmentId)
            {
                _cursorsByMonitor.Remove(monitorId);
                cursor.Dispose();
                return;
            }
        }
    }

    /// <summary>
    /// Evicts frames until one of <paramref name="bytes"/> fits the budget. Done before decoding, so an
    /// evicted frame's buffer (when nobody else holds it) is the one the new frame reuses; for that, the
    /// least recently used frame of the same size goes first (monitors differ in size).
    /// </summary>
    private void MakeRoom(int bytes)
    {
        while (_cacheBytes + bytes > _cacheBudgetBytes && _cacheOrder.Count > 0)
        {
            var victim = _cacheOrder.Last!;
            for (var node = _cacheOrder.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Frame.I420.Length == bytes)
                {
                    victim = node;
                    break;
                }
            }
            _cacheOrder.Remove(victim);
            _cache.Remove(victim.Value.Key);
            _cacheBytes -= victim.Value.Frame.I420.Length;
            victim.Value.Frame.Release();
        }
    }

    private void Remember(FrameRef key, DecodedFrame frame)
    {
        _cache[key] = _cacheOrder.AddFirst((key, frame));
        _cacheBytes += frame.I420.Length;
    }

    /// <summary>A decoder positioned somewhere in one segment.</summary>
    private sealed class SegmentCursor : IDisposable
    {
        private readonly WorklapseDatabase _db;
        private readonly string _path;
        private readonly SegmentState _state;
        private readonly int _width, _height, _threads;
        private List<(long Offset, int Length)> _frames = [];
        private FileStream? _ivf;
        private Av1Decoder? _decoder;
        private StagingReader? _staging;
        private byte[] _readBuffer = [];
        private int _position = -1;

        private SegmentCursor(WorklapseDatabase db, long segmentId, long monitorId, string path, SegmentState state, int width, int height, int threads)
        {
            _db = db;
            SegmentId = segmentId;
            MonitorId = monitorId;
            _path = path;
            _state = state;
            _width = width;
            _height = height;
            _threads = threads;
            LastUsed = Environment.TickCount64;
        }

        public long SegmentId { get; }
        public long MonitorId { get; }
        public long LastUsed { get; set; }

        public static SegmentCursor Open(WorklapseDatabase db, long segmentId, int threads)
        {
            using var command = db.Command("SELECT path, state, width, height, monitor_id FROM segments WHERE id = $id;");
            command.Parameters.AddWithValue("$id", segmentId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException($"Segment {segmentId} does not exist.");
            var path = Path.Combine(db.DataFolder, reader.GetString(0));
            var cursor = new SegmentCursor(db, segmentId, reader.GetInt64(4), path, (SegmentState)reader.GetInt32(1),
                reader.GetInt32(2), reader.GetInt32(3), threads);
            cursor.LoadFrameIndex();
            return cursor;
        }

        /// <summary>The frame last decoded, or −1 before the first.</summary>
        public int Position => _position;

        public int FrameBytes => I420.Size(_width, _height);

        /// <summary>
        /// Gets ready to decode up to <paramref name="index"/> with <see cref="Advance"/>. Frames can only be
        /// rebuilt forward from the first, so going back starts over; returns whether it did.
        /// </summary>
        public bool MoveBefore(int index)
        {
            // A staged segment may have grown since the index was loaded.
            if (index >= _frames.Count)
                LoadFrameIndex();
            if (index >= _frames.Count)
                throw new InvalidDataException($"Segment {SegmentId} has no frame {index}.");

            // At or before the frame (possibly on it already): carry on from here.
            if (index >= _position && _position >= 0)
                return false;
            Restart();
            return true;
        }

        /// <summary>Decodes the next frame.</summary>
        public void Advance()
        {
            Apply(_position + 1);
            _position++;
        }

        /// <summary>Copies the current frame out.</summary>
        public DecodedFrame Copy(FramePool pool)
        {
            var frame = pool.Rent(_width, _height);
            try
            {
                if (_state == SegmentState.Encoded)
                    _decoder!.CopyI420(frame.Writable);
                else
                    _staging!.Frame.CopyTo(frame.Writable);
            }
            catch
            {
                frame.Release();
                throw;
            }
            return frame;
        }

        public void Dispose()
        {
            _decoder?.Dispose();
            _staging?.Dispose();
            _ivf?.Dispose();
        }

        private void Restart()
        {
            _position = -1;
            if (_state == SegmentState.Encoded)
            {
                _ivf ??= new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (_decoder is null)
                    _decoder = new Av1Decoder(_threads);
                else
                    _decoder.Reset();
            }
            else
            {
                _staging ??= new StagingReader(StagingFile.PathFor(_path));
                _staging.Reset();
            }
        }

        private void Apply(int index)
        {
            var (offset, length) = _frames[index];
            if (_state == SegmentState.Encoded)
                _decoder!.Decode(IvfReader.ReadFrame(_ivf!, offset, length, ref _readBuffer));
            else
                _staging!.Apply(offset, length);
        }

        private void LoadFrameIndex()
        {
            // Encoding a staged segment rewrites its offsets to point into the AV1 file; offsets for one
            // format must never be used with the other, so a change of state reopens the cursor.
            using (var state = _db.Command("SELECT state FROM segments WHERE id = $id;"))
            {
                state.Parameters.AddWithValue("$id", SegmentId);
                if (state.ExecuteScalar() is not long current || (SegmentState)current != _state)
                    throw new InvalidOperationException($"Segment {SegmentId} changed state; reopen it.");
            }

            using var command = _db.Command("SELECT file_offset, length FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;");
            command.Parameters.AddWithValue("$id", SegmentId);
            using var reader = command.ExecuteReader();
            var frames = new List<(long, int)>();
            while (reader.Read())
                frames.Add((reader.GetInt64(0), reader.GetInt32(1)));
            _frames = frames;
        }
    }
}
