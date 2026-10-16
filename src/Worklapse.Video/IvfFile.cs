using System.Buffers.Binary;

namespace Worklapse.Video;

/// <summary>
/// The IVF container: a 32-byte file header, then each frame as a 12-byte header (size, timestamp) followed
/// by its data. Simple enough that a file cut short by a crash is still readable up to its last whole frame,
/// and standard enough that ffmpeg and other players can open a segment directly.
/// </summary>
public static class IvfFile
{
    public const int FileHeaderSize = 32;
    public const int FrameHeaderSize = 12;
    public const int TimebaseDenominator = 1000; // timestamps are milliseconds
}

public sealed class IvfWriter : IDisposable
{
    private readonly FileStream _stream;
    private uint _frameCount;

    public IvfWriter(string path, int width, int height)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "IVF frame size must fit in 16 bits.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

        Span<byte> header = stackalloc byte[IvfFile.FileHeaderSize];
        header.Clear();
        "DKIF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], IvfFile.FileHeaderSize);
        "AV01"u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], IvfFile.TimebaseDenominator);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1);
        _stream.Write(header);
        _stream.Flush();
    }

    /// <summary>Appends a frame and returns the file offset of its data (just past its frame header).</summary>
    public long WriteFrame(long ptsMs, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[IvfFile.FrameHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[4..], ptsMs);
        _stream.Write(header);
        var offset = _stream.Position;
        _stream.Write(data);
        // Each frame reaches the OS before the database row pointing at it is committed.
        _stream.Flush();
        _frameCount++;
        return offset;
    }

    public void Dispose()
    {
        // The frame count in the header is informational; readers don't rely on it (a crash leaves it at 0).
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, _frameCount);
        _stream.Position = 24;
        _stream.Write(count);
        _stream.Dispose();
    }
}

public static class IvfReader
{
    /// <summary>Reads a frame's data, given the offset recorded when it was written.</summary>
    public static byte[] ReadFrame(FileStream stream, long offset, int length)
    {
        var data = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>Lists every whole frame in a file (offset of its data, length, timestamp).</summary>
    public static IEnumerable<(long Offset, int Length, long PtsMs)> EnumerateFrames(FileStream stream)
    {
        var header = new byte[IvfFile.FileHeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("DKIF"u8))
            throw new InvalidDataException("Not an IVF file.");

        var position = (long)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var frameHeader = new byte[IvfFile.FrameHeaderSize];
        while (position + IvfFile.FrameHeaderSize <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(frameHeader);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
            var pts = BinaryPrimitives.ReadInt64LittleEndian(frameHeader.AsSpan(4));
            var dataOffset = position + IvfFile.FrameHeaderSize;
            if (dataOffset + length > stream.Length)
                yield break; // a frame cut short by a crash
            yield return (dataOffset, length, pts);
            position = dataOffset + length;
        }
    }
}
