using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;

namespace Worklapse.Tests;

public class RecorderTests
{
    private const string RightId = @"\\?\DISPLAY#EDO4245#4&1ea5a07c&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 1920, 1080), IsPrimary: false) { StableId = "left" };
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 3440, 1440), IsPrimary: true) { StableId = "main" };
    private static readonly MonitorInfo Right = new(@"\\.\DISPLAY3", new PixelRect(3440, 0, 2560, 1600), IsPrimary: false) { StableId = RightId };
    private static readonly MonitorInfo[] All = [Left, Main, Right];

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
