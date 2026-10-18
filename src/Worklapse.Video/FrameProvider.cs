using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// The frame is no longer in its segment, or the segment no longer exists: it was rebuilt or removed after
/// captures were deleted. Its capture, if it still exists, refers to a frame in another segment now; reload it.
/// </summary>
public sealed class FrameUnavailableException(FrameRef frame)
    : Exception($"Frame {frame.FrameIndex} of segment {frame.SegmentId} is no longer stored there.")
{
    public FrameRef Frame { get; } = frame;
}

/// <summary>
/// Decodes recorded frames for playback, whether their segment is still staged or already encoded, video or image.
/// Keeps one decoder per monitor positioned in the video segment last used, so stepping forward decodes one
/// frame, and a small byte-limited cache of recent frames so stepping back a little is usually free. Images are
/// read and decoded on their own; no decoder stays open for them.
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
    private byte[] _imageFile = []; // the image file last read, reused

    public FrameProvider(string dataFolder, long cacheBudgetBytes = DefaultCacheBudgetBytes)
    {
        _db = WorklapseDatabase.Open(dataFolder);
        _cacheBudgetBytes = cacheBudgetBytes;
    }

    /// <summary>Decoders currently open (one per monitor at most).</summary>
    public int OpenDecoders => _cursorsByMonitor.Count;

    /// <summary>
    /// Returns the frame with a reference the caller owns: call <see cref="DecodedFrame.Release"/> when done.
    /// Throws <see cref="FrameUnavailableException"/> if the frame has moved to another segment or is gone.
    /// A frame still cached or in a segment already open is returned even then (it is the same picture).
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
            // The segment was encoded (its staging file removed), rebuilt or removed since the cursor was
            // opened; reopen once. If it's gone, opening it says so.
            DropCursor(frame.SegmentId);
            decoded = Decode(frame);
        }

        Remember(frame, decoded);
        return decoded.AddReference();
    }

    private DecodedFrame Decode(FrameRef frame)
    {
        var cursor = OpenCursor(frame.SegmentId);
        if (cursor is null)
        {
            var segment = SegmentRow.Load(_db, frame.SegmentId) ?? throw new FrameUnavailableException(frame);
            if (segment.Format != FrameFormat.Video)
                return DecodeImage(frame, segment);
            cursor = Open(segment);
        }

        var target = cursor.Find(frame);
        var restarted = cursor.MoveBefore(target);

        // Going back means decoding again from the key frame. Keep the frames just before the one wanted
        // on the way, since the next steps back are likely to need them.
        var keepFrom = restarted ? target - FramesKeptWhenSteppingBack : target;
        while (cursor.Position < target)
        {
            cursor.Advance();
            var passing = frame with { FrameIndex = cursor.FrameIndex };
            if (cursor.Position >= keepFrom && cursor.Position < target && !_cache.ContainsKey(passing))
            {
                MakeRoom(cursor.FrameBytes);
                Remember(passing, cursor.Copy(_pool));
            }
        }
        MakeRoom(cursor.FrameBytes);
        return cursor.Copy(_pool);
    }

    /// <summary>An image segment's one frame, straight from its file.</summary>
    private DecodedFrame DecodeImage(FrameRef frame, SegmentRow segment)
    {
        if (frame.FrameIndex != 0)
            throw new FrameUnavailableException(frame);

        int length;
        using (var file = new FileStream(Path.Combine(_db.DataFolder, segment.Path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1))
        {
            length = checked((int)file.Length);
            if (_imageFile.Length < length)
                _imageFile = GC.AllocateUninitializedArray<byte>(length);
            file.ReadExactly(_imageFile, 0, length);
        }

        var image = _imageFile.AsSpan(0, length);
        var (_, width, height) = ImageCodec.Identify(image);
        MakeRoom(I420.Size(width, height));
        var decoded = _pool.Rent(width, height);
        try
        {
            ImageCodec.DecodeI420(image, width, height, decoded.Writable);
        }
        catch
        {
            decoded.Release();
            throw;
        }
        return decoded;
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
        _imageFile = [];
        _db.Dispose();
    }

    /// <summary>The cursor already open on a video segment, if any.</summary>
    private SegmentCursor? OpenCursor(long segmentId)
    {
        foreach (var open in _cursorsByMonitor.Values)
        {
            if (open.SegmentId == segmentId)
            {
                open.LastUsed = Environment.TickCount64;
                return open;
            }
        }
        return null;
    }

    private SegmentCursor Open(SegmentRow segment)
    {
        // A monitor's frames are decoded by one decoder: opening another of its segments closes the last one.
        var cursor = SegmentCursor.Open(_db, segment, DecoderThreads);
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

    private sealed record SegmentRow(long Id, long MonitorId, string Path, SegmentState State, FrameFormat Format, int Width, int Height)
    {
        /// <summary>The segment; null if it doesn't exist (any more).</summary>
        public static SegmentRow? Load(WorklapseDatabase db, long id)
        {
            using var command = db.Command("SELECT monitor_id, path, state, format, width, height FROM segments WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SegmentRow(id, reader.GetInt64(0), reader.GetString(1), (SegmentState)reader.GetInt32(2), (FrameFormat)reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5))
                : null;
        }
    }

    /// <summary>
    /// A decoder positioned somewhere in one video segment. Positions count the frames stored in the segment, in
    /// order; frame indexes can have gaps where frames were left out after deletes.
    /// </summary>
    private sealed class SegmentCursor : IDisposable
    {
        private readonly WorklapseDatabase _db;
        private readonly string _path;
        private readonly SegmentState _state;
        private readonly int _width, _height, _threads;
        private List<(int Index, long Offset, int Length)> _frames = [];
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

        public static SegmentCursor Open(WorklapseDatabase db, SegmentRow segment, int threads)
        {
            var cursor = new SegmentCursor(db, segment.Id, segment.MonitorId, Path.Combine(db.DataFolder, segment.Path), segment.State,
                segment.Width, segment.Height, threads);
            cursor.LoadFrameIndex();
            return cursor;
        }

        /// <summary>The position of the frame last decoded, or −1 before the first.</summary>
        public int Position => _position;

        /// <summary>The frame index of the frame last decoded.</summary>
        public int FrameIndex => _frames[_position].Index;

        public int FrameBytes => I420.Size(_width, _height);

        /// <summary>The position of a frame in the segment.</summary>
        public int Find(FrameRef frame)
        {
            var position = PositionOf(frame.FrameIndex);
            if (position < 0)
            {
                // A staged segment may have grown since the index was loaded.
                LoadFrameIndex();
                position = PositionOf(frame.FrameIndex);
            }
            return position >= 0 ? position : throw new FrameUnavailableException(frame);
        }

        /// <summary>
        /// Gets ready to decode up to <paramref name="position"/> with <see cref="Advance"/>. Frames can only be
        /// rebuilt forward from the first, so going back starts over; returns whether it did.
        /// </summary>
        public bool MoveBefore(int position)
        {
            // At or before the frame (possibly on it already): carry on from here.
            if (position >= _position && _position >= 0)
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

        private void Apply(int position)
        {
            var (_, offset, length) = _frames[position];
            if (_state == SegmentState.Encoded)
                _decoder!.Decode(IvfReader.ReadFrame(_ivf!, offset, length, ref _readBuffer));
            else
                _staging!.Apply(offset, length);
        }

        private void LoadFrameIndex()
        {
            // Encoding a staged segment rewrites its offsets to point into the AV1 file; offsets for one
            // format must never be used with the other, so a change of state (or the segment's removal)
            // reopens the cursor.
            using (var state = _db.Command("SELECT state FROM segments WHERE id = $id;"))
            {
                state.Parameters.AddWithValue("$id", SegmentId);
                if (state.ExecuteScalar() is not long current || (SegmentState)current != _state)
                    throw new InvalidOperationException($"Segment {SegmentId} changed state; reopen it.");
            }

            using var command = _db.Command("SELECT frame_index, file_offset, length FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;");
            command.Parameters.AddWithValue("$id", SegmentId);
            using var reader = command.ExecuteReader();
            var frames = new List<(int, long, int)>();
            while (reader.Read())
                frames.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)));
            _frames = frames;
        }

        private int PositionOf(int frameIndex)
        {
            int low = 0, high = _frames.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var index = _frames[middle].Index;
                if (index == frameIndex)
                    return middle;
                if (index < frameIndex)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return -1;
        }
    }
}
