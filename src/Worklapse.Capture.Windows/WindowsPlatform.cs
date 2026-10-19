using System.Runtime.Versioning;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsPlatform
{
    public static PlatformServices Create() => new(
        new GdiScreenSource(),
        new Win32ForegroundWindowProbe(),
        new Win32IdleDetector(),
        new Win32SessionStateMonitor())
    {
        CreateScreenSource = () => new GdiScreenSource(),
    };
}
