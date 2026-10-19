using System.Runtime.Versioning;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Linux;

/// <summary>Recording in a KDE Plasma 6 Wayland session.</summary>
[SupportedOSPlatform("linux")]
public static class KdePlatform
{
    /// <summary>Whether this is a Plasma Wayland session, the only Linux session Worklapse records in so far.</summary>
    public static bool IsKdeWayland =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
        && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);

    /// <param name="iconPath">An image file for the application menu entry; null for none.</param>
    public static PlatformServices Create(string? iconPath)
    {
        if (!IsKdeWayland)
            throw new PlatformNotSupportedException("Worklapse records only in a KDE Plasma Wayland session so far.");

        // Before the first screenshot: KWin checks it.
        DesktopEntry.Install(iconPath);

        var session = KWinSession.Start();
        var foreground = new KWinForegroundWindowProbe(session);
        ScreenSaverSessionMonitor? locks = null;
        try
        {
            locks = ScreenSaverSessionMonitor.Start(session.Connection);
            return new PlatformServices(new KWinScreenSource(session), foreground, WaylandIdleDetector.Connect(), locks)
            {
                CreateScreenSource = () => new KWinScreenSource(session),
            };
        }
        catch
        {
            locks?.Dispose();
            foreground.Dispose();
            throw;
        }
    }
}
