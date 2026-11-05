using Worklapse.Capture.Linux;
using Worklapse.Core.Platform;

namespace Worklapse.Tests;

/// <summary>The parts of KDE capture that don't need KWin: its reports, the screen layout and how programs are named.</summary>
public class KdeCaptureTests
{
    // The developer's docked laptop as KWin might lay it out: an ultrawide at 100 %, the laptop panel at 150 % to its
    // right, and a 1080p monitor at 100 % to the right of that.
    private static readonly KWinScreen Ultrawide = new("DP-1", "Dell Inc.", "U3421WE", "ABC123", 0, 0, 3440, 1440, 1);
    private static readonly KWinScreen Panel = new("eDP-1", "BOE", "0x0BCA", "", 3440, 0, 1707, 1067, 1.5);
    private static readonly KWinScreen Side = new("HDMI-A-1", "", "", "", 5147, 0, 1920, 1080, 1);
    private static readonly KWinScreens Docked = new([Ultrawide, Panel, Side]);

    [Fact]
    public void ScreensAreReadFromTheScriptsReport()
    {
        const string json = """
            {"kind":"screens","screens":[
              {"name":"eDP-1","manufacturer":"BOE","model":"0x0BCA","serial":"","x":0,"y":0,"width":1707,"height":1067,"scale":1.5}]}
            """;
        Assert.True(KWinReports.TryParse(json, out var window, out var screens));
        Assert.Null(window);
        var screen = Assert.Single(screens!.Screens);
        Assert.Equal(new KWinScreen("eDP-1", "BOE", "0x0BCA", "", 0, 0, 1707, 1067, 1.5), screen);
    }

    [Fact]
    public void TheActiveWindowIsReadFromTheScriptsReport()
    {
        Assert.True(KWinReports.TryParse(
            """{"kind":"window","active":true,"pid":4242,"caption":"Inbox — Mozilla Firefox","resourceClass":"firefox","desktopFile":"firefox","x":10,"y":20,"width":800,"height":600}""",
            out var window, out var screens));
        Assert.Null(screens);
        Assert.Equal(new KWinWindow(4242, "Inbox — Mozilla Firefox", "firefox", "firefox", 10, 20, 800, 600), window);

        Assert.True(KWinReports.TryParse("""{"kind":"window","active":false}""", out window, out _));
        Assert.Null(window);

        Assert.False(KWinReports.TryParse("not json", out _, out _));
        Assert.False(KWinReports.TryParse("""{"kind":"something else"}""", out _, out _));
    }

    [Fact]
    public void MonitorsMeetEdgeToEdgeWithTheirOwnPixelSizes()
    {
        var layout = ScreenLayout.From(Docked, _ => null);
        var monitors = layout.Monitors;

        Assert.Equal(["DP-1", "eDP-1", "HDMI-A-1"], monitors.Select(m => m.DeviceName));
        Assert.Equal(new PixelRect(0, 0, 3440, 1440), monitors[0].Bounds);
        Assert.Equal(new PixelRect(3440, 0, 2560, 1600), monitors[1].Bounds);   // right after the ultrawide
        Assert.Equal(new PixelRect(6000, 0, 1920, 1080), monitors[2].Bounds);   // right after the panel
        // KWin doesn't say which is primary, so none claims to be.
        Assert.All(monitors, m => Assert.False(m.IsPrimary));

        // Never overlapping, whatever the scales.
        for (var i = 0; i < monitors.Count; i++)
            for (var j = i + 1; j < monitors.Count; j++)
                Assert.True(monitors[i].Bounds.Intersect(monitors[j].Bounds).IsEmpty);
    }

    [Fact]
    public void StackedMonitorsBesideAScaledPanelLeaveNoGaps()
    {
        // The developer's desk on Kubuntu (kscreen-doctor): two 1080p monitors stacked on the left, an ultrawide
        // beside them, the laptop panel at 135 % on the right.
        var top = new KWinScreen("DP-7", "", "", "", 0, 0, 1920, 1080, 1);
        var bottom = new KWinScreen("DP-6", "", "", "", 0, 1080, 1920, 1080, 1);
        var ultrawide = new KWinScreen("DP-2", "", "", "", 1920, 0, 3440, 1440, 1);
        var panel = new KWinScreen("eDP-1", "", "", "", 5360, 0, 1897, 1186, 1.35);
        var monitors = ScreenLayout.From(new KWinScreens([panel, ultrawide, bottom, top]),
            s => s == panel ? (2560, 1600) : null).Monitors;

        Assert.Equal(new PixelRect(0, 0, 1920, 1080), monitors.Single(m => m.DeviceName == "DP-7").Bounds);
        Assert.Equal(new PixelRect(0, 1080, 1920, 1080), monitors.Single(m => m.DeviceName == "DP-6").Bounds);
        Assert.Equal(new PixelRect(1920, 0, 3440, 1440), monitors.Single(m => m.DeviceName == "DP-2").Bounds);
        Assert.Equal(new PixelRect(5360, 0, 2560, 1600), monitors.Single(m => m.DeviceName == "eDP-1").Bounds);
    }

