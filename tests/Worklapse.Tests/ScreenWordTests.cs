using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Projects;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Reading the active window's text while recording, and keeping the words only where reading is allowed.</summary>
public class ScreenWordTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 128, 64), true);

    [Fact]
    public async Task TheRecorderReadsTheActiveWindowAndCopiesTheWordsWhileItDoesntChange()
    {
        using var folder = new TempFolder();
        using (var history = new HistoryStore(WorklapseDatabase.Open(folder.Path)))
        {
            new RecordingStore(history.Database).GetOrAddApp("keepass.exe", null); // recorded before, with reading turned off since
            history.SetReadScreen(new ProgramKey(ProgramKind.App, "keepass.exe"), false);
        }
        var reader = new FakeReader();
        await using (var run = RecorderRun.Start(folder.Path, reader))
        {
            run.Tick(0, "rider64.exe", picture: 1);
            run.Tick(10, "rider64.exe", picture: 1);   // unchanged: the words are copied, not read again
            run.Tick(20, "rider64.exe", picture: 2);
            run.Tick(30, "keepass.exe", picture: 3);   // reading is off for it
            run.Settings.Save(run.Settings.Current with { ReadScreenText = false });
            run.Tick(40, "rider64.exe", picture: 4);   // reading is off altogether
        }

        using var db = WorklapseDatabase.Open(folder.Path);
        var words = new ScreenWordStore(db).Get(Nine, Nine.AddMinutes(1));
        var byTime = Captures(db).ToDictionary(c => c.Seconds, c => words.GetValueOrDefault(c.Id));
        Assert.Equal(["shade1", "docket"], Present(byTime[0]));
        Assert.Equal(["shade1", "docket"], Present(byTime[10]));
        Assert.Equal(["shade2", "docket"], Present(byTime[20]));
        Assert.Null(byTime[30]);
        Assert.Null(byTime[40]);
        Assert.Equal(2, reader.Reads); // keepass.exe's window wasn't even read
    }

    [Fact]
    public void CropLumaCopiesTheWindowsGrayPixelsClippedToTheFrame()
    {
        const int width = 40, height = 20;
        var frame = new byte[I420.Size(width, height)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                frame[y * width + x] = (byte)(y * width + x);

        var (gray, w, h) = ScreenReadingQueue.CropLuma(frame, width, height, new PixelRect(20, 2, 30, 17))!.Value;

        Assert.Equal((20, 17), (w, h));
        Assert.Equal((byte)(2 * width + 20), gray[0]);
        Assert.Equal(unchecked((byte)(18 * width + 39)), gray[^1]); // the shades wrap at 256
        Assert.Null(ScreenReadingQueue.CropLuma(frame, width, height, new PixelRect(30, 0, 30, 20))); // 10 wide: too small
    }

    [Fact]
    public void WordsGoWhenReadingIsTurnedOffForTheirAppSiteOrProject()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var store = new RecordingStore(db);
        var projects = new ProjectStore(db);
        var client = projects.AddProject("Client", null);
        var other = projects.AddProject("Other", null);
        var rider = store.GetOrAddApp("rider64.exe", null);
        var slack = store.GetOrAddApp("slack.exe", null);
        var firefox = store.GetOrAddApp("firefox.exe", null);
        long Add(int seconds, long app, string title, string? site = null) =>
            store.InsertCapture(Nine.AddSeconds(seconds), CaptureKind.Active, app, title, null, site is null ? null : store.GetOrAddSite(site));
        var byHand = Add(0, rider, "Notes.cs");           // set to Client by hand
        var linked = Add(10, slack, "Total Label");      // Slack is linked to Client
        var titled = Add(20, rider, "Client – Job.cs");  // a title rule gives it Client
        var overridden = Add(30, slack, "Total Label");  // linked to Client, but set to Other by hand
        var site = Add(40, firefox, "Bank", "bank.example");
        var plain = Add(50, rider, "Other.cs");
        var words = new ScreenWordStore(db);
        foreach (var id in new[] { byHand, linked, titled, overridden, site, plain })
            Assert.True(words.Set(id, ["word"]));

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        projects.SetSpan(Nine, Nine.AddSeconds(5), client);
        projects.SetSpan(Nine.AddSeconds(30), Nine.AddSeconds(35), other);
        history.SetProject(new ProgramKey(ProgramKind.App, "slack.exe"), client);
        projects.SetTitleRules(client, [(null, "Client", null)]);
        Assert.Equal(0, words.Prune()); // reading is still on everywhere

        // Turning reading off deletes at once: by hand, by link and by title rule for the project, and the site's.
        projects.SetSuggestionOptOuts(client, suggest: true, readScreen: false);
        history.SetReadScreen(new ProgramKey(ProgramKind.Site, "bank.example"), false);
        Assert.Equal([overridden, plain], words.Get(Nine, Nine.AddMinutes(1)).Keys.Order());
        Assert.Equal(0, words.Prune());
        Assert.False(words.Set(byHand, ["word"])); // and they aren't kept again
        Assert.False(words.MayRead(titled));
        Assert.True(words.MayRead(plain));

        // Time given to the project later loses its words too.
        projects.SetSpan(Nine.AddSeconds(50), Nine.AddSeconds(55), client);
        Assert.Equal([overridden], words.Get(Nine, Nine.AddMinutes(1)).Keys);
    }

    [Fact]
    public void WordsGoWithTheirCapturesAndOptedOutAppsKeepTheirRows()
    {
        using var folder = new TempFolder();
        var day = DateOnly.FromDateTime(Nine.LocalDateTime);
        long capture;
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            capture = store.InsertCapture(Nine, CaptureKind.Active, store.GetOrAddApp("keepass.exe", null), "Vault", null);
            Assert.True(new ScreenWordStore(db).Set(capture, ["secret"]));
        }

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        history.SetLearn(new ProgramKey(ProgramKind.App, "keepass.exe"), false);
        history.SetPendingDelete(Nine, Nine.AddSeconds(10), pending: true);
        Assert.Equal(1, history.DeleteMarked(day).Captures);

        Assert.Null(new ScreenWordStore(history.Database).Get(capture));
        Assert.Contains("keepass.exe", SuggestionOptOuts.Load(history.Database).UnlearnedApps);
        // A new copy of it (another path) is opted out alike.
        var copy = new RecordingStore(history.Database).GetOrAddApp("keepass.exe", @"C:\Tools\keepass.exe");
        using var learn = history.Database.Command($"SELECT learn FROM apps WHERE id = {copy};");
        Assert.Equal(0L, learn.ExecuteScalar());
    }

    [Fact]
    public void PastCapturesAreReadFromTheirStoredFramesOncePerPicture()
    {
        using var folder = new TempFolder();
        var ids = new List<long>();
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            using var sink = new FrameSink(store, () => (FrameFormat.Video, 30));
            var monitorId = store.GetOrAddMonitor(Main);
            var app = store.GetOrAddApp("rider64.exe", null);
            FrameRef frame = default;
            foreach (var (seconds, shade) in new (int, byte?)[] { (0, 100), (10, 101), (20, null) }) // the last shows the frame before
            {
                using var transaction = store.BeginTransaction();
                var id = store.InsertCapture(Nine.AddSeconds(seconds), CaptureKind.Active, app, "Code", new PixelRect(10, 10, 50, 30));
                if (shade is { } s)
                {
                    var pixels = new byte[I420.Size(Main.Bounds.Width, Main.Bounds.Height)];
                    Array.Fill(pixels, s);
                    frame = sink.Append(monitorId, new ScreenCapture { Monitor = Main, Width = 128, Height = 64, Frame = pixels, Hash = s }, Nine.AddSeconds(seconds));
                }
                store.InsertFrame(id, monitorId, frame);
                transaction.Commit();
                sink.Committed();
                ids.Add(id);
            }
        }

        var reader = new FakeReader();
        Assert.Equal(3, PastScreenReading.Run(folder.Path, reader, Nine, Nine.AddHours(1), null, TestContext.Current.CancellationToken));

        using var check = WorklapseDatabase.Open(folder.Path);
        var words = new ScreenWordStore(check);
        Assert.Equal(["shade100", "docket"], Present(words.Get(ids[0])));
        Assert.Equal(["shade101", "docket"], Present(words.Get(ids[1])));
        Assert.Equal(["shade101", "docket"], Present(words.Get(ids[2])));
        Assert.Equal(2, reader.Reads);
        Assert.Equal(0, PastScreenReading.Run(folder.Path, reader, Nine, Nine.AddHours(1), null, TestContext.Current.CancellationToken)); // nothing left
    }

    [Fact]
    public void APictureThatCantBeReadIsSkippedAndTheRestAreRead()
    {
        using var folder = new TempFolder();
        var ids = new List<long>();
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            using var sink = new FrameSink(store, () => (FrameFormat.Jpeg, 90));
            var monitorId = store.GetOrAddMonitor(Main);
            var app = store.GetOrAddApp("rider64.exe", null);
            foreach (var (seconds, shade) in new (int, byte)[] { (0, 100), (10, 200) })
            {
                using var transaction = store.BeginTransaction();
                var id = store.InsertCapture(Nine.AddSeconds(seconds), CaptureKind.Active, app, "Code", new PixelRect(10, 10, 50, 30));
                var pixels = new byte[I420.Size(Main.Bounds.Width, Main.Bounds.Height)];
                Array.Fill(pixels, shade);
                var frame = sink.Append(monitorId, new ScreenCapture { Monitor = Main, Width = 128, Height = 64, Frame = pixels, Hash = shade }, Nine.AddSeconds(seconds));
                store.InsertFrame(id, monitorId, frame);
                transaction.Commit();
                sink.Committed();
                ids.Add(id);
            }
            sink.CloseAll();
            sink.Committed();
            // The older picture's file is gone (deleted by hand, say).
            using var path = db.Command($"SELECT s.path FROM frames f JOIN segments s ON s.id = f.segment_id WHERE f.capture_id = {ids[0]};");
            File.Delete(Path.Combine(folder.Path, (string)path.ExecuteScalar()!));
        }

        Assert.Equal(1, PastScreenReading.Run(folder.Path, new FakeReader(), Nine, Nine.AddHours(1), null, TestContext.Current.CancellationToken));
        using var check = WorklapseDatabase.Open(folder.Path);
        Assert.Null(new ScreenWordStore(check).Get(ids[0]));
        Assert.NotNull(new ScreenWordStore(check).Get(ids[1]));
    }

    /// <summary>A capture's words, failing the test when it has none.</summary>
    private static string[] Present(string[]? words)
    {
        Assert.NotNull(words);
        return words;
    }

    private static List<(long Id, int Seconds)> Captures(WorklapseDatabase db)
    {
        using var command = db.Command("SELECT id, ts_ms FROM captures ORDER BY ts_ms;");
        using var reader = command.ExecuteReader();
        var captures = new List<(long, int)>();
        while (reader.Read())
            captures.Add((reader.GetInt64(0), (int)(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)) - Nine).TotalSeconds));
        return captures;
    }

    /// <summary>"Reads" a window as the shade of its first pixel, and counts the pictures it was given.</summary>
    private sealed class FakeReader : IScreenTextReader
    {
        public int Reads;

        public string Read(byte[] gray, int width, int height)
        {
            Interlocked.Increment(ref Reads);
            return $"shade{gray[0]} docket 42 a";
        }
    }

    /// <summary>The recorder on a test folder with a fake desktop, ticked by hand.</summary>
    private sealed class RecorderRun : IAsyncDisposable
    {
        private readonly Clock _clock = new();
        private readonly FakeForeground _foreground = new();
        private readonly FakeScreen _screen = new();

        public Recorder Recorder { get; private set; } = null!;
        public SettingsStore Settings { get; private set; } = null!;

        public static RecorderRun Start(string dataFolder, IScreenTextReader reader)
        {
            var run = new RecorderRun();
            run.Settings = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
            run.Settings.Save(new WorklapseSettings { DataFolder = dataFolder, CaptureMode = CaptureMode.AllMonitors, IdleMinutes = 120 });
            var platform = new PlatformServices(run._screen, run._foreground, new FakeIdle(), new FakeSession()) { TextReader = reader };
            run.Recorder = new Recorder(run.Settings, platform, store => new FrameSink(store, () => (FrameFormat.Video, 30)), run._clock);
            return run;
        }

        /// <summary>A tick at 09:00 + <paramref name="seconds"/> with this app in front and the screen all one shade.</summary>
        public void Tick(int seconds, string app, byte picture)
        {
            _clock.Now = Nine.AddSeconds(seconds);
            _foreground.Window = new ForegroundWindow(app, null, app, new PixelRect(10, 10, 50, 30));
            _screen.Picture = picture;
            Recorder.Tick();
            Assert.Equal(RecorderState.Recording, Recorder.Status.State);
        }

        public ValueTask DisposeAsync() => Recorder.DisposeAsync(); // finishes what the screen reading has queued

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = Nine;
            public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
        }

        private sealed class FakeScreen : IScreenSource
        {
            public byte Picture { get; set; }
            public IReadOnlyList<MonitorInfo> GetMonitors() => [Main];

            public ScreenCapture? Capture(MonitorInfo monitor)
            {
                var frame = new byte[I420.Size(monitor.Bounds.Width, monitor.Bounds.Height)];
                Array.Fill(frame, Picture);
                return new ScreenCapture { Monitor = monitor, Width = monitor.Bounds.Width, Height = monitor.Bounds.Height, Frame = frame, Hash = Picture };
            }

            public void Dispose() { }
        }

        private sealed class FakeForeground : IForegroundWindowProbe
        {
            public ForegroundWindow? Window { get; set; }
            public ForegroundWindow? GetForegroundWindow() => Window;
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
