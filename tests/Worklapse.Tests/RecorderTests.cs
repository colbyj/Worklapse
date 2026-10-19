using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

public class RecorderTests
{
    private const string RightId = @"\\?\DISPLAY#EDO4245#4&1ea5a07c&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 1920, 1080), IsPrimary: false) { StableId = "left" };
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 3440, 1440), IsPrimary: true) { StableId = "main" };
    private static readonly MonitorInfo Right = new(@"\\.\DISPLAY3", new PixelRect(3440, 0, 2560, 1600), IsPrimary: false) { StableId = RightId };
    private static readonly MonitorInfo[] All = [Left, Main, Right];

    [Fact]
    public async Task WhereRecordingCantWorkTheRecorderSaysWhyAndRecordsNothing()
    {
        using var folder = new TempFolder();
        var settings = new SettingsStore(Path.Combine(folder.Path, "settings.json"));
        settings.Save(new WorklapseSettings { DataFolder = folder.Path, IdleMinutes = 120 });
        var platform = PlatformServices.Unavailable("Worklapse records only in a KDE Plasma Wayland session so far.");
        await using (var recorder = new Recorder(settings, platform, store => new FrameSink(store, () => (FrameFormat.Video, 30))))
        {
            recorder.Tick();
            Assert.Equal((RecorderState.Error, "Worklapse records only in a KDE Plasma Wayland session so far."),
                (recorder.Status.State, recorder.Status.Error));

            // Paused or not, it says so.
            recorder.IsPaused = true;
            recorder.Tick();
            Assert.Equal(RecorderState.Error, recorder.Status.State);
        }

        // The history is still opened (Day review and reclaiming deleted video need it), but nothing went in.
        using var db = WorklapseDatabase.Open(folder.Path);
        using var count = db.Command("SELECT COUNT(*) FROM captures;");
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public async Task PrimaryMonitorModeOnADesktopThatDoesntSayWhichIsPrimaryIsReportedNotRecordedEmpty()
    {
        using var folder = new TempFolder();
        var settings = new SettingsStore(Path.Combine(folder.Path, "settings.json"));
        settings.Save(new WorklapseSettings { DataFolder = folder.Path, IdleMinutes = 120, CaptureMode = CaptureMode.PrimaryMonitor });
        var platform = PlatformServices.Unavailable("") with { CannotRecord = null, KnowsPrimaryMonitor = false };
        await using var recorder = new Recorder(settings, platform, store => new FrameSink(store, () => (FrameFormat.Video, 30)));

        recorder.Tick();
        Assert.Equal(RecorderState.Error, recorder.Status.State);
        Assert.Contains("primary", recorder.Status.Error);

        settings.Save(settings.Current with { CaptureMode = CaptureMode.AllMonitors });
        recorder.Tick();
        Assert.Equal(RecorderState.Recording, recorder.Status.State);
    }

    [Fact]
    public void AllMonitorsCapturesEveryMonitor()
    {
        var chosen = Recorder.ChooseMonitors(new WorklapseSettings { CaptureMode = CaptureMode.AllMonitors }, All, null);
        Assert.Equal(All, chosen);
    }

    [Fact]
    public void PrimaryMonitorCapturesOnlyThePrimary()
    {
        var chosen = Recorder.ChooseMonitors(new WorklapseSettings { CaptureMode = CaptureMode.PrimaryMonitor }, All, null);
        Assert.Equal([Main], chosen);
    }

    [Fact]
    public void SelectedMonitorsMatchStableIdsIgnoringCase()
    {
        var settings = new WorklapseSettings { CaptureMode = CaptureMode.SelectedMonitors, SelectedMonitors = [RightId.ToLowerInvariant()] };
        Assert.Equal([Right], Recorder.ChooseMonitors(settings, All, null));
    }

    [Fact]
    public void SelectedMonitorsFollowTheMonitorWhenWindowsRenamesIt()
    {
        // After replugging, Windows gave the right monitor's device name to the left one.
        MonitorInfo[] replugged = [Left with { DeviceName = Right.DeviceName }, Main, Right with { DeviceName = @"\\.\DISPLAY4" }];
        var settings = new WorklapseSettings { CaptureMode = CaptureMode.SelectedMonitors, SelectedMonitors = [RightId] };
        Assert.Equal([replugged[2]], Recorder.ChooseMonitors(settings, replugged, null));
    }

    [Fact]
    public void ActiveApplicationPicksTheMonitorShowingMostOfTheWindow()
    {
        // Mostly on the right monitor, with a sliver on the main one.
        var window = new ForegroundWindow("app.exe", null, "App", new PixelRect(3300, 100, 1200, 800));
        var settings = new WorklapseSettings { CaptureMode = CaptureMode.ActiveApplication };
        Assert.Equal([Right], Recorder.ChooseMonitors(settings, All, window));
    }

    [Fact]
    public void ActiveApplicationWithoutAWindowCapturesNothing()
    {
        var settings = new WorklapseSettings { CaptureMode = CaptureMode.ActiveApplication };
        Assert.Empty(Recorder.ChooseMonitors(settings, All, null));
    }
}
