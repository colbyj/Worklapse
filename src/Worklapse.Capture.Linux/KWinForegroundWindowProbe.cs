using Worklapse.Core.Platform;

namespace Worklapse.Capture.Linux;

/// <summary>
/// The active window, from the KWin script's latest report. Owns the KWin session, which the screen sources share.
/// </summary>
/// <remarks>Browser sites aren't read yet (Windows reads the address bar through UI Automation; Linux would need AT-SPI).</remarks>
internal sealed class KWinForegroundWindowProbe(KWinSession session) : IForegroundWindowProbe, IDisposable
{
    public ForegroundWindow? GetForegroundWindow()
    {
        if (session.ActiveWindow is not { } window)
            return null;
        var (name, path) = ProcessIdentity.Of(window.Pid, window.ResourceClass, window.DesktopFile);
        var bounds = session.Layout.ToPixels(window.X, window.Y, window.Width, window.Height);
        return new ForegroundWindow(name, path, window.Caption, bounds);
    }

    public void Dispose() => session.Dispose();
}
