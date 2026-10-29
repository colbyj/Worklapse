using System.Buffers.Binary;
using System.Text;
using Worklapse.Core.Export;
using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

public class TimelapseTests
{
    private const int Width = 320, Height = 180;

    // --- the MP4 file ---------------------------------------------------------------------------

    [Fact]
    public void Mp4FramesDecodeAndKeyFramesAreIndexed()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "a.mp4");
        const int frames = 7;
        using (var encoder = new Av1Encoder(Width, Height, quality: 30, keyFrameInterval: 3, limitedRange: true))
        using (var writer = new Mp4Writer(File.Create(path), Width, Height, framesPerSecond: 30))
        {
            for (var i = 0; i < frames; i++)
                writer.WriteFrame(encoder.Encode(Frame(i), i * 33));
            writer.Finish();
        }

        var file = File.ReadAllBytes(path);
        var top = Boxes(file, 0, file.Length);
        Assert.Equal(["ftyp", "mdat", "moov"], top.Select(b => b.Type));
        Assert.Equal(file.Length, top.Sum(b => b.Size)); // the mdat's size was filled in

        var stbl = Find(file, top, "moov", "trak", "mdia", "minf", "stbl");
        var sizes = Table(file, Find(file, stbl, "stsz"), headerFields: 2).ToList();
        var offsets = Table64(file, Find(file, stbl, "co64")).ToList();
        var keys = Table(file, Find(file, stbl, "stss"), headerFields: 1).ToList();
        Assert.Equal(frames, sizes.Count);
        Assert.Equal(frames, offsets.Count);
        Assert.Equal([1L, 4, 7], keys);

        using var decoder = new Av1Decoder();
        for (var i = 0; i < frames; i++)
        {
            var sample = file.AsSpan((int)offsets[i], (int)sizes[i]);
            Assert.DoesNotContain(Av1Obu.Split(sample), u => u.Type == Av1Obu.TemporalDelimiter);
            decoder.Decode(sample);
            Assert.Equal((Width, Height), (decoder.Width, decoder.Height));
        }

        // The codec configuration starts with the sequence header; colours are BT.709 limited range.
        var av1C = Find(file, Find(file, stbl, "stsd"), "av01");
        var config = Boxes(file, av1C.Start + 8 + 78, av1C.End).Single(b => b.Type == "av1C");
        Assert.Equal(0x81, file[config.Start + 8]);
        Assert.Equal(Av1Obu.SequenceHeader, (file[config.Start + 12] >> 3) & 0xF);
        var colr = Boxes(file, av1C.Start + 8 + 78, av1C.End).Single(b => b.Type == "colr");
        var nclx = file.AsSpan(colr.Start + 8);
        Assert.Equal("nclx", Encoding.ASCII.GetString(nclx[..4]));
        Assert.Equal((1, 1, 1, 0), (BinaryPrimitives.ReadUInt16BigEndian(nclx[4..]), BinaryPrimitives.ReadUInt16BigEndian(nclx[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(nclx[8..]), nclx[10]));
    }

    [Theory]
    [InlineData(false, 13, true)]  // recorded: sRGB transfer, full range
    [InlineData(true, 1, false)]   // exported: BT.709, limited range
    public void SequenceHeaderSaysHowTheStreamIsEncoded(bool limitedRange, int transfer, bool fullRange)
    {
        using var encoder = new Av1Encoder(Width, Height, quality: 30, limitedRange: limitedRange);
        var frame = encoder.Encode(Frame(0), 0).Data.Span;
        var header = Av1Obu.Split(frame).Single(u => u.Type == Av1Obu.SequenceHeader);
        var info = Av1SequenceInfo.Parse(Av1Obu.Payload(frame, header));

        Assert.Equal(0, info.Profile);
        Assert.False(info.HighBitDepth || info.Monochrome);
        Assert.True(info.SubsamplingX && info.SubsamplingY);
        Assert.Equal((1, transfer, 1, fullRange), (info.ColorPrimaries, info.TransferCharacteristics, info.MatrixCoefficients, info.FullRange));
    }

    // --- pictures -------------------------------------------------------------------------------

    [Fact]
    public void ReduceAveragesBlocksIncludingPartOnesAtTheEdges()
    {
        // 5 × 3 luma, chroma 3 × 2; by 2: luma 3 × 2, chroma 2 × 1.
        byte[] frame =
        [
            0, 10, 20, 30, 40,
            10, 20, 30, 40, 50,
            100, 100, 200, 200, 255,
            1, 2, 3, 4, 5, 6,   // U
            7, 8, 9, 10, 11, 12, // V
        ];
        var reduced = new byte[I420.Size(3, 2)];
        I420.Reduce(frame, 5, 3, 2, reduced);
        Assert.Equal((3, 2), I420.ReducedSize(5, 3, 2));
        Assert.Equal(new byte[]
        {
            10, 30, 45,     // (0+10+10+20)/4, (20+30+30+40)/4, (40+50)/2
            100, 200, 255,  // the last row alone
            3, 5,           // (1+2+4+5)/4 rounded, (3+6)/2 rounded
            9, 11,
        }, reduced);
    }

    [Fact]
    public void LimitedRangeKeepsGreyAndSqueezesTheEnds()
    {
        byte[] frame = [0, 255, 128, 64, /* U */ 0, /* V */ 255];
        I420.ToLimitedRange(frame, 2, 2);
        Assert.Equal(new byte[] { 16, 235, 126, 71, 16, 240 }, frame);

        byte[] grey = [0, 0, 0, 0, 128, 128];
        I420.ToLimitedRange(grey, 2, 2);
        Assert.Equal(128, grey[4]);
    }

    // --- what goes in ---------------------------------------------------------------------------

    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly MonitorRecord Left = new(1, "A", new PixelRect(0, 0, 1920, 1080), true);
    private static readonly MonitorRecord Right = new(2, "B", new PixelRect(1920, 0, 1280, 1024), false);
    private static readonly MonitorRecord RightReplugged = new(3, "B", new PixelRect(1920, 0, 1280, 1024), false);

    private static readonly IReadOnlyList<(DateTimeOffset, DateTimeOffset)> AllDay = [(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)];

    private static DayHistory Day() => new(DateOnly.FromDateTime(Start.Date),
    [
        Capture(1, 0, CaptureKind.Active, [(1, 10), (2, 20)], [1, 2], window: new PixelRect(100, 100, 800, 600)),
        Capture(2, 10, CaptureKind.Idle, [], [1, 2]),
        Capture(3, 20, CaptureKind.Active, [(1, 11)], [1, 2], pendingDelete: true),
        Capture(4, 30, CaptureKind.Active, [(1, 12), (3, 30)], [1, 3]),
        Capture(5, 40, CaptureKind.Active, [], [1, 2]),
    ], [Left, Right, RightReplugged]);

    [Fact]
    public void PlanLeavesOutIdleMarkedAndEmptyCaptures()
    {
        var frames = TimelapsePlan.Frames(Day(), Options());
        Assert.Equal([1L, 4], frames.Select(f => f.CaptureId));
        Assert.Equal([1L, 2], frames[0].Tiles.Select(t => t.Monitor.Id));
        Assert.Equal([1L, 3], frames[1].Tiles.Select(t => t.Monitor.Id));
    }

    [Fact]
    public void IdleCapturesShowTheScreenAsLastCaptured()
    {
        var frames = TimelapsePlan.Frames(Day(), Options() with { IncludeIdle = true });
        Assert.Equal([1L, 2, 4], frames.Select(f => f.CaptureId));
        Assert.Equal([new FrameRef(1, 10), new FrameRef(2, 20)], frames[1].Tiles.Select(t => t.Frame!.Value));
    }

    [Fact]
    public void ChosenMonitorIsFoundAgainByItsPlaceAfterReplugging()
    {
        var frames = TimelapsePlan.Frames(Day(), Options() with { Monitor = Right });
        Assert.Equal([1L, 4], frames.Select(f => f.CaptureId));
        Assert.Equal([2L], frames[0].Tiles.Select(t => t.Monitor.Id));
        Assert.Equal([3L], frames[1].Tiles.Select(t => t.Monitor.Id));
    }

    [Fact]
    public void OnlyCapturesInTheRangesAreKept()
    {
        var frames = TimelapsePlan.Frames(Day(), Options() with { Ranges = [(Start.AddSeconds(25), Start.AddSeconds(35))] });
        Assert.Equal([4L], frames.Select(f => f.CaptureId));
    }

    [Fact]
    public void FocusedFramesShowTheWindowOnly()
    {
        var frames = TimelapsePlan.Frames(Day(), Options() with { FocusOnApplication = true });
        Assert.Equal(new PixelRect(100, 100, 800, 600), frames[0].Focus);
        Assert.Equal(new PixelRect(100, 100, 800, 600), frames[0].Content);
        Assert.Null(frames[1].Focus); // no window recorded
        Assert.Equal(new PixelRect(0, 0, 3200, 1080), frames[1].Content);
    }

    [Fact]
    public void VideoFitsTheLargestPictureInTheBoxWithEvenSides()
    {
        var frames = TimelapsePlan.Frames(Day(), Options());
        // 3200 × 1080 in 1920 × 1080: scaled by 0.6 to 1920 × 648.
        Assert.Equal((1920, 648), TimelapsePlan.VideoSize(frames, (1920, 1080)));
        Assert.Equal((1280, 720), TimelapsePlan.VideoSize([], (1280, 720)));
    }

    [Fact]
    public void FrameAtShowsAnyCaptureWithSomethingRecorded()
    {
        var day = Day();
        // Export Frame shows what Day review shows, marked or idle.
        var marked = TimelapsePlan.FrameAt(day, 2, null, false)!;
        Assert.Equal([new FrameRef(1, 11), null], marked.Tiles.Select(t => t.Frame));
        Assert.Equal([new FrameRef(1, 10), new FrameRef(2, 20)], TimelapsePlan.FrameAt(day, 1, null, false)!.Tiles.Select(t => t.Frame!.Value));
        Assert.Equal([3L], TimelapsePlan.FrameAt(day, 3, Right, false)!.Tiles.Select(t => t.Monitor.Id));
        Assert.Null(TimelapsePlan.FrameAt(day, 4, null, false)); // nothing recorded
        Assert.Null(TimelapsePlan.FrameAt(day, 2, Right, false)); // nothing recorded on the monitor chosen
    }

    private static TimelapseOptions Options() => new(AllDay, null, false, false, 30, (1920, 1080), false);

    private static CaptureRecord Capture(long id, int seconds, CaptureKind kind, (long Monitor, int Index)[] frames, long[] monitors,
        PixelRect? window = null, bool pendingDelete = false) =>
        new(id, Start.AddSeconds(seconds), kind, "app.exe", null, null, window, false, null, pendingDelete,
            frames.Select(f => new MonitorFrame(f.Monitor, new FrameRef(f.Monitor, f.Index))).ToList(), monitors);

    // --- helpers --------------------------------------------------------------------------------

    private static byte[] Frame(int seed)
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var p = (y * Width + x) * 4;
                bgra[p] = (byte)(x + seed * 20);
                bgra[p + 1] = (byte)(y * 2);
                bgra[p + 2] = (byte)((x ^ y) + seed * 7);
                bgra[p + 3] = 255;
            }
        var i420 = new byte[I420.Size(Width, Height)];
        I420.FromBgraRows(bgra, Width * 4, Width, Height, 0, Height, i420);
        I420.ToLimitedRange(i420, Width, Height);
        return i420;
    }

    private readonly record struct Box(string Type, int Start, long Size)
    {
        public int End => (int)(Start + Size);
    }

    private static List<Box> Boxes(byte[] file, int start, int end)
    {
        var boxes = new List<Box>();
        for (var position = start; position + 8 <= end;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            if (size == 1)
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(position + 8));
            boxes.Add(new Box(Encoding.ASCII.GetString(file, position + 4, 4), position, size));
            position += (int)size;
        }
        return boxes;
    }

    /// <summary>Follows a path of nested boxes; full boxes holding boxes (stsd) are entered past their counts.</summary>
    private static Box Find(byte[] file, Box parent, params string[] path) => Find(file, Boxes(file, ChildStart(parent), parent.End), path);

    private static Box Find(byte[] file, List<Box> boxes, params string[] path)
    {
        var box = boxes.Single(b => b.Type == path[0]);
        return path.Length == 1 ? box : Find(file, box, path[1..]);
    }

    private static int ChildStart(Box box) => box.Start + (box.Type == "stsd" ? 16 : 8);

    /// <summary>The 32-bit entries of a full box after its version/flags and <paramref name="headerFields"/> fields (the last the count).</summary>
    private static IEnumerable<long> Table(byte[] file, Box box, int headerFields)
    {
        var start = box.Start + 12 + 4 * headerFields;
        var count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(start - 4));
        for (var i = 0; i < count; i++)
            yield return BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(start + 4 * i));
    }

    private static IEnumerable<long> Table64(byte[] file, Box box)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(box.Start + 12));
        for (var i = 0; i < count; i++)
            yield return (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(box.Start + 16 + 8 * i));
    }
}