    [Fact]
    public void ALayoutThatWouldOverlapFallsBackToTheLargestScale()
    {
        // A at 100 % with B at 200 % beside it, and C at 200 % below A only: placed just below A, C's 2000 pixels of
        // width would run under B.
        var a = new KWinScreen("A", "", "", "", 0, 0, 1000, 1000, 1);
        var b = new KWinScreen("B", "", "", "", 1000, 0, 1000, 1000, 2);
        var c = new KWinScreen("C", "", "", "", 0, 1000, 1000, 1000, 2);
        var monitors = ScreenLayout.From(new KWinScreens([a, b, c]), _ => null).Monitors;

        Assert.Equal(new PixelRect(0, 0, 1000, 1000), monitors.Single(m => m.DeviceName == "A").Bounds);
        Assert.Equal(new PixelRect(2000, 0, 2000, 2000), monitors.Single(m => m.DeviceName == "B").Bounds);
        Assert.Equal(new PixelRect(0, 2000, 2000, 2000), monitors.Single(m => m.DeviceName == "C").Bounds);
        for (var i = 0; i < monitors.Count; i++)
            for (var j = i + 1; j < monitors.Count; j++)
                Assert.True(monitors[i].Bounds.Intersect(monitors[j].Bounds).IsEmpty);
    }

    [Fact]
    public void AMonitorsStableIdIsItsEdidPlusTheConnectorOrJustTheConnector()
    {
        var monitors = ScreenLayout.From(Docked, _ => null).Monitors;
        Assert.Equal("Dell Inc.|U3421WE|ABC123|DP-1", monitors[0].StableId);
        Assert.Equal("HDMI-A-1", monitors[2].StableId);
    }

    [Fact]
    public void ACapturesActualSizeWinsOverTheEstimate()
    {
        // A 2560 × 1600 panel at 175 % is 1463 × 914 logical: 2560 × 1599 when scaled back.
        var panel = new KWinScreen("eDP-1", "", "", "", 3440, 0, 1463, 914, 1.75);
        var screens = new KWinScreens([Ultrawide, panel]);

        Assert.Equal(new PixelRect(3440, 0, 2560, 1599), ScreenLayout.From(screens, _ => null).Monitors[1].Bounds);
        var captured = ScreenLayout.From(screens, s => s == panel ? (2560, 1600) : null);
        Assert.Equal(new PixelRect(3440, 0, 2560, 1600), captured.Monitors[1].Bounds);
    }

    [Fact]
    public void AWindowIsConvertedWithTheScaleOfTheScreenShowingMostOfIt()
    {
        var layout = ScreenLayout.From(Docked, _ => null);

        // On the panel (150 %): 100 logical units in, 600 × 400 big.
        Assert.Equal(new PixelRect(3440 + 150, 75, 900, 600), layout.ToPixels(3440 + 100, 50, 600, 400));

        // Mostly on the ultrawide, hanging over onto the panel: converted at 100 % from the ultrawide's origin.
        Assert.Equal(new PixelRect(3000, 0, 600, 300), layout.ToPixels(3000, 0, 600, 300));

        // The window the recorder masks to lies within its monitor's capture.
        var window = layout.ToPixels(3440 + 100, 50, 600, 400);
        var panel = layout.Monitors[1];
        var inCapture = window.Offset(-panel.Bounds.X, -panel.Bounds.Y);
        Assert.Equal(new PixelRect(150, 75, 900, 600), inCapture);
    }

    [Fact]
    public void AWindowOffEveryScreenGoesToTheNearestOne()
    {
        var layout = ScreenLayout.From(Docked, _ => null);
        // Just right of the last screen, as when a window is dragged off the edge.
        var rect = layout.ToPixels(5147 + 1920 + 10, 0, 100, 100);
        Assert.Equal(6000 + 1930, rect.X);
    }

