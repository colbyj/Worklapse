using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

public class FrameProviderTests
{
    private static readonly (int Width, int Height) Large = (320, 180), Small = (160, 96);

    [Theory]
    [InlineData(FrameProvider.DefaultCacheBudgetBytes)]
    [InlineData(1L)] // nothing stays cached: every request decodes
    public void FramesMatchSequentialDecodingInAnyOrder(long cacheBudget)
    {
        using var recording = Recording.Create();
        using var provider = new FrameProvider(recording.Folder.Path, cacheBudget);

        var refs = recording.Expected.Keys.ToList();
        var random = new Random(7);
        var order = refs.Concat(Enumerable.Reverse(refs)).Concat(refs.OrderBy(_ => random.Next())).ToList();
        foreach (var frameRef in order)
        {
            var frame = provider.Get(frameRef);
            Assert.Equal(recording.Expected[frameRef], frame.I420.ToArray());
            frame.Release();
            Assert.True(provider.OpenDecoders <= 2, "at most one decoder per monitor");
        }
    }

    [Fact]
    public void HeldFramesStayIntactAfterTheCacheDropsThem()
    {
        using var recording = Recording.Create();
        using var provider = new FrameProvider(recording.Folder.Path, cacheBudgetBytes: 1);

        var held = recording.Expected.Keys.First();
        var frame = provider.Get(held);
        foreach (var other in recording.Expected.Keys.Skip(1))
            provider.Get(other).Release();

        Assert.Equal(recording.Expected[held], frame.I420.ToArray());
        frame.Release();
        Assert.Throws<InvalidOperationException>(frame.Release);
    }

    [Fact]
    public void IdleDecodersAreClosedAndReopenedOnDemand()
    {
        using var recording = Recording.Create();
        using var provider = new FrameProvider(recording.Folder.Path, cacheBudgetBytes: 1);
        var refs = recording.Expected.Keys.ToList();

        foreach (var frameRef in refs)
            provider.Get(frameRef).Release();
        Assert.Equal(2, provider.OpenDecoders);

        provider.CloseIdle(TimeSpan.FromMinutes(1));
        Assert.Equal(2, provider.OpenDecoders);
        provider.CloseIdle(TimeSpan.Zero);
        Assert.Equal(0, provider.OpenDecoders);

        var last = provider.Get(refs[^1]);
        Assert.Equal(recording.Expected[refs[^1]], last.I420.ToArray());
        last.Release();
    }

    /// <summary>
    /// Two monitors of different sizes: an encoded segment for each, then a second, still staged segment
    /// for the large one. Expected frames are the AV1 frames decoded in order, or the staged captures.
    /// </summary>
    private sealed class Recording : IDisposable
    {
        private Recording(TempFolder folder, Dictionary<FrameRef, byte[]> expected)
        {
            Folder = folder;
            Expected = expected;
        }

        public TempFolder Folder { get; }
        public Dictionary<FrameRef, byte[]> Expected { get; }

        public static Recording Create()
        {
            var folder = new TempFolder();
            var expected = new Dictionary<FrameRef, byte[]>();
            using var db = WorklapseDatabase.Open(folder.Path);
            var store = new RecordingStore(db);
            var large = new MonitorInfo("LARGE", new PixelRect(0, 0, Large.Width, Large.Height), true);
            var small = new MonitorInfo("SMALL", new PixelRect(Large.Width, 0, Small.Width, Small.Height), false);
            var largeId = store.GetOrAddMonitor(large);
            var smallId = store.GetOrAddMonitor(small);
            var start = new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

            var encoded = new List<FrameRef>();
            using (var sink = new StagedVideoSink(store, () => 30))
            {
                FrameRef Append(MonitorInfo monitor, long monitorId, int index)
                {
                    var (width, height) = (monitor.Bounds.Width, monitor.Bounds.Height);
                    var frame = ToI420(TestImage(width, height, index), width, height);
                    using var transaction = store.BeginTransaction();
                    var frameRef = sink.Append(monitorId, new ScreenCapture { Monitor = monitor, Width = width, Height = height, Frame = frame, Changed = true }, start.AddSeconds(10 * index));
                    transaction.Commit();
                    sink.Committed();
                    expected[frameRef] = frame;
                    return frameRef;
                }

                for (var i = 0; i < 6; i++)
                {
                    encoded.Add(Append(large, largeId, i));
                    encoded.Add(Append(small, smallId, i));
                }
                sink.CloseAll();
                Assert.True(sink.Encoder.WaitUntilIdle(TimeSpan.FromMinutes(1)));

                // Left open: stays staged when the sink is disposed.
                for (var i = 6; i < 10; i++)
                    Append(large, largeId, i);
            }

            foreach (var segment in encoded.GroupBy(r => r.SegmentId))
            {
                var decoded = DecodeInOrder(db, segment.Key);
                foreach (var frameRef in segment)
                    expected[frameRef] = decoded[frameRef.FrameIndex];
            }
            return new Recording(folder, expected);
        }

        public void Dispose() => Folder.Dispose();

        private static List<byte[]> DecodeInOrder(WorklapseDatabase db, long segmentId)
        {
            using var path = db.Command("SELECT path FROM segments WHERE id = $id;");
            path.Parameters.AddWithValue("$id", segmentId);
            using var stream = File.OpenRead(Path.Combine(db.DataFolder, (string)path.ExecuteScalar()!));
            using var decoder = new Av1Decoder(threads: 1);
            var frames = new List<byte[]>();
            foreach (var (offset, length, _) in IvfReader.EnumerateFrames(stream).ToList())
            {
                decoder.Decode(IvfReader.ReadFrame(stream, offset, length));
                var frame = new byte[I420.Size(decoder.Width, decoder.Height)];
                decoder.CopyI420(frame);
                frames.Add(frame);
            }
            return frames;
        }
    }

    private static byte[] ToI420(byte[] bgra, int width, int height)
    {
        var frame = new byte[I420.Size(width, height)];
        I420.FromBgraRows(bgra, width * 4, width, height, 0, height, frame);
        return frame;
    }

    /// <summary>A gradient with a moving bar, different for each index.</summary>
    private static byte[] TestImage(int width, int height, int index)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = (y * width + x) * 4;
            var bar = Math.Abs(x - index * 20 % width) < 8;
            pixels[p] = (byte)(bar ? 30 : x * 255 / width);
            pixels[p + 1] = (byte)(bar ? 30 : y * 255 / height);
            pixels[p + 2] = (byte)((x / 8 + y / 8) % 2 == 0 ? 220 : 160);
            pixels[p + 3] = 255;
        }
        return pixels;
    }
}
