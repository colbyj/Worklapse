using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;

namespace Worklapse.Tests;

public class RecorderTests
{
    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY2", new PixelRect(-1920, 0, 1920, 1080), IsPrimary: false);
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 3440, 1440), IsPrimary: true);
    private static readonly MonitorInfo Right = new(@"\\.\DISPLAY3", new PixelRect(3440, 0, 2560, 1600), IsPrimary: false);
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
    public void SelectedMonitorsMatchDeviceNamesIgnoringCase()
    {
        var settings = new WorklapseSettings { CaptureMode = CaptureMode.SelectedMonitors, SelectedMonitors = [@"\\.\display3"] };
        Assert.Equal([Right], Recorder.ChooseMonitors(settings, All, null));
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

    [Fact]
    public void BlackOutsideOfKeepsOnlyTheWindow()
    {
        var capture = new ScreenCapture
        {
            Monitor = Main, Width = 4, Height = 3, Stride = 16, Changed = true,
            Pixels = Enumerable.Repeat((byte)200, 48).ToArray(),
        };

        Recorder.BlackOutsideOf(capture, new PixelRect(1, 1, 2, 1));

        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 4; x++)
        {
            var inside = y == 1 && x is 1 or 2;
            Assert.Equal(inside ? 200 : 0, capture.Pixels.Span[y * 16 + x * 4]);
        }
    }
}