    [Theory]
    [InlineData("/usr/lib/firefox/firefox", "firefox", "", "firefox")]
    [InlineData("/usr/share/code/code", "Code", "code", "code")]
    [InlineData("/usr/bin/python3.12", "hamster", "", "hamster")]          // a Python app: named by its window
    [InlineData("/usr/lib/jvm/java-21/bin/java", "jetbrains-idea", "", "jetbrains-idea")]
    [InlineData("/opt/app/electron37", "", "obsidian", "obsidian")]       // no class: the desktop file
    [InlineData(null, "konsole", "org.kde.konsole", "konsole")]           // unreadable /proc entry
    [InlineData(null, "", "", "unknown")]
    public void ProgramsAreNamedByTheirExecutableUnlessItOnlyHostsThem(string? path, string resourceClass, string desktopFile,
        string expected)
    {
        Assert.Equal(expected, ProcessIdentity.Name(path, resourceClass, desktopFile));
    }

    [Theory]
    [InlineData("python3", true)]
    [InlineData("python3.12", true)]
    [InlineData("pythonista", false)]
    [InlineData("shotwell", false)]
    [InlineData("node", true)]
    public void HostsAreRecognisedWithTheirVersionNumbers(string executable, bool host)
    {
        Assert.Equal(host, ProcessIdentity.IsHost(executable));
    }

    [Fact]
    public void TheDesktopFileNamesTheExecutableAndTheScreenshotInterface()
    {
        var contents = DesktopEntry.Contents("/home/me/Worklapse/bin/Worklapse", "/home/me/Worklapse/bin/worklapse.png");
        Assert.Contains("\nExec=/home/me/Worklapse/bin/Worklapse\n", contents);
        Assert.Contains("\nIcon=/home/me/Worklapse/bin/worklapse.png\n", contents);
        Assert.Contains("\nX-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2\n", contents);
        Assert.StartsWith("[Desktop Entry]\n", contents);
    }

    [Fact]
    public void TheEntryForKWinFromAnAppImageIsLeftOutOfTheMenu()
    {
        var contents = DesktopEntry.Contents("/tmp/.mount_WorklaAbC123/usr/bin/Worklapse", null, hidden: true);
        Assert.Contains("\nExec=/tmp/.mount_WorklaAbC123/usr/bin/Worklapse\n", contents);
        Assert.Contains("\nNoDisplay=true\n", contents);
        Assert.Contains("\nX-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2\n", contents);
        Assert.DoesNotContain("NoDisplay", DesktopEntry.Contents("/home/me/Worklapse/bin/Worklapse", null));
    }

    [Fact]
    public void TheAutostartEntryPassesItsArgument()
    {
        var contents = DesktopEntry.Contents("/home/me/My Apps/Worklapse", null, "--autostart");
        Assert.Contains("\nExec=\"/home/me/My Apps/Worklapse\" --autostart\n", contents);
        Assert.DoesNotContain("Icon=", contents);
    }

    [Theory]
    [InlineData("/opt/worklapse/Worklapse", "/opt/worklapse/Worklapse")]
    [InlineData("/home/me/My Apps/Worklapse", "\"/home/me/My Apps/Worklapse\"")]
    [InlineData("/home/me/$weird/Worklapse", "\"/home/me/\\\\$weird/Worklapse\"")]
    [InlineData(@"/home/me/back\slash", "\"/home/me/back\\\\\\\\slash\"")]
    public void ExecPathsAreQuotedAsTheDesktopEntrySpecificationSays(string path, string expected)
    {
        Assert.Equal(expected, DesktopEntry.Quote(path));
    }

    [Theory]
    [InlineData("wayland-0", "/run/user/1000", "/run/user/1000/wayland-0")]
    [InlineData("/tmp/other-compositor", "/run/user/1000", "/tmp/other-compositor")]
    [InlineData("", "/run/user/1000", null)]
    [InlineData("wayland-0", null, null)]
    public void TheWaylandSocketIsFoundFromTheEnvironment(string display, string? runtimeDirectory, string? expected)
    {
        Assert.Equal(expected, WaylandConnection.SocketPath(display, runtimeDirectory)?.Replace('\\', '/'));
    }
}
