using System.Globalization;
using Dapper;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>
/// Stores each monitor's captures in the format chosen in settings, and hands what it finishes to a
/// <see cref="BatchEncoder"/>:
/// <list type="bullet">
/// <item>Video: a staging file per segment, encoded to AV1 once the segment is finished. No encoder is open while
/// recording, which keeps memory low.</item>
/// <item>Images (JPEG or WebP): a file per changed capture, written at once. Each is a segment of one frame, open
/// (staged) while the recorder may still show it again for a monitor that hasn't changed.</item>
/// </list>
/// A change of format takes effect at each monitor's next changed capture.
/// </summary>
public sealed class FrameSink : IFrameSink
{
    /// <summary>90 captures, 15 minutes at the default interval. Deleting captures re-encodes at most a segment.</summary>
    public const int FramesPerSegment = 90;

    public const string VideoFolder = "video";
    public const string ImagesFolder = "images";

    /// <summary>An image's name until the capture showing it is committed.</summary>
    internal const string TempSuffix = ".tmp";

    private readonly RecordingStore _store;
    private readonly Func<(FrameFormat Format, int Quality)> _storage;
    private readonly Dictionary<long, OpenSegment> _open = [];
    private readonly List<long> _closedUncommitted = [];
    private readonly List<(string Path, string? Final)> _filesUncommitted = [];

    /// <param name="storage">How new frames are stored: format and quality (AV1 quantizer, or image quality).</param>
    public FrameSink(RecordingStore store, Func<(FrameFormat Format, int Quality)> storage)
    {
        _store = store;
        _storage = storage;
        FinishInterruptedImages(store.Database);
        Encoder = new BatchEncoder(store.Database.DataFolder);
    }

    public BatchEncoder Encoder { get; }

    private string DataFolder => _store.Database.DataFolder;

    public FrameRef Append(long monitorId, ScreenCapture capture, DateTimeOffset timestamp)
    {
        var (format, quality) = _storage();
        if (_open.TryGetValue(monitorId, out var segment) && !segment.Takes(format, capture.Width, capture.Height))
        {
            segment.Writer?.Dispose();
            _open.Remove(monitorId);
            _closedUncommitted.Add(segment.Id);
            segment = null;
        }

        if (format != FrameFormat.Video)
            return AppendImage(monitorId, capture, timestamp, format, quality);

        segment ??= _open[monitorId] = OpenVideo(monitorId, capture.Width, capture.Height, quality, timestamp);
        var (offset, length) = segment.Writer!.WriteFrame(capture.Frame.Span, timestamp.ToUnixTimeMilliseconds());
        var index = segment.FrameCount++;
        _store.InsertSegmentFrame(segment.Id, index, offset, length, isKey: index == 0);
        return new FrameRef(segment.Id, index);
    }

    public void Committed()
    {
        // The captures showing these images are in the database now, so the images take their real names.
        var files = _filesUncommitted.ToArray();
        _filesUncommitted.Clear();
        foreach (var (path, final) in files)
        {
            if (final is not null)
                File.Move(path, final);
        }

        // Only now can the encoder's own connection see every frame of the segments closed in that transaction.
        foreach (var id in _closedUncommitted)
            Encoder.Enqueue(id);
        _closedUncommitted.Clear();
    }

    public void Close(long monitorId)
    {
        if (!_open.Remove(monitorId, out var segment))
            return;
        segment.Writer?.Dispose();
        _closedUncommitted.Add(segment.Id);
    }

    public void CloseAll()
    {
        foreach (var segment in _open.Values)
        {
            segment.Writer?.Dispose();
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
            segment.Writer?.Dispose();
        _open.Clear();

        // Files made for captures that were never committed (the recorder disposes the sink after a tick fails): no
        // segment refers to them, so nothing would ever show or delete them. An image left behind is cleaned up the
        // next time a sink starts; nothing else can be done about the rest here.
        foreach (var (path, _) in _filesUncommitted)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _filesUncommitted.Clear();
        Encoder.Dispose();
    }

    private FrameRef AppendImage(long monitorId, ScreenCapture capture, DateTimeOffset timestamp, FrameFormat format, int quality)
    {
        using var image = ImageCodec.Encode(format, capture.Frame.Span, capture.Width, capture.Height, quality);
        var relative = NewPath(ImagesFolder, timestamp, monitorId, ImageCodec.Extension(format));
        var final = Path.Combine(DataFolder, relative);
        var temp = final + TempSuffix;

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1))
        {
            _filesUncommitted.Add((temp, final));
            file.Write(image.Data);
        }

