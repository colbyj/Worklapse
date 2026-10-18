using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Recording captures as image files instead of video: what's written, shown, deleted and cleaned up.</summary>
public class ImageRecordingTests
{
    private const int Width = 160, Height = 96;
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, Width, Height), true) { StableId = "main" };
    private static readonly MonitorInfo Side = new(@"\\.\DISPLAY2", new PixelRect(Width, 0, Width, Height), false) { StableId = "side" };
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly string Day = "images/2026-10-15";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    /// <summary>Images are lossy; at the tests' quality (80) they must still be close to what was captured.</summary>
    private const double MinimumPsnr = 30;

    [Theory]
    [InlineData(FrameFormat.Jpeg, ".jpg")]
    [InlineData(FrameFormat.WebP, ".webp")]
    public async Task EachChangedCaptureIsAnImageFileAndAnUnchangedOneShowsItAgain(FrameFormat format, string extension)
    {
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path, format))
        {
            run.Tick(0, Picture(1));
            run.Tick(10, Picture(1));
            run.Tick(20, Picture(2));
            Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout));
        }

        var frames = Frames(folder);
        Assert.Equal(frames[0], frames[1]);
        Assert.NotEqual(frames[0], frames[2]);
        var monitor = MonitorId(folder);
        Assert.Equal([$"{Day}/090000-m{monitor}{extension}", $"{Day}/090020-m{monitor}{extension}"], FilesOnDisk(folder));
        // The first is finished; the last stays open, since the recorder could still have shown it again.
        Assert.Equal([(format, SegmentState.Encoded), (format, SegmentState.Staged)], Segments(folder));
        AssertShows(folder, frames, [Picture(1), Picture(1), Picture(2)]);

        // The next start finishes it.
        using (var encoder = new BatchEncoder(folder.Path))
            Assert.True(encoder.WaitUntilIdle(Timeout));
        Assert.Equal([(format, SegmentState.Encoded), (format, SegmentState.Encoded)], Segments(folder));
        AssertTidy(folder);
    }

    [Fact]
    public async Task AChangeOfFormatTakesEffectAtEachMonitorsNextChange()
    {
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path, FrameFormat.Video, Main, Side))
        {
            run.Tick(0, Picture(1), Picture(2));
            run.UseFormat(FrameFormat.WebP);
            run.Tick(10, Picture(3), Picture(2)); // the side monitor still shows its video frame
            run.Tick(20, Picture(3), Picture(4));
            run.UseFormat(FrameFormat.Jpeg);
            run.Tick(30, Picture(5), Picture(4));
            Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout));
        }

        Assert.Equal(
            [
                (FrameFormat.Video, SegmentState.Encoded), // main, then side, at 09:00:00
                (FrameFormat.Video, SegmentState.Encoded),
                (FrameFormat.WebP, SegmentState.Encoded),  // main at 09:00:10
                (FrameFormat.WebP, SegmentState.Staged),   // side at 09:00:20, still shown
                (FrameFormat.Jpeg, SegmentState.Staged),   // main at 09:00:30
            ],
            Segments(folder));

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var day = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime));
        var main = day.Monitors.Single(m => m.DeviceName == Main.DeviceName).Id;
        using var provider = new FrameProvider(folder.Path);
        byte[][] expectedMain = [Picture(1), Picture(3), Picture(3), Picture(5)], expectedSide = [Picture(2), Picture(2), Picture(4), Picture(4)];
        for (var i = 0; i < day.Captures.Count; i++)
        {
            foreach (var (monitorId, frame) in day.Captures[i].Frames)
                AssertShows(provider.Get(frame), monitorId == main ? expectedMain[i] : expectedSide[i]);
        }
    }

    [Fact]
    public async Task DeletedCapturesTakeTheirImagesWithThemUnlessAKeptCaptureStillShowsThem()
    {
        using var folder = new TempFolder();
        await using var run = RecorderRun.Start(folder.Path, FrameFormat.WebP);
        for (var i = 0; i < 5; i++)
            run.Tick(10 * i, i == 2 ? Secret() : Picture(i));
        run.Tick(50, Picture(4)); // unchanged: shows the image of 09:00:40 again
        run.Tick(60, Picture(6));
        Assert.Contains(ImagesOnDisk(folder), image => Psnr(image, Secret()) > MinimumPsnr);
        Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout)); // the images the recorder moved on from are finished

        var result = Delete(folder, from: 10, to: 50);
        Assert.False(result.StagedSegmentsAffected);
        run.Sink!.Encoder.RequestReclaim();
        Assert.True(run.Sink.Encoder.WaitUntilIdle(Timeout));

        var monitor = MonitorId(folder);
        Assert.Equal([$"{Day}/090000-m{monitor}.webp", $"{Day}/090040-m{monitor}.webp", $"{Day}/090100-m{monitor}.webp"], FilesOnDisk(folder));
        Assert.DoesNotContain(ImagesOnDisk(folder), image => Psnr(image, Secret()) > MinimumPsnr);
        AssertShows(folder, Frames(folder), [Picture(0), Picture(4), Picture(6)]);
        AssertTidy(folder);
    }

    [Fact]
    public async Task TheLatestImageGoesOnceTheRecorderHasMovedOnFromIt()
    {
        using var folder = new TempFolder();
        await using var run = RecorderRun.Start(folder.Path, FrameFormat.WebP);
        run.Tick(0, Picture(0));
        run.Tick(10, Secret());

        // The recorder could still show the secret again for an unchanged screen, so it stays for now.
        var result = Delete(folder, from: 10, to: 20);
        Assert.True(result.StagedSegmentsAffected);
        run.Sink!.Encoder.RequestReclaim();
        Assert.True(run.Sink.Encoder.WaitUntilIdle(Timeout));
        Assert.Equal(2, FilesOnDisk(folder).Count);

        // As the app does after such a delete: the recorder lets go of it at its next tick, and it goes.
        run.Recorder.CloseSegmentsSoon();
        run.Tick(20, Picture(2));
        Assert.True(run.Sink.Encoder.WaitUntilIdle(Timeout));

        var monitor = MonitorId(folder);
        Assert.Equal([$"{Day}/090000-m{monitor}.webp", $"{Day}/090020-m{monitor}.webp"], FilesOnDisk(folder));
        Assert.DoesNotContain(ImagesOnDisk(folder), image => Psnr(image, Secret()) > MinimumPsnr);
        AssertTidy(folder);
    }

    [Fact]
    public async Task AnImageDeletedWhileTheScreenStillShowsItIsRecordedAgainAsANewOne()
    {
        // The delete can't take back what is still on screen: the next capture shows it, in a file of its own.
        using var folder = new TempFolder();
        await using var run = RecorderRun.Start(folder.Path, FrameFormat.WebP);
        run.Tick(0, Picture(0));
        run.Tick(10, Picture(1));
        Delete(folder, from: 10, to: 20);
        run.Recorder.CloseSegmentsSoon();
        run.Tick(20, Picture(1));
        Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout));

        var monitor = MonitorId(folder);
        Assert.Equal([$"{Day}/090000-m{monitor}.webp", $"{Day}/090020-m{monitor}.webp"], FilesOnDisk(folder));
        AssertShows(folder, Frames(folder), [Picture(0), Picture(1)]);
        AssertTidy(folder);
    }

    [Fact]
    public void AnImageOfACaptureThatWasNeverCommittedIsDeleted()
    {
        using var folder = new TempFolder();
        long monitorId;
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            monitorId = store.GetOrAddMonitor(Main);
            var sink = new FrameSink(store, () => (FrameFormat.WebP, 80));
            TestRecording.Append(store, sink, Main, monitorId, Picture(0), Nine);

            // The next tick fails after the image was written (another monitor couldn't be captured): its
            // transaction rolls back, and the recorder disposes the sink.
            using (store.BeginTransaction())
            {
                var captureId = store.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, null, null, null);
                var capture = new ScreenCapture { Monitor = Main, Width = Width, Height = Height, Frame = Picture(1), Hash = 1 };
                store.InsertFrame(captureId, monitorId, sink.Append(monitorId, capture, Nine.AddSeconds(10)));
                Assert.Single(Directory.GetFiles(Path.Combine(folder.Path, Day), "*.tmp"));
            }
            sink.Dispose();
        }

        Assert.Equal([$"{Day}/090000-m{monitorId}.webp"], FilesOnDisk(folder));
        Assert.Equal(1, Count(folder, "SELECT count(*) FROM segments"));
    }

    [Fact]
    public async Task ImagesACrashLeftHalfDoneAreFinishedOrDeletedAtTheNextStart()
    {
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path, FrameFormat.WebP))
        {
            run.Tick(0, Picture(0));
            run.Tick(10, Picture(1));
        }
        var monitor = MonitorId(folder);
        var committed = Path.Combine(folder.Path, Day, $"090010-m{monitor}.webp");
        var uncommitted = Path.Combine(folder.Path, Day, $"090020-m{monitor}.webp.tmp");

        // A crash after the capture of 09:00:10 was committed, before its image took its name; and one while the
        // capture of 09:00:20 was being recorded, after its image was written.
        File.Move(committed, committed + ".tmp");
        File.Copy(committed + ".tmp", uncommitted);

        await using (var run = RecorderRun.Start(folder.Path, FrameFormat.WebP))
            run.Tick(30, Picture(3));

        Assert.Equal([$"{Day}/090000-m{monitor}.webp", $"{Day}/090010-m{monitor}.webp", $"{Day}/090030-m{monitor}.webp"], FilesOnDisk(folder));
        AssertShows(folder, Frames(folder), [Picture(0), Picture(1), Picture(3)]);
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>A gradient with a moving bar, different for each index.</summary>
    private static byte[] Picture(int index)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = (y * Width + x) * 4;
            var bar = Math.Abs(x - index * 23 % Width) < 8;
            pixels[p] = (byte)(bar ? 30 : x * 255 / Width);
            pixels[p + 1] = (byte)(bar ? 30 : y * 255 / Height);
            pixels[p + 2] = (byte)((x / 8 + y / 8) % 2 == 0 ? 220 : 160);
            pixels[p + 3] = 255;
        }
        var frame = new byte[I420.Size(Width, Height)];
        I420.FromBgraRows(pixels, Width * 4, Width, Height, 0, Height, frame);
        return frame;
    }

    /// <summary>A picture unlike any <see cref="Picture"/>: what must not stay on disk once deleted.</summary>
    private static byte[] Secret()
    {
        var frame = Picture(0);
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(255 - frame[i]);
        return frame;
    }

    private static DeleteResult Delete(TempFolder folder, int from, int to)
    {
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        history.SetPendingDelete(Nine.AddSeconds(from), Nine.AddSeconds(to), pending: true);
        return history.DeleteMarked(DateOnly.FromDateTime(Nine.LocalDateTime));
    }

    /// <summary>The frame each capture of the day shows, in order (one monitor).</summary>
    private static List<FrameRef> Frames(TempFolder folder)
    {
        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        return history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime)).Captures.Select(c => c.Frames.Single().Frame).ToList();
    }

    private static long MonitorId(TempFolder folder) => Count(folder, "SELECT min(id) FROM monitors");

    private static List<(FrameFormat, SegmentState)> Segments(TempFolder folder)
    {
        using var db = WorklapseDatabase.Open(folder.Path);
        using var command = db.Command("SELECT format, state FROM segments ORDER BY id;");
        using var reader = command.ExecuteReader();
        var segments = new List<(FrameFormat, SegmentState)>();
        while (reader.Read())
            segments.Add(((FrameFormat)reader.GetInt32(0), (SegmentState)reader.GetInt32(1)));
        return segments;
    }

    private static long Count(TempFolder folder, string sql)
    {
        using var db = WorklapseDatabase.Open(folder.Path);
        using var command = db.Command(sql);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Every file in the images folder, relative to the data folder.</summary>
    private static List<string> FilesOnDisk(TempFolder folder)
    {
        var images = Path.Combine(folder.Path, FrameSink.ImagesFolder);
        return Directory.Exists(images)
            ? Directory.GetFiles(images, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder.Path, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    /// <summary>Every image in the images folder, decoded: what anyone with the folder could see.</summary>
    private static List<byte[]> ImagesOnDisk(TempFolder folder) => FilesOnDisk(folder).Select(relative =>
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder.Path, relative));
        var (_, width, height) = ImageCodec.Identify(bytes);
        var frame = new byte[I420.Size(width, height)];
        ImageCodec.DecodeI420(bytes, width, height, frame);
        return frame;
    }).ToList();

    private static void AssertShows(TempFolder folder, List<FrameRef> frames, byte[][] pictures)
    {
        Assert.Equal(pictures.Length, frames.Count);
        using var provider = new FrameProvider(folder.Path);
        for (var i = 0; i < frames.Count; i++)
            AssertShows(provider.Get(frames[i]), pictures[i]);
    }

    private static void AssertShows(DecodedFrame frame, byte[] picture)
    {
        try
        {
            var psnr = Psnr(frame.I420, picture);
            Assert.True(psnr > MinimumPsnr, $"PSNR {psnr:0.0} dB");
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>Every image file belongs to a segment, and nothing is left to delete or to look at again.</summary>
    private static void AssertTidy(TempFolder folder)
    {
        Assert.Equal(0, Count(folder, "SELECT count(*) FROM dead_segment_files"));
        Assert.Equal(0, Count(folder, "SELECT count(*) FROM reclaim_queue"));
        using var db = WorklapseDatabase.Open(folder.Path);
        using var command = db.Command("SELECT path FROM segments WHERE format <> 0;");
        using var reader = command.ExecuteReader();
        var expected = new List<string>();
        while (reader.Read())
            expected.Add(reader.GetString(0));
        Assert.Equal(expected.Order(StringComparer.Ordinal), FilesOnDisk(folder));
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        var mse = sum / a.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>The recorder on a test folder with a fake desktop and clock, ticked by hand.</summary>
    private sealed class RecorderRun : IAsyncDisposable
    {
        private readonly Clock _clock = new();
        private readonly FakeScreen _screen = new();

        public Recorder Recorder { get; private set; } = null!;
        public SettingsStore Settings { get; private set; } = null!;

        /// <summary>The recorder's current sink (it makes a new one after an error).</summary>
        public FrameSink? Sink { get; private set; }

        public static RecorderRun Start(string dataFolder, FrameFormat format, params MonitorInfo[] monitors)
        {
            var run = new RecorderRun();
            run._screen.Monitors = monitors.Length > 0 ? monitors : [Main];
            run.Settings = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
            run.Settings.Save(new WorklapseSettings
            {
                DataFolder = dataFolder, CaptureMode = CaptureMode.AllMonitors, IdleMinutes = 120,
                Format = format, Quality = 30, JpegQuality = 80, WebPQuality = 80,
            });
            var platform = new PlatformServices(run._screen, new FakeForeground(), new FakeIdle(), new FakeSession());
            run.Recorder = new Recorder(run.Settings, platform, store => run.Sink = new FrameSink(store, () => run.Settings.Current.FrameStorage()), run._clock);
            return run;
        }

        public void UseFormat(FrameFormat format) => Settings.Save(Settings.Current with { Format = format });

        /// <summary>A tick at 09:00 + <paramref name="seconds"/>, each monitor showing its picture; the same picture again is unchanged.</summary>
        public void Tick(int seconds, params byte[][] pictures)
        {
            _clock.Now = Nine.AddSeconds(seconds);
            _screen.Pictures = pictures;
            Recorder.Tick();
            Assert.NotEqual(RecorderState.Error, Recorder.Status.State);
        }

        public ValueTask DisposeAsync() => Recorder.DisposeAsync();

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = Nine;
            public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
        }

        private sealed class FakeScreen : IScreenSource
        {
            public MonitorInfo[] Monitors { get; set; } = [];
            public byte[][] Pictures { get; set; } = [];
            public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;

            // A copy: the recorder may change the buffer in place.
            public ScreenCapture? Capture(MonitorInfo monitor)
            {
                var picture = Pictures[Array.IndexOf(Monitors, monitor)];
                return new ScreenCapture { Monitor = monitor, Width = Width, Height = Height, Frame = picture.ToArray(), Hash = Hash(picture) };
            }

            public void Dispose() { }

            private static ulong Hash(byte[] picture)
            {
                var hash = 14695981039346656037UL;
                foreach (var b in picture)
                    hash = (hash ^ b) * 1099511628211UL;
                return hash;
            }
        }

        private sealed class FakeForeground : IForegroundWindowProbe
        {
            public ForegroundWindow? GetForegroundWindow() => new("work.exe", null, "Work", new PixelRect(10, 10, 50, 30));
        }

        private sealed class FakeIdle : IIdleDetector
        {
            public TimeSpan GetIdleTime() => TimeSpan.Zero;
        }

        private sealed class FakeSession : ISessionStateMonitor
        {
            public bool IsLocked => false;
            public void Dispose() { }
        }
    }
}
