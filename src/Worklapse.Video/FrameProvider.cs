using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>A decoded frame: BGRA pixels with rows packed (stride = width × 4). Never modified after creation.</summary>
public sealed record DecodedFrame(int Width, int Height, byte[] Pixels);

/// <summary>
/// Decodes recorded frames for playback, whether their segment is still staged or already encoded.
/// Keeps a decoder positioned in each recently used segment so stepping forward decodes one frame, and a
/// byte-limited cache of recent frames so stepping back is usually free. Not thread-safe: use it from one
/// thread (playback decodes on a background thread).
/// </summary>
public sealed class FrameProvider : IDisposable
{
    private const int MaxOpenSegments = 8;

    private readonly WorklapseDatabase _db;
    private readonly long _cacheBudgetBytes;
    private readonly Dictionary<long, SegmentCursor> _cursors = [];
    private readonly LinkedList<(FrameRef Key, DecodedFrame Frame)> _cacheOrder = new();
    private readonly Dictionary<FrameRef, LinkedListNode<(FrameRef Key, DecodedFrame Frame)>> _cache = [];
    private long _cacheBytes;

    public FrameProvider(string dataFolder, long cacheBudgetBytes = 400L * 1024 * 1024)
    {
        _db = WorklapseDatabase.Open(dataFolder);
        _cacheBudgetBytes = cacheBudgetBytes;
    }

    public DecodedFrame Get(FrameRef frame)
    {
        if (_cache.TryGetValue(frame, out var node))
        {
            _cacheOrder.Remove(node);
            _cacheOrder.AddFirst(node);
            return node.Value.Frame;
        }

        DecodedFrame decoded;
        try
        {
            decoded = Cursor(frame.SegmentId).Decode(frame.FrameIndex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            // The segment was encoded (its staging file removed) since the cursor was opened; reopen once.
            DropCursor(frame.SegmentId);
            decoded = Cursor(frame.SegmentId).Decode(frame.FrameIndex);
        }

        Remember(frame, decoded);
        return decoded;
    }

    public void Dispose()
    {
        foreach (var cursor in _cursors.Values)
            cursor.Dispose();
        _cursors.Clear();
        _db.Dispose();
    }

    private SegmentCursor Cursor(long segmentId)
    {
        if (_cursors.TryGetValue(segmentId, out var cursor))
        {
            cursor.LastUsed = Environment.TickCount64;
            return cursor;
        }

        if (_cursors.Count >= MaxOpenSegments)
            DropCursor(_cursors.MinBy(c => c.Value.LastUsed).Key);

        cursor = SegmentCursor.Open(_db, segmentId);
        _cursors[segmentId] = cursor;
        return cursor;
    }

    private void DropCursor(long segmentId)
    {
        if (_cursors.Remove(segmentId, out var cursor))
            cursor.Dispose();
    }

    private void Remember(FrameRef key, DecodedFrame frame)
    {
        _cache[key] = _cacheOrder.AddFirst((key, frame));
        _cacheBytes += frame.Pixels.Length;
        while (_cacheBytes > _cacheBudgetBytes && _cacheOrder.Count > 1)
        {
            var last = _cacheOrder.Last!;
            _cacheOrder.RemoveLast();
            _cache.Remove(last.Value.Key);
            _cacheBytes -= last.Value.Frame.Pixels.Length;
        }
    }

    /// <summary>A decoder positioned somewhere in one segment.</summary>
    private sealed class SegmentCursor : IDisposable
    {
        private readonly WorklapseDatabase _db;
        private readonly long _segmentId;
        private readonly string _path;
        private readonly SegmentState _state;
        private readonly int _width, _height;
        private List<(long Offset, int Length)> _frames = [];
        private FileStream? _ivf;
        private Av1Decoder? _decoder;
        private StagingReader? _staging;
        private int _position = -1;

        private SegmentCursor(WorklapseDatabase db, long segmentId, string path, SegmentState state, int width, int height)
        {
            _db = db;
            _segmentId = segmentId;
            _path = path;
            _state = state;
            _width = width;
            _height = height;
            LastUsed = Environment.TickCount64;
        }

        public long LastUsed { get; set; }

        public static SegmentCursor Open(WorklapseDatabase db, long segmentId)
        {
            using var command = db.Command("SELECT path, state, width, height FROM segments WHERE id = $id;");
            command.Parameters.AddWithValue("$id", segmentId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException($"Segment {segmentId} does not exist.");
            var path = Path.Combine(db.DataFolder, reader.GetString(0));
            var cursor = new SegmentCursor(db, segmentId, path, (SegmentState)reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
            cursor.LoadFrameIndex();
            return cursor;
        }

        public DecodedFrame Decode(int index)
        {
            // A staged segment may have grown since the index was loaded.
            if (index >= _frames.Count)
                LoadFrameIndex();
            if (index >= _frames.Count)
                throw new InvalidDataException($"Segment {_segmentId} has no frame {index}.");

            if (index <= _position || _position < 0)
                Restart();

            for (var i = _position + 1; i <= index; i++)
                Apply(i);
            _position = index;

            var pixels = new byte[_width * _height * 4];
            if (_state == SegmentState.Encoded)
                _decoder!.CopyBgra(pixels, _width * 4);
            else
                _staging!.Frame.CopyTo(pixels);
            return new DecodedFrame(_width, _height, pixels);
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
                    _decoder = new Av1Decoder();
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
                _decoder!.Decode(IvfReader.ReadFrame(_ivf!, offset, length));
            else
                _staging!.Apply(offset, length);
        }

        private void LoadFrameIndex()
        {
            // Encoding a staged segment rewrites its offsets to point into the AV1 file; offsets for one
            // format must never be used with the other, so a change of state reopens the cursor.
            using (var state = _db.Command("SELECT state FROM segments WHERE id = $id;"))
            {
                state.Parameters.AddWithValue("$id", _segmentId);
                if (state.ExecuteScalar() is not long current || (SegmentState)current != _state)
                    throw new InvalidOperationException($"Segment {_segmentId} changed state; reopen it.");
            }

            using var command = _db.Command("SELECT file_offset, length FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;");
            command.Parameters.AddWithValue("$id", _segmentId);
            using var reader = command.ExecuteReader();
            var frames = new List<(long, int)>();
            while (reader.Read())
                frames.Add((reader.GetInt64(0), reader.GetInt32(1)));
            _frames = frames;
        }
    }
}
