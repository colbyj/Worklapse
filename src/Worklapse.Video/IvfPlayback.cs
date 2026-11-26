using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Worklapse.Video;

/// <summary>One recorded .ivf file, read without a database: its frames and where each one is in the file.</summary>
public sealed class IvfSegmentFile
{
    internal IvfSegmentFile(string path, int width, int height, IReadOnlyList<(long Offset, int Length, long PtsMs)> frames)
    {
        Path = path;
        Width = width;
        Height = height;
        Frames = frames;
    }

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<(long Offset, int Length, long PtsMs)> Frames { get; }
}

/// <summary>A frame of a file, with the moment it was captured (in the recording's own time zone when known).</summary>
public sealed record IvfFrame(IvfSegmentFile File, int Index, DateTimeOffset Time);

/// <summary>One monitor's frames, from all the files opened, in time order.</summary>
public sealed record IvfTrack(string Name, IReadOnlyList<IvfFrame> Frames);

/// <summary>What was found when opening a file or folder.</summary>
public sealed record IvfRecording(IReadOnlyList<IvfTrack> Tracks, int FileCount, IReadOnlyList<string> Skipped);

/// <summary>
/// One or more monitors played together: every moment at which any of them has a frame, in order, and what each
/// shows then (its latest frame so far, since a monitor that didn't change gets no new frame).
/// </summary>
public sealed class IvfPlaylist
{
    private readonly int[][] _frameIndexes; // per track, per moment: index into its frames, -1 before its first

    public IvfPlaylist(IReadOnlyList<IvfTrack> tracks)
    {
        Tracks = tracks;
        Times = tracks
            .SelectMany(t => t.Frames.Select(f => f.Time))
            .DistinctBy(t => t.ToUnixTimeMilliseconds())
            .OrderBy(t => t)
            .ToList();

        _frameIndexes = new int[tracks.Count][];
        for (var t = 0; t < tracks.Count; t++)
        {
            var frames = tracks[t].Frames;
            var indexes = _frameIndexes[t] = new int[Times.Count];
            var next = 0;
            for (var m = 0; m < Times.Count; m++)
            {
                while (next < frames.Count && frames[next].Time <= Times[m])
                    next++;
                indexes[m] = next - 1;
            }
        }
    }

    public IReadOnlyList<IvfTrack> Tracks { get; }

    /// <summary>The moments to step through.</summary>
    public IReadOnlyList<DateTimeOffset> Times { get; }

    /// <summary>What each track shows at a moment, in the order of <see cref="Tracks"/>; null before its first frame.</summary>
    public IvfFrame?[] FramesAt(int moment)
    {
        var frames = new IvfFrame?[Tracks.Count];
        for (var t = 0; t < Tracks.Count; t++)
        {
            var index = _frameIndexes[t][moment];
            frames[t] = index >= 0 ? Tracks[t].Frames[index] : null;
        }
        return frames;
    }

    /// <summary>Whether a track has a new frame at this moment (its screen changed then).</summary>
    public bool ChangesAt(int track, int moment) =>
        _frameIndexes[track][moment] >= 0 && (moment == 0 || _frameIndexes[track][moment] != _frameIndexes[track][moment - 1]);
}

/// <summary>
/// Opens Worklapse's .ivf segments on their own, without the database that normally points into them: for looking at
/// recordings someone sent. A file is "HHmmss-m&lt;monitor&gt;.ivf" (or "-r1" and so on once rebuilt) in a "YYYY-MM-DD"
/// folder; each frame's timestamp is its capture time in Unix milliseconds. A capture that didn't change a monitor
/// adds no frame, so frames can be far apart.
/// </summary>
public static partial class IvfLibrary
{
    /// <summary>Reads one .ivf file, or every .ivf file in a folder and the folders below it.</summary>
    public static IvfRecording Open(string path)
    {
        var paths = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.ivf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [path];

        var skipped = new List<string>();
        var files = new List<(IvfSegmentFile File, string Monitor)>();
        foreach (var file in paths)
        {
            try
            {
                if (Read(file) is { } segment)
                    files.Add((segment, MonitorName(file)));
                else
                    skipped.Add($"{System.IO.Path.GetFileName(file)}: not an AV1 recording, or has no frames");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                skipped.Add($"{System.IO.Path.GetFileName(file)}: {ex.Message}");
            }
        }

        var offset = RecordingOffset(files.Select(f => f.File));
        var tracks = files
            .GroupBy(f => f.Monitor)
            .Select(g => new IvfTrack(g.Key, g
                .SelectMany(f => f.File.Frames.Select((frame, i) =>
                    new IvfFrame(f.File, i, DateTimeOffset.FromUnixTimeMilliseconds(frame.PtsMs).ToOffset(offset))))
                .OrderBy(f => f.Time)
                .ToList()))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        return new IvfRecording(tracks, files.Count, skipped);
    }

