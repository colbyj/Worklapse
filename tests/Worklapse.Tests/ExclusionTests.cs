using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Apps and sites excluded on the Programs page: nothing is recorded while they have the focus.</summary>
public class ExclusionTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 128, 64), true);

    [Fact]
    public async Task NothingIsRecordedWhileAnExcludedAppOrSiteHasTheFocus()
    {
        using var folder = new TempFolder();
        var day = DateOnly.FromDateTime(Nine.LocalDateTime);
        await using (var run = RecorderRun.Start(folder.Path))
        {
            run.Tick(0, "work.exe");
            run.Tick(10, "game.exe");
            run.Tick(20, "firefox.exe", "bank.example");

            using (var history = new HistoryStore(WorklapseDatabase.Open(folder.Path)))
            {
                history.SetExcluded(new ProgramKey(ProgramKind.App, "GAME.EXE"), true); // app names ignore case
                history.SetExcluded(new ProgramKey(ProgramKind.Site, "bank.example"), true);
            }

            run.Tick(30, "game.exe");
            Assert.Equal(RecorderState.Excluded, run.Recorder.Status.State);
            run.Tick(40, "firefox.exe", "bank.example");
            Assert.Equal(RecorderState.Excluded, run.Recorder.Status.State);
            run.Tick(50, "firefox.exe", "github.com"); // the same browser on another site
            run.Tick(60, "work.exe");
            Assert.Equal(RecorderState.Recording, run.Recorder.Status.State);

            using (var history = new HistoryStore(WorklapseDatabase.Open(folder.Path)))
                history.SetExcluded(new ProgramKey(ProgramKind.App, "game.exe"), false);
            run.Tick(70, "game.exe");
        }

        using var review = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var captures = review.LoadDay(day).Captures;
        Assert.Equal([0, 10, 20, 50, 60, 70], captures.Select(c => (int)(c.Time - Nine).TotalSeconds));
        Assert.All(captures, c => Assert.Single(c.Frames));

        // What was recorded before stays.
        var programs = review.GetPrograms(TimeSpan.FromSeconds(15)).ToDictionary(p => p.Program);
        Assert.False(programs[new ProgramKey(ProgramKind.App, "game.exe")].Excluded);
        Assert.True(programs[new ProgramKey(ProgramKind.Site, "bank.example")].Excluded);
        Assert.NotEqual(TimeSpan.Zero, programs[new ProgramKey(ProgramKind.Site, "bank.example")].Recorded);
    }

    [Fact]
    public void ExcludedAppsAndSitesStayExcludedWhenTheirCapturesAreDeleted()
    {
        using var folder = new TempFolder();
        var day = DateOnly.FromDateTime(Nine.LocalDateTime);
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            var game = store.GetOrAddApp("game.exe", @"C:\Games\game.exe");
            var firefox = store.GetOrAddApp("firefox.exe", null);
            store.InsertCapture(Nine, CaptureKind.Active, game, "t", null);
            store.InsertCapture(Nine.AddSeconds(10), CaptureKind.Active, firefox, "t", null, store.GetOrAddSite("bank.example"));
            store.InsertCapture(Nine.AddSeconds(20), CaptureKind.Active, firefox, "t", null, store.GetOrAddSite("news.example"));
        }

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        history.SetExcluded(new ProgramKey(ProgramKind.App, "game.exe"), true);
        history.SetExcluded(new ProgramKey(ProgramKind.Site, "bank.example"), true);
        history.SetPendingDelete(Nine, Nine.AddSeconds(30), pending: true);
        Assert.Equal(3, history.DeleteMarked(day).Captures);

        // Their rows are what excludes them; the others go with their captures.
        Assert.Equal(["bank.example", "game.exe"], history.GetPrograms(TimeSpan.FromSeconds(15)).Where(p => p.Excluded).Select(p => p.Program.Name).Order());
        Assert.Equal(2, history.GetPrograms(TimeSpan.FromSeconds(15)).Count);
        var recording = new RecordingStore(history.Database);
        Assert.True(recording.IsExcluded("GAME.exe", null));
        Assert.True(recording.IsExcluded("firefox.exe", "bank.example"));
        Assert.False(recording.IsExcluded("firefox.exe", "news.example"));
        Assert.False(recording.IsExcluded("firefox.exe", null));
    }

    /// <summary>The recorder on a test folder with a fake desktop, ticked by hand.</summary>
    private sealed class RecorderRun : IAsyncDisposable
    {
        private readonly Clock _clock = new();
        private readonly FakeForeground _foreground = new();

        public Recorder Recorder { get; private set; } = null!;

        public static RecorderRun Start(string dataFolder)
        {
            var run = new RecorderRun();
            var settings = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
            settings.Save(new WorklapseSettings { DataFolder = dataFolder, CaptureMode = CaptureMode.AllMonitors, IdleMinutes = 120 });
            var platform = new PlatformServices(new FakeScreen(), run._foreground, new FakeIdle(), new FakeSession());
            run.Recorder = new Recorder(settings, platform, store => new FrameSink(store, () => (FrameFormat.Video, 30)), run._clock);
            return run;
        }

        /// <summary>A tick at 09:00 + <paramref name="seconds"/> with this app (showing this site) in front.</summary>
        public void Tick(int seconds, string app, string? site = null)
        {
            _clock.Now = Nine.AddSeconds(seconds);
            _foreground.Window = new ForegroundWindow(app, null, app, new PixelRect(10, 10, 50, 30), site);
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

        // A different picture every capture, so each one stores a frame.
        private sealed class FakeScreen : IScreenSource
        {
            private byte _picture;
            public IReadOnlyList<MonitorInfo> GetMonitors() => [Main];

            public ScreenCapture? Capture(MonitorInfo monitor)
            {
                var frame = new byte[I420.Size(monitor.Bounds.Width, monitor.Bounds.Height)];
                Array.Fill(frame, ++_picture);
                return new ScreenCapture { Monitor = monitor, Width = monitor.Bounds.Width, Height = monitor.Bounds.Height, Frame = frame, Hash = _picture };
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
