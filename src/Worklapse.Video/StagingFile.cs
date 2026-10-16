using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Worklapse.Video;

/// <summary>
/// Holds a segment's captures until the segment is encoded to AV1. The first frame is stored whole and
/// each later one as the XOR with the frame before it; both are Brotli-compressed (quality 1,
/// as fast as Deflate's fastest level and much smaller on screen content). Unchanged screen
/// areas XOR to zero, so a capture that differs in a few places costs a few kilobytes.
/// <para>
/// Layout: a 16-byte header ("WLST", version, width, height), then per frame a 13-byte record header
/// (payload length, timestamp, kind) followed by the compressed payload. Frames can only be rebuilt in
/// order from the first.
/// </para>
/// </summary>
public static class StagingFile
{
    public const int HeaderSize = 16;
    public const int RecordHeaderSize = 13;
    private const int Version = 2;

    internal enum FrameKind : byte
    {
        Full = 0,
        Delta = 1,
    }

    public static string PathFor(string segmentPath) => segmentPath + ".staging";

    internal static void WriteHeader(Stream stream, int width, int height)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "WLST"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], height);
        stream.Write(header);
    }

    internal static (int Width, int Height) ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("WLST"u8) || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version)
            throw new InvalidDataException("Not a Worklapse staging file, or an unsupported version.");
        return (BinaryPrimitives.ReadInt32LittleEndian(header[8..]), BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
    }

    /// <summary>XORs <paramref name="other"/> into <paramref name="target"/>.</summary>
    internal static void Xor(Span<byte> target, ReadOnlySpan<byte> other)
    {
        var t = MemoryMarshal.Cast<byte, Vector<byte>>(target);
        var o = MemoryMarshal.Cast<byte, Vector<byte>>(other);
        for (var i = 0; i < t.Length; i++)
            t[i] ^= o[i];
        for (var i = t.Length * Vector<byte>.Count; i < target.Length; i++)
            target[i] ^= other[i];
    }
}

/// <summary>
/// Appends captures to a staging file. Keeps one copy of the previous frame to compute deltas.
/// </summary>
public sealed class StagingWriter : IDisposable
{
    private const int BrotliQuality = 1;

    private readonly FileStream _stream;
    private readonly byte[] _previous;
    private readonly MemoryStream _payload = new();
    private bool _hasPrevious;

    public StagingWriter(string path, int width, int height)
    {
        Width = width;
        Height = height;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        StagingFile.WriteHeader(_stream, width, height);
        _stream.Flush();
        _previous = new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// Appends a frame (BGRA, rows packed: stride = width × 4); returns where its record starts and its
    /// total length.
    /// </summary>
    public (long Offset, int Length) WriteFrame(ReadOnlySpan<byte> bgra, long ptsMs)
    {
        if (bgra.Length != _previous.Length)
            throw new ArgumentException("The frame must be width × height × 4 bytes with packed rows.", nameof(bgra));

        // For a delta, XOR the new frame into the previous one, compress that, then make the previous
        // frame a copy of the new one for next time. One buffer per monitor is enough.
        var kind = StagingFile.FrameKind.Full;
        var payloadSource = bgra;
        if (_hasPrevious)
        {
            StagingFile.Xor(_previous, bgra);
            payloadSource = _previous;
            kind = StagingFile.FrameKind.Delta;
        }

        // One growable buffer reused for every frame, so a full frame's megabytes aren't reallocated each time.
        var payload = _payload;
        payload.SetLength(0);
        using (var brotli = new BrotliStream(payload, new BrotliCompressionOptions { Quality = BrotliQuality }, leaveOpen: true))
            brotli.Write(payloadSource);

        bgra.CopyTo(_previous);
        _hasPrevious = true;

        var offset = _stream.Position;
        Span<byte> header = stackalloc byte[StagingFile.RecordHeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, (int)payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[4..], ptsMs);
        header[12] = (byte)kind;
        _stream.Write(header);
        _stream.Write(payload.GetBuffer().AsSpan(0, (int)payload.Length));
        // Each frame reaches the OS before the database row pointing at it is committed.
        _stream.Flush();
        return (offset, StagingFile.RecordHeaderSize + (int)payload.Length);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Rebuilds staged frames in order. Holds one frame (the latest rebuilt) in memory.</summary>
public sealed class StagingReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _frame;
    private byte[] _payload = [];
    private bool _hasFrame;

    public StagingReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        (Width, Height) = StagingFile.ReadHeader(_stream);
        _frame = new byte[Width * Height * 4];
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The most recently rebuilt frame, BGRA with a stride of Width × 4.</summary>
    public ReadOnlySpan<byte> Frame => _frame;

    /// <summary>Timestamp (Unix milliseconds) of the most recently rebuilt frame.</summary>
    public long FramePtsMs { get; private set; }

    /// <summary>
    /// Applies the frame record at <paramref name="offset"/>. Records must be applied in order starting
    /// from the first (a full frame); call <see cref="Reset"/> to start over.
    /// </summary>
    public void Apply(long offset, int length)
    {
        if (length < StagingFile.RecordHeaderSize)
            throw new InvalidDataException("Staging record is too short.");
        if (_payload.Length < length)
            _payload = new byte[length];

        _stream.Position = offset;
        _stream.ReadExactly(_payload.AsSpan(0, length));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(_payload);
        var pts = BinaryPrimitives.ReadInt64LittleEndian(_payload.AsSpan(4));
        var kind = (StagingFile.FrameKind)_payload[12];
        if (payloadLength != length - StagingFile.RecordHeaderSize)
            throw new InvalidDataException("Staging record length mismatch.");
        if (kind == StagingFile.FrameKind.Delta && !_hasFrame)
            throw new InvalidOperationException("A delta frame needs the frames before it applied first.");

        using var input = new MemoryStream(_payload, StagingFile.RecordHeaderSize, payloadLength, writable: false);
        using var inflate = new BrotliStream(input, CompressionMode.Decompress);
        if (kind == StagingFile.FrameKind.Full)
        {
            inflate.ReadExactly(_frame);
        }
        else
        {
            // Inflate the delta in chunks and XOR it into the current frame.
            Span<byte> chunk = stackalloc byte[64 * 1024];
            var position = 0;
            while (position < _frame.Length)
            {
                var read = inflate.Read(chunk[..Math.Min(chunk.Length, _frame.Length - position)]);
                if (read == 0)
                    throw new InvalidDataException("Staging delta is truncated.");
                StagingFile.Xor(_frame.AsSpan(position, read), chunk[..read]);
                position += read;
            }
        }
        _hasFrame = true;
        FramePtsMs = pts;
    }

    public void Reset() => _hasFrame = false;

    public void Dispose() => _stream.Dispose();
}
