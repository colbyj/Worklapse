using System.Buffers.Binary;
using System.Text;

namespace Worklapse.Video;

/// <summary>
/// Writes an AV1 video to an MP4 file (ISO base media file format, with the "AV1 Codec ISO Media File Format Binding"):
/// the file type, then the frames one after another as they come, then the index of where each frame is. One video
/// track at a constant frame rate, nothing else. The file is only playable once <see cref="Finish"/> has written the
/// index.
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    /// <summary>Media time units per frame; the track's timescale is this times the frame rate.</summary>
    private const int TicksPerFrame = 100;

    private const int MovieTimescale = 1000;

    private readonly Stream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly int _framesPerSecond;
    private readonly long _mediaDataStart;   // where the mdat box starts
    private readonly List<long> _offsets = [];
    private readonly List<int> _sizes = [];
    private readonly List<int> _keyFrames = [];   // 1-based sample numbers
    private byte[]? _sequenceHeader;               // the whole OBU, for the av1C box
    private Av1SequenceInfo? _sequence;
    private bool _finished;

    /// <param name="stream">A new, seekable stream to write to; the writer disposes it.</param>
    public Mp4Writer(Stream stream, int width, int height, int framesPerSecond)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("The stream must be seekable.", nameof(stream));
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "The frame size must fit in 16 bits.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);

        _stream = stream;
        _width = width;
        _height = height;
        _framesPerSecond = framesPerSecond;

        var box = new BoxWriter();
        box.Begin("ftyp");
        box.FourCc("isom");
        box.UInt32(0x200);
        foreach (var brand in new[] { "isom", "iso2", "av01", "mp41" })
            box.FourCc(brand);
        box.End();
        _stream.Write(box.ToArray());

        // The frames' box, with a 64-bit size filled in by Finish.
        _mediaDataStart = _stream.Position;
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1); // 1: the size is in the 64 bits after the type
        "mdat"u8.CopyTo(header[4..]);
        _stream.Write(header);
    }

    public int FrameCount => _sizes.Count;

    /// <summary>Appends the next frame, as the encoder returned it. The first must be a key frame.</summary>
    public void WriteFrame(EncodedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var data = frame.Data.Span;
        var units = Av1Obu.Split(data);

        if (_sequenceHeader is null)
        {
            if (!frame.IsKey)
                throw new ArgumentException("The first frame must be a key frame.", nameof(frame));
            var header = units.FirstOrDefault(u => u.Type == Av1Obu.SequenceHeader);
            if (header.Length == 0)
                throw new InvalidDataException("The first frame has no sequence header.");
            _sequenceHeader = data.Slice(header.Offset, header.Length).ToArray();
            _sequence = Av1SequenceInfo.Parse(Av1Obu.Payload(data, header));
        }

        // Temporal delimiters mark where frames start in a raw stream; in MP4 the index does, so samples leave them out.
        var offset = _stream.Position;
        var size = 0;
        foreach (var unit in units)
        {
            if (unit.Type == Av1Obu.TemporalDelimiter)
                continue;
            _stream.Write(data.Slice(unit.Offset, unit.Length));
            size += unit.Length;
        }
        _offsets.Add(offset);
        _sizes.Add(size);
        if (frame.IsKey)
            _keyFrames.Add(_sizes.Count);
    }

    /// <summary>Writes the index after the frames, completing the file.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_sequence is null)
            throw new InvalidOperationException("A video needs at least one frame.");
        _finished = true;

        var end = _stream.Position;
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)(end - _mediaDataStart));
        _stream.Position = _mediaDataStart + 8;
        _stream.Write(size);
        _stream.Position = end;

        _stream.Write(Movie());
        _stream.Flush();
    }

    public void Dispose()
    {
        _finished = true;
        _stream.Dispose();
    }

    private byte[] Movie()
    {
        var mediaTimescale = (uint)(_framesPerSecond * TicksPerFrame);
        var mediaDuration = (ulong)_sizes.Count * TicksPerFrame;
        var movieDuration = (ulong)Math.Ceiling(_sizes.Count * (double)MovieTimescale / _framesPerSecond);
        var box = new BoxWriter();

        box.Begin("moov");

        box.BeginFull("mvhd", version: 1, flags: 0);
        box.UInt64(0); // creation time
        box.UInt64(0); // modification time
        box.UInt32(MovieTimescale);
        box.UInt64(movieDuration);
        box.UInt32(0x00010000); // playback rate 1.0
        box.UInt16(0x0100);     // volume 1.0
        box.Zeros(10);
        box.Matrix();
        box.Zeros(24);
        box.UInt32(2); // next track id
        box.End();

        box.Begin("trak");

        box.BeginFull("tkhd", version: 1, flags: 0x3); // enabled, in the movie
        box.UInt64(0);
        box.UInt64(0);
        box.UInt32(1); // track id
        box.UInt32(0);
        box.UInt64(movieDuration);
        box.Zeros(8);
        box.UInt16(0); // layer
        box.UInt16(0); // alternate group
        box.UInt16(0); // volume (none: video)
        box.UInt16(0);
        box.Matrix();
        box.UInt32((uint)_width << 16);  // 16.16 fixed point
        box.UInt32((uint)_height << 16);
        box.End();

        box.Begin("mdia");

        box.BeginFull("mdhd", version: 1, flags: 0);
        box.UInt64(0);
        box.UInt64(0);
        box.UInt32(mediaTimescale);
        box.UInt64(mediaDuration);
        box.UInt16(0x55C4); // language "und", packed as three 5-bit letters
        box.UInt16(0);
        box.End();

        box.BeginFull("hdlr", version: 0, flags: 0);
        box.UInt32(0);
        box.FourCc("vide");
        box.Zeros(12);
        box.Bytes("VideoHandler\0"u8);
        box.End();

        box.Begin("minf");

        box.BeginFull("vmhd", version: 0, flags: 1);
        box.Zeros(8); // graphics mode, opcolor
        box.End();

        box.Begin("dinf");
        box.BeginFull("dref", version: 0, flags: 0);
        box.UInt32(1);
        box.BeginFull("url ", version: 0, flags: 1); // the media data is in this file
        box.End();
        box.End();
        box.End();

        box.Begin("stbl");
        SampleDescription(box);

        box.BeginFull("stts", version: 0, flags: 0); // every frame lasts the same
        box.UInt32(1);
        box.UInt32((uint)_sizes.Count);
        box.UInt32(TicksPerFrame);
        box.End();

        box.BeginFull("stss", version: 0, flags: 0); // the key frames
        box.UInt32((uint)_keyFrames.Count);
        foreach (var sample in _keyFrames)
            box.UInt32((uint)sample);
        box.End();

        box.BeginFull("stsc", version: 0, flags: 0); // each frame is a chunk of its own
        box.UInt32(1);
        box.UInt32(1); // first chunk
        box.UInt32(1); // frames per chunk
        box.UInt32(1); // sample description
        box.End();

        box.BeginFull("stsz", version: 0, flags: 0);
        box.UInt32(0); // sizes differ: listed
        box.UInt32((uint)_sizes.Count);
        foreach (var size in _sizes)
            box.UInt32((uint)size);
        box.End();

        box.BeginFull("co64", version: 0, flags: 0);
        box.UInt32((uint)_offsets.Count);
        foreach (var offset in _offsets)
            box.UInt64((ulong)offset);
        box.End();

        box.End(); // stbl
        box.End(); // minf
        box.End(); // mdia
        box.End(); // trak
        box.End(); // moov
        return box.ToArray();
    }

    private void SampleDescription(BoxWriter box)
    {
        var sequence = _sequence!;
        box.BeginFull("stsd", version: 0, flags: 0);
        box.UInt32(1);

        box.Begin("av01");
        box.Zeros(6);
        box.UInt16(1); // data reference
        box.Zeros(16);
        box.UInt16((ushort)_width);
        box.UInt16((ushort)_height);
        box.UInt32(0x00480000); // 72 dpi
        box.UInt32(0x00480000);
        box.UInt32(0);
        box.UInt16(1); // frames per sample
        var compressor = new byte[32];
        var name = "AOM Coding"u8;
        compressor[0] = (byte)name.Length;
        name.CopyTo(compressor.AsSpan(1));
        box.Bytes(compressor);
        box.UInt16(0x0018); // depth: colour, no alpha
        box.UInt16(0xFFFF);

        box.Begin("av1C");
        box.Byte(0x81); // marker, version 1
        box.Byte((byte)(sequence.Profile << 5 | sequence.Level));
        box.Byte((byte)(sequence.Tier << 7
            | (sequence.HighBitDepth ? 1 : 0) << 6
            | (sequence.TwelveBit ? 1 : 0) << 5
            | (sequence.Monochrome ? 1 : 0) << 4
            | (sequence.SubsamplingX ? 1 : 0) << 3
            | (sequence.SubsamplingY ? 1 : 0) << 2
            | sequence.ChromaSamplePosition));
        box.Byte(0); // no initial presentation delay
        box.Bytes(_sequenceHeader);
        box.End();

        box.Begin("colr");
        box.FourCc("nclx");
        box.UInt16((ushort)sequence.ColorPrimaries);
        box.UInt16((ushort)sequence.TransferCharacteristics);
        box.UInt16((ushort)sequence.MatrixCoefficients);
        box.Byte((byte)(sequence.FullRange ? 0x80 : 0));
        box.End();

        box.End(); // av01
        box.End(); // stsd
    }

    /// <summary>Builds nested boxes in memory, filling in each box's size when it ends.</summary>
    private sealed class BoxWriter
    {
        private readonly MemoryStream _buffer = new();
        private readonly Stack<long> _open = new();

        public void Begin(string type)
        {
            _open.Push(_buffer.Position);
            UInt32(0);
            FourCc(type);
        }

        public void BeginFull(string type, byte version, uint flags)
        {
            Begin(type);
            UInt32((uint)version << 24 | flags);
        }

        public void End()
        {
            var start = _open.Pop();
            var end = _buffer.Position;
            _buffer.Position = start;
            UInt32((uint)(end - start));
            _buffer.Position = end;
        }

        public void FourCc(string code) => Bytes(Encoding.ASCII.GetBytes(code));

        public void Byte(byte value) => _buffer.WriteByte(value);

        public void UInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void UInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void Zeros(int count)
        {
            for (var i = 0; i < count; i++)
                _buffer.WriteByte(0);
        }

        public void Bytes(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

        /// <summary>The identity transformation matrix of movie and track headers.</summary>
        public void Matrix()
        {
            foreach (var value in new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 })
                UInt32(value);
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}