    /// <summary>The file's frames, or null if it isn't an AV1 IVF file or has no whole frame.</summary>
    internal static IvfSegmentFile? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < IvfFile.FileHeaderSize)
            return null;
        Span<byte> header = stackalloc byte[IvfFile.FileHeaderSize];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("DKIF"u8) || !header[8..12].SequenceEqual("AV01"u8))
            return null;
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        var frames = IvfReader.EnumerateFrames(stream).ToList();
        return frames.Count == 0 ? null : new IvfSegmentFile(path, width, height, frames);
    }

    /// <summary>"Monitor 3" for "153000-m3.ivf" or "153000-m3-r1.ivf"; otherwise the file's name.</summary>
    internal static string MonitorName(string path)
    {
        var match = SegmentName().Match(System.IO.Path.GetFileName(path));
        return match.Success ? $"Monitor {match.Groups["monitor"].Value}" : System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// The time zone the recording was made in. A file's name is its first capture's local time and its folder the
    /// local date, so the difference from that frame's UTC timestamp gives it (rebuilt files are left out: their first
    /// frame can come later). Falls back to this computer's time zone.
    /// </summary>
    internal static TimeSpan RecordingOffset(IEnumerable<IvfSegmentFile> files)
    {
        var offsets = new List<TimeSpan>();
        foreach (var file in files)
        {
            var match = SegmentName().Match(System.IO.Path.GetFileName(file.Path));
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file.Path));
            if (!match.Success || match.Groups["rebuilt"].Success
                || !DateTime.TryParseExact($"{folder} {match.Groups["time"].Value}", "yyyy-MM-dd HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                continue;
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(file.Frames[0].PtsMs).UtcDateTime;
            // Offsets are whole quarter hours; the name is cut to the second.
            var minutes = Math.Round((local - utc).TotalMinutes / 15) * 15;
            if (Math.Abs(minutes) <= 14 * 60)
                offsets.Add(TimeSpan.FromMinutes(minutes));
        }
        return offsets.Count > 0
            ? offsets.GroupBy(o => o).MaxBy(g => g.Count())!.Key
            : TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
    }

    [GeneratedRegex(@"^(?<time>\d{6})-m(?<monitor>\d+)(-\d+)?(?<rebuilt>-r\d+)?\.ivf$", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentName();
}

/// <summary>
/// Decodes one monitor's frames, from files opened with <see cref="IvfLibrary"/>. Each file starts with its only key frame, so one
/// decoder stays positioned in the file last used: stepping forward decodes one frame, stepping back decodes again
/// from the start of the file, keeping the frames just before on the way. Not thread-safe.
/// </summary>
public sealed class IvfFrameSource : IDisposable
{
    private const int FramesKeptWhenSteppingBack = 2;
    public const long DefaultCacheBudgetBytes = 64L * 1024 * 1024;

    private readonly long _cacheBudgetBytes;
    private readonly FramePool _pool = new();
    private readonly LinkedList<(IvfSegmentFile File, int Index, DecodedFrame Frame)> _cache = new();
    private long _cacheBytes;
    private Av1Decoder? _decoder;
    private FileStream? _stream;
    private IvfSegmentFile? _file;
    private int _position = -1;
    private byte[] _buffer = [];
    private DateTime _lastUsed;

    /// <param name="cacheBudgetBytes">Room for recent frames, so stepping back a little is usually free.</param>
    public IvfFrameSource(long cacheBudgetBytes = DefaultCacheBudgetBytes) => _cacheBudgetBytes = cacheBudgetBytes;

    /// <summary>Returns the frame with a reference the caller owns: call <see cref="DecodedFrame.Release"/> when done.</summary>
    public DecodedFrame Get(IvfFrame frame)
    {
        _lastUsed = DateTime.UtcNow;
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.File == frame.File && node.Value.Index == frame.Index)
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value.Frame.AddReference();
            }
        }

        var restarted = false;
        if (_file != frame.File || frame.Index <= _position || _decoder is null)
        {
            Close();
            _file = frame.File;
            _stream = new FileStream(frame.File.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _decoder = new Av1Decoder(threads: 1);
            restarted = true;
        }

        var keepFrom = restarted ? frame.Index - FramesKeptWhenSteppingBack : frame.Index;
        DecodedFrame? decoded = null;
        try
        {
            while (_position < frame.Index)
            {
                var (offset, length, _) = _file.Frames[_position + 1];
                _decoder.Decode(IvfReader.ReadFrame(_stream!, offset, length, ref _buffer));
                _position++;
                if (_position < keepFrom)
                    continue;
                var copy = _pool.Rent(_decoder.Width, _decoder.Height);
                _decoder.CopyI420(copy.Writable);
                Remember(frame.File, _position, copy);
                decoded = copy;
            }
        }
        catch
        {
            // The decoder is somewhere in the file now; start again from its key frame next time.
            Close();
            throw;
        }
        return decoded!.AddReference();
    }

    /// <summary>Closes the decoder if it hasn't been used for <paramref name="idle"/>, to give its memory back.</summary>
    public void CloseIdle(TimeSpan idle)
    {
        if (_decoder is not null && DateTime.UtcNow - _lastUsed >= idle)
        {
            Close();
            NativeHeap.Trim();
        }
    }

    public void Dispose()
    {
        Close();
        NativeHeap.Trim();
        foreach (var entry in _cache)
            entry.Frame.Release();
        _cache.Clear();
        _pool.Clear();
    }

    private void Remember(IvfSegmentFile file, int index, DecodedFrame frame)
    {
        var bytes = Core.Imaging.I420.Size(frame.Width, frame.Height);
        while (_cache.Count > 0 && _cacheBytes + bytes > _cacheBudgetBytes)
        {
            var last = _cache.Last!.Value;
            _cache.RemoveLast();
            _cacheBytes -= Core.Imaging.I420.Size(last.Frame.Width, last.Frame.Height);
            last.Frame.Release();
        }
        _cache.AddFirst((file, index, frame));
        _cacheBytes += bytes;
    }

    private void Close()
    {
        _decoder?.Dispose();
        _decoder = null;
        _stream?.Dispose();
        _stream = null;
        _file = null;
        _position = -1;
    }
}
