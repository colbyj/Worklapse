using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Plugging monitors in and out: which monitors each capture had, and what happens to their segments.</summary>
public class MonitorLayoutTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    // A docked laptop, small: a monitor on the left, the main one, and the laptop's panel on the right.
    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY3", new PixelRect(-64, 0, 64, 48), false) { StableId = "left" };
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY5", new PixelRect(0, 0, 128, 64), true) { StableId = "main" };
    private static readonly MonitorInfo Panel = new(@"\\.\DISPLAY2", new PixelRect(128, 0, 96, 64), false) { StableId = "panel" };

    // Undocked: the panel alone, primary, under another device name.
    private static readonly MonitorInfo PanelAlone = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 96, 64), true) { StableId = "panel" };

    [Fact]
    public async Task EachCaptureRecordsTheMonitorsAttachedAndAnUnpluggedMonitorsSegmentIsEncodedAtOnce()
    {
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path))
        {
            run.Tick(0, [Left, Main, Panel]);
            run.Tick(10, [PanelAlone]);                  // undocked
            run.Tick(20, [PanelAlone], idle: true);
            Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout));

            // The docked monitors' segments were finished when they went away, not left staged until a pause.
            using var db = WorklapseDatabase.Open(folder.Path);
            using var states = db.Command("SELECT m.device_name, s.state FROM segments s JOIN monitors m ON m.id = s.monitor_id ORDER BY s.id;");
            using var reader = states.ExecuteReader();
            var segments = new List<(string, int)>();
            while (reader.Read())
                segments.Add((reader.GetString(0), reader.GetInt32(1)));
            Assert.Equal([(Left.DeviceName, 1), (Main.DeviceName, 1), (Panel.DeviceName, 1), (PanelAlone.DeviceName, 0)], segments);
        }

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var day = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime));
        string[] Attached(CaptureRecord c) => c.MonitorIds.Select(id => day.Monitors.Single(m => m.Id == id).DeviceName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Panel.DeviceName, Left.DeviceName, Main.DeviceName], Attached(day.Captures[0]));
        Assert.Equal([PanelAlone.DeviceName], Attached(day.Captures[1]));
        Assert.Equal([PanelAlone.DeviceName], Attached(day.Captures[2]));  // idle captures too
    }

    [Fact]
    public async Task InActiveApplicationModeMonitorsNotCapturedStillBelongToTheLayout()
    {
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path, CaptureMode.ActiveApplication))
            run.Tick(0, [Left, Main, Panel]);

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var day = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime));
        Assert.Single(day.Captures[0].Frames);
        Assert.Equal(3, day.Captures[0].MonitorIds.Count);
        Assert.Equal(3, day.Monitors.Count);
    }

    [Fact]
    public async Task AMonitorThatComesBackDoesNotReuseAFrameFromBeforeItLeft()
    {
        // Windows moved the main monitor to the left of the panel and back, keeping its device name. Before,
        // the screen source compared each capture with its previous one under that device name: the main
        // monitor's return looked unchanged (same picture as on the left) and reused its first frame, from
        // before it moved, showing the wrong picture.
        var mainOnTheLeft = Main with { Bounds = new PixelRect(-128, 0, 128, 64), IsPrimary = false };
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path))
        {
            run.Tick(0, [Main, Panel], picture: 10);
            run.Tick(10, [mainOnTheLeft, Panel with { IsPrimary = true }], picture: 20);
            run.Tick(20, [Main, Panel], picture: 20);
        }

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var day = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime));
        var mainId = day.Monitors.Single(m => m.Bounds == Main.Bounds).Id;
        var first = day.Captures[0].Frames.Single(f => f.MonitorId == mainId).Frame;
        var last = day.Captures[2].Frames.Single(f => f.MonitorId == mainId).Frame;
        Assert.NotEqual(first, last);
        Assert.NotEqual(first.SegmentId, last.SegmentId); // its segment was finished when it moved
    }

    [Fact]
    public async Task AnUnchangedMonitorReusesItsFrame()
    {
        using var folder = new TempFolder();
        await using (var run = RecorderRun.Start(folder.Path))
        {
            run.Tick(0, [Main], picture: 10);
            run.Tick(10, [Main], picture: 10);
        }

        using var history = new HistoryStore(WorklapseDatabase.Open(folder.Path));
        var captures = history.LoadDay(DateOnly.FromDateTime(Nine.LocalDateTime)).Captures;
        Assert.Equal(captures[0].Frames.Single().Frame, captures[1].Frames.Single().Frame);
    }

    // --- working out the layout of captures recorded without one ---------------------------------

    // The same desktop as monitors rows: what replugging leaves behind (device names reassigned, monitors moved).
    private static readonly MonitorRecord LeftRow = Row(1, Left);
    private static readonly MonitorRecord MainRow = Row(2, Main);
    private static readonly MonitorRecord PanelRow = Row(3, Panel);
    private static readonly MonitorRecord PanelAloneRow = Row(4, PanelAlone);
    private static readonly MonitorRecord MainLeftOfPanelRow = new(5, Panel.DeviceName, new PixelRect(-128, 0, 128, 64), false);
    private static readonly Dictionary<long, MonitorRecord> Rows =
        new[] { LeftRow, MainRow, PanelRow, PanelAloneRow, MainLeftOfPanelRow }.ToDictionary(m => m.Id);

    [Fact]
    public void ACaptureWithFramesOfSeveralMonitorsHadExactlyThose()
    {
        var captures = new[] { Capture(0, PanelAloneRow), Capture(10, LeftRow, MainRow, PanelRow) };
        Assert.Equal([1L, 2, 3], MonitorLayouts.Guess(captures, Rows)[1]);
    }

    [Fact]
    public void ACaptureWithOneFrameGetsTheDaysOtherMonitorsThatFitAlongsideIt()
    {
        // Active application mode while docking: the panel alone, then the main monitor left of it, then the
        // final arrangement, one monitor per capture.
        var captures = new[]
        {
            Capture(0, PanelAloneRow),
            Capture(10, MainLeftOfPanelRow),
            Capture(20, MainRow),
            Capture(30, PanelRow),
            Capture(40, LeftRow),
            Capture(50, MainRow),
        };
        var layouts = MonitorLayouts.Guess(captures, Rows);

        // The panel alone overlaps the main monitor; the main monitor left of the panel overlaps the left
        // monitor and has the panel's device name.
        Assert.Equal([1L, 2, 3], layouts[5]);
        foreach (var layout in layouts)
            foreach (var a in layout)
                foreach (var b in layout.Where(b => b != a))
                {
                    Assert.True(Rows[a].Bounds.Intersect(Rows[b].Bounds).IsEmpty);
                    Assert.NotEqual(Rows[a].DeviceName, Rows[b].DeviceName);
                    Assert.False(Rows[a].IsPrimary && Rows[b].IsPrimary);
                }
    }

    [Fact]
    public void IdleCapturesTakeTheLayoutOfTheCaptureBeforeThem()
    {
        var captures = new[] { Capture(0), Capture(10, LeftRow, MainRow, PanelRow), Capture(20), Capture(30, PanelAloneRow, MainLeftOfPanelRow), Capture(40) };
        var layouts = MonitorLayouts.Guess(captures, Rows);
        Assert.Equal([1L, 2, 3], layouts[0]);  // nothing before: the first one after
        Assert.Equal([1L, 2, 3], layouts[2]);
        Assert.Equal([4L, 5], layouts[4]);
    }

    private static MonitorRecord Row(long id, MonitorInfo monitor) => new(id, monitor.DeviceName, monitor.Bounds, monitor.IsPrimary);

    private static CaptureRecord Capture(int secondsAfterNine, params MonitorRecord[] framesOf) =>
        new(secondsAfterNine, Nine.AddSeconds(secondsAfterNine), framesOf.Length == 0 ? CaptureKind.Idle : CaptureKind.Active,
            "app.exe", null, "Title", null, false, null, false,
            framesOf.Select(m => new MonitorFrame(m.Id, new FrameRef(m.Id, secondsAfterNine))).ToList(), []);

    /// <summary>The recorder on a test folder with a fake desktop, ticked by hand.</summary>
    private sealed class RecorderRun : IAsyncDisposable
    {
        private readonly Clock _clock = new();
        private readonly FakeScreen _screen = new();
        private readonly FakeIdle _idle = new();

        public Recorder Recorder { get; private set; } = null!;
        public StagedVideoSink? Sink { get; private set; }

        public static RecorderRun Start(string dataFolder, CaptureMode mode = CaptureMode.AllMonitors)
        {
            var run = new RecorderRun();
            var settings = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
            settings.Save(new WorklapseSettings { DataFolder = dataFolder, CaptureMode = mode, IdleMinutes = 120 });
            var platform = new PlatformServices(run._screen, new FakeForeground(), run._idle, new FakeSession());
            run.Recorder = new Recorder(settings, platform, store => run.Sink = new StagedVideoSink(store, () => 30), run._clock);
            return run;
        }

        /// <summary>A tick at 09:00 + <paramref name="seconds"/> with these monitors attached, all showing one picture.</summary>
        public void Tick(int seconds, MonitorInfo[] monitors, byte picture = 0, bool idle = false)
        {
            _clock.Now = Nine.AddSeconds(seconds);
            _screen.Monitors = monitors;
            _screen.Picture = picture;
            _idle.Idle = idle ? TimeSpan.FromHours(3) : TimeSpan.Zero;
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
            public byte Picture { get; set; }
            public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;

            public ScreenCapture? Capture(MonitorInfo monitor)
            {
                var frame = new byte[I420.Size(monitor.Bounds.Width, monitor.Bounds.Height)];
                Array.Fill(frame, Picture);
                return new ScreenCapture { Monitor = monitor, Width = monitor.Bounds.Width, Height = monitor.Bounds.Height, Frame = frame, Hash = Picture };
            }

            public void Dispose() { }
        }

        // Focused window on the main monitor (or the panel, undocked).
        private sealed class FakeForeground : IForegroundWindowProbe
        {
            public ForegroundWindow? GetForegroundWindow() => new("work.exe", null, "Work", new PixelRect(10, 10, 50, 30));
        }

        private sealed class FakeIdle : IIdleDetector
        {
            public TimeSpan Idle { get; set; }
            public TimeSpan GetIdleTime() => Idle;
        }

        private sealed class FakeSession : ISessionStateMonitor
        {
            public bool IsLocked => false;
            public void Dispose() { }
        }
    }
}
