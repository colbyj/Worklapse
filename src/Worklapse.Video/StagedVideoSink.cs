using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// Writes each monitor's captures to a staging file per segment and hands finished segments to a
/// <see cref="BatchEncoder"/>. No encoder is open while recording, which keeps memory low.
/// </summary>
public sealed class StagedVideoSink : IFrameSink
{
    /// <summary>90 captures, 15 minutes at the default interval. Deleting captures re-encodes at most a segment.</summary>
    public const int FramesPerSegment = 90;

    private readonly RecordingStore _store;
    private readonly Func<int> _quality;
    private readonly Dictionary<long, OpenSegment> _open = [];
    private readonly List<long> _closedUncommitted = [];

    public StagedVideoSink(RecordingStore store, Func<int> quality)
    {
        _store = store;
        _quality = quality;
        Encoder = new BatchEncoder(store.Database.DataFolder);
    }

    public BatchEncoder Encoder { get; }

    public FrameRef Append(long monitorId, ScreenCapture capture, DateTimeOffset timestamp)
    {
        if (_open.TryGetValue(monitorId, out var segment)
            && (segment.Writer.Width != capture.Width || segment.Writer.Height != capture.Height || segment.FrameCount >= FramesPerSegment))
        {
            segment.Writer.Dispose();
            _open.Remove(monitorId);
            _closedUncommitted.Add(segment.Id);
            segment = null;
        }

        segment ??= _open[monitorId] = OpenSegment.Create(_store, monitorId, capture.Width, capture.Height, _quality(), timestamp);

        var (offset, length) = segment.Writer.WriteFrame(capture.Frame.Span, timestamp.ToUnixTimeMilliseconds());
        var index = segment.FrameCount++;
        _store.InsertSegmentFrame(segment.Id, index, offset, length, isKey: index == 0);
        return new FrameRef(segment.Id, index);
    }

    public void Committed()
    {
        // Only now can the encoder's own connection see every frame of the segments closed in that transaction.
        foreach (var id in _closedUncommitted)
            Encoder.Enqueue(id);
        _closedUncommitted.Clear();
    }

    public void Close(long monitorId)
    {
        if (!_open.Remove(monitorId, out var segment))
            return;
        segment.Writer.Dispose();
        _closedUncommitted.Add(segment.Id);
    }

    public void CloseAll()
    {
        foreach (var segment in _open.Values)
        {
            segment.Writer.Dispose();
            _closedUncommitted.Add(segment.Id);
        }
        _open.Clear();
        // CloseAll is called between transactions, so everything appended so far is committed.
        Committed();
    }

    public void Dispose()
    {
        // Open segments stay staged on disk and are encoded the next time recording starts on this folder.
        foreach (var segment in _open.Values)
            segment.Writer.Dispose();
        _open.Clear();
        Encoder.Dispose();
    }

    private sealed class OpenSegment(long id, StagingWriter writer)
    {
        public long Id { get; } = id;
        public StagingWriter Writer { get; } = writer;
        public int FrameCount { get; set; }

        public static OpenSegment Create(RecordingStore store, long monitorId, int width, int height, int quality, DateTimeOffset start)
        {
            var folder = Path.Combine("video", start.ToString("yyyy-MM-dd"));
            var name = $"{start:HHmmss}-m{monitorId}";
            var relative = Path.Combine(folder, name + ".ivf");
            var dataFolder = store.Database.DataFolder;
            for (var n = 2; File.Exists(Path.Combine(dataFolder, relative)) || File.Exists(StagingFile.PathFor(Path.Combine(dataFolder, relative))); n++)
                relative = Path.Combine(folder, $"{name}-{n}.ivf");

            var writer = new StagingWriter(StagingFile.PathFor(Path.Combine(dataFolder, relative)), width, height);
            try
            {
                var id = store.InsertSegment(monitorId, relative.Replace('\\', '/'), width, height, quality, start, SegmentState.Staged);
                return new OpenSegment(id, writer);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }
    }
}
