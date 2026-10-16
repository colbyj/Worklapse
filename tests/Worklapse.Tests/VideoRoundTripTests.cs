using Worklapse.Core.Platform;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

public class VideoRoundTripTests
{
    private const int Width = 320, Height = 180;

    [Fact]
    public void EncodedFramesDecodeCloseToTheOriginal()
    {
        using var encoder = new Av1Encoder(Width, Height, quality: 20);
        using var decoder = new Av1Decoder();
        var output = new byte[Width * Height * 4];

        for (var i = 0; i < 5; i++)
        {
            var frame = TestImage(i);
            var encoded = encoder.Encode(frame, Width * 4, i * 10_000);
            Assert.Equal(i == 0, encoded.IsKey);

            decoder.Decode(encoded.Data.Span);
            Assert.Equal((Width, Height), (decoder.Width, decoder.Height));
            decoder.CopyBgra(output, Width * 4);
            Assert.True(Psnr(frame, output) > 35, $"frame {i}: PSNR {Psnr(frame, output):F1} dB");
        }
    }

    [Fact]
    public void StagedFramesRebuildExactly()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "a.staging");
        var records = new List<(long Offset, int Length)>();
        using (var writer = new StagingWriter(path, Width, Height))
        {
            for (var i = 0; i < 4; i++)
                records.Add(writer.WriteFrame(TestImage(i), 1000L * i));
        }

        using var reader = new StagingReader(path);
        for (var i = 0; i < records.Count; i++)
        {
            reader.Apply(records[i].Offset, records[i].Length);
            Assert.Equal(TestImage(i), reader.Frame.ToArray());
            Assert.Equal(1000L * i, reader.FramePtsMs);
        }
    }

    [Fact]
    public void StagedSegmentsAreEncodedAndReadableByIndex()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var monitor = new MonitorInfo("TEST", new PixelRect(0, 0, Width, Height), true);
        var monitorId = store.GetOrAddMonitor(monitor);
        var start = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(-6));

        var refs = new List<FrameRef>();
        using (var sink = new StagedVideoSink(store, () => 30))
        {
            for (var i = 0; i < StagedVideoSink.FramesPerSegment + 3; i++)
            {
                var pixels = TestImage(i);
                var capture = new ScreenCapture { Monitor = monitor, Width = Width, Height = Height, Stride = Width * 4, Pixels = pixels, Changed = true };
                using var transaction = store.BeginTransaction();
                refs.Add(sink.Append(monitorId, capture, start.AddSeconds(10 * i)));
                transaction.Commit();
                sink.Committed();
            }

            // The first segment filled up and is being encoded; the second is still staged.
            Assert.True(sink.Encoder.WaitUntilIdle(TimeSpan.FromMinutes(1)));
            Assert.Equal([(int)SegmentState.Encoded, (int)SegmentState.Staged], SegmentStates(db));

            sink.CloseAll();
            Assert.True(sink.Encoder.WaitUntilIdle(TimeSpan.FromMinutes(1)));
        }

        Assert.Equal([(int)SegmentState.Encoded, (int)SegmentState.Encoded], SegmentStates(db));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.staging", SearchOption.AllDirectories));

        // A full segment rolls over into a second one that starts with a key frame.
        Assert.Equal(2, refs.Select(r => r.SegmentId).Distinct().Count());
        Assert.Equal(0, refs[StagedVideoSink.FramesPerSegment].FrameIndex);

        // Decode the last frame of the second segment from its key frame, using only the database index.
        var last = refs[^1];
        using var path = db.Command("SELECT path FROM segments WHERE id = $id;");
        path.Parameters.AddWithValue("$id", last.SegmentId);
        using var stream = File.OpenRead(Path.Combine(folder.Path, (string)path.ExecuteScalar()!));
        using var frames = db.Command("SELECT file_offset, length, is_key FROM segment_frames WHERE segment_id = $id ORDER BY frame_index;");
        frames.Parameters.AddWithValue("$id", last.SegmentId);
        using var decoder = new Av1Decoder();
        using (var reader = frames.ExecuteReader())
        {
            var first = true;
            while (reader.Read())
            {
                Assert.Equal(first, reader.GetInt64(2) == 1);
                first = false;
                decoder.Decode(IvfReader.ReadFrame(stream, reader.GetInt64(0), reader.GetInt32(1)));
            }
        }

        var output = new byte[Width * Height * 4];
        decoder.CopyBgra(output, Width * 4);
        Assert.True(Psnr(TestImage(refs.Count - 1), output) > 30);

        // The file is a valid IVF stream on its own, too.
        Assert.Equal(3, IvfReader.EnumerateFrames(stream).Count());
    }

    [Fact]
    public void SegmentsLeftStagedAreEncodedWhenRecordingStartsAgain()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var monitor = new MonitorInfo("TEST", new PixelRect(0, 0, Width, Height), true);
        var monitorId = store.GetOrAddMonitor(monitor);
        var start = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        // Like quitting (or crashing) mid-segment: the sink is disposed with a segment still open.
        using (var sink = new StagedVideoSink(store, () => 30))
        {
            for (var i = 0; i < 5; i++)
            {
                var capture = new ScreenCapture { Monitor = monitor, Width = Width, Height = Height, Stride = Width * 4, Pixels = TestImage(i), Changed = true };
                sink.Append(monitorId, capture, start.AddSeconds(10 * i));
            }
        }
        Assert.Equal([(int)SegmentState.Staged], SegmentStates(db));

        using (var sink = new StagedVideoSink(store, () => 30))
            Assert.True(sink.Encoder.WaitUntilIdle(TimeSpan.FromMinutes(1)));

        Assert.Equal([(int)SegmentState.Encoded], SegmentStates(db));
    }

    private static List<int> SegmentStates(WorklapseDatabase db)
    {
        using var command = db.Command("SELECT state FROM segments ORDER BY id;");
        using var reader = command.ExecuteReader();
        var states = new List<int>();
        while (reader.Read())
            states.Add(reader.GetInt32(0));
        return states;
    }

    /// <summary>A gradient with a moving bar and some fine detail, different for each index.</summary>
    private static byte[] TestImage(int index)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = (y * Width + x) * 4;
            var bar = Math.Abs(x - index * 20 % Width) < 8;
            pixels[p] = (byte)(bar ? 30 : x * 255 / Width);
            pixels[p + 1] = (byte)(bar ? 30 : y * 255 / Height);
            pixels[p + 2] = (byte)((x / 8 + y / 8) % 2 == 0 ? 220 : 160);
            pixels[p + 3] = 255;
        }
        return pixels;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
        for (var c = 0; c < 3; c++)
        {
            var d = a[i + c] - b[i + c];
            sum += d * d;
            count++;
        }
        var mse = sum / count;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
