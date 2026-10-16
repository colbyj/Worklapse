using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Worklapse.Core.Platform;
using static Worklapse.Capture.Windows.NativeMethods;

namespace Worklapse.Capture.Windows;

[SupportedOSPlatform("windows")]
public sealed class Win32IdleDetector : IIdleDetector
{
    public TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        // Both values are milliseconds since boot in 32 bits; unsigned subtraction handles the wrap at 49.7 days.
        var elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