        var id = _store.InsertSegment(monitorId, relative, capture.Width, capture.Height, quality, timestamp, SegmentState.Staged, format);
        _store.InsertSegmentFrame(id, 0, fileOffset: 0, image.Length, isKey: true);
        _open[monitorId] = new OpenSegment(id, format, writer: null);
        return new FrameRef(id, 0);
    }

    private OpenSegment OpenVideo(long monitorId, int width, int height, int quality, DateTimeOffset start)
    {
        var relative = NewPath(VideoFolder, start, monitorId, ".ivf");
        var stagingPath = StagingFile.PathFor(Path.Combine(DataFolder, relative));
        var writer = new StagingWriter(stagingPath, width, height);
        _filesUncommitted.Add((stagingPath, null));
        try
        {
            var id = _store.InsertSegment(monitorId, relative, width, height, quality, start, SegmentState.Staged);
            return new OpenSegment(id, FrameFormat.Video, writer);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// "video/2026-10-17/153000-m3.ivf", after the segment's first capture and its monitor; "-2" and so on when a
    /// name is taken (the clock went back an hour, or recording restarted within the second).
    /// </summary>
    private string NewPath(string kind, DateTimeOffset start, long monitorId, string extension)
    {
        var folder = $"{kind}/{start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var name = $"{start.ToString("HHmmss", CultureInfo.InvariantCulture)}-m{monitorId}";
        for (var n = 1; ; n++)
        {
            var relative = $"{folder}/{name}{(n == 1 ? "" : $"-{n}")}{extension}";
            var full = Path.Combine(DataFolder, relative);
            if (!File.Exists(full) && !File.Exists(StagingFile.PathFor(full)) && !File.Exists(full + TempSuffix))
                return relative;
        }
    }

    /// <summary>
    /// Finishes the images a crash interrupted. Each is written under a temporary name and renamed once the capture
    /// showing it is committed, so one still named so belongs either to a committed capture (renamed now) or to one
    /// that never was (deleted: nothing would ever show it, or delete it). They can only be in the folder of the
    /// last image recorded or a later one; the day before is looked at too, in case the clock went back.
    /// </summary>
    private static void FinishInterruptedImages(WorklapseDatabase db)
    {
        var root = Path.Combine(db.DataFolder, ImagesFolder);
        if (!Directory.Exists(root))
            return;

        var newest = db.Connection.ExecuteScalar<string?>("SELECT max(path) FROM segments WHERE path >= $from AND path < $to;",
            new { from = ImagesFolder + "/", to = ImagesFolder + "0" }); // '0' follows '/'
        var fromDay = newest?.Split('/') is [_, var day, ..]
            && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (string.CompareOrdinal(Path.GetFileName(folder), fromDay) < 0)
                continue;
            foreach (var temp in Directory.EnumerateFiles(folder).Where(f => f.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                var final = temp[..^TempSuffix.Length];
                var committed = db.Connection.ExecuteScalar<long?>("SELECT 1 FROM segments WHERE path = $path;",
                    new { path = Path.GetRelativePath(db.DataFolder, final).Replace('\\', '/') });
                if (committed is not null)
                    File.Move(temp, final, overwrite: true);
                else
                    File.Delete(temp);
            }
        }
    }

    private sealed class OpenSegment(long id, FrameFormat format, StagingWriter? writer)
    {
        public long Id { get; } = id;
        public FrameFormat Format { get; } = format;
        public StagingWriter? Writer { get; } = writer;
        public int FrameCount { get; set; }

        /// <summary>Whether the next frame of this format and size goes in this segment. An image holds only one.</summary>
        public bool Takes(FrameFormat format, int width, int height) =>
            Format == FrameFormat.Video && format == FrameFormat.Video
            && Writer!.Width == width && Writer.Height == height && FrameCount < FramesPerSegment;
    }
}
