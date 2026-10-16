using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Worklapse.Core.Platform;
using static Worklapse.Capture.Windows.NativeMethods;

namespace Worklapse.Capture.Windows;

[SupportedOSPlatform("windows")]
public sealed class Win32ForegroundWindowProbe : IForegroundWindowProbe
{
    public ForegroundWindow? GetForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
            return null;

        // A hung app's window is covered by a "Ghost" window owned by DWM; attribute it to the hung app.
        var className = GetClassNameOf(hwnd);
        var owner = hwnd;
        if (string.Equals(className, "Ghost", StringComparison.Ordinal) && HungWindowFromGhostWindow(hwnd) is var hung and not 0)
            owner = hung;

        GetWindowThreadProcessId(owner, out var processId);

        // Store (UWP) apps are hosted in ApplicationFrameHost; the real app owns a child window.
        if (string.Equals(className, "ApplicationFrameWindow", StringComparison.Ordinal))
        {
            var hostPid = processId;
            EnumChildWindows(hwnd, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid == hostPid)
                    return true;
                processId = childPid;
                return false;
            }, 0);
        }

        var path = GetProcessPath(processId);
        var name = path is null ? GetProcessNameFallback(processId) : Path.GetFileName(path);
        return new ForegroundWindow(name, path, GetTitle(hwnd), GetBounds(hwnd));
    }

    /// <summary>
    /// Name of a process that can't be opened (protected or higher-integrity, such as the window Windows
    /// shows for a hung app). The process snapshot API still reports its image name.
    /// </summary>
    private static string GetProcessNameFallback(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            return $"pid {processId}"; // the process exited in between
        }
        catch (InvalidOperationException)
        {
            return $"pid {processId}";
        }
    }

    private static unsafe string GetTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = GetWindowText(hwnd, p, buffer.Length);
            return new string(buffer, 0, Math.Max(0, copied));
        }
    }

    private static unsafe string GetClassNameOf(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassName(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private static PixelRect GetBounds(nint hwnd)
    {
        // The extended frame bounds exclude the invisible resize borders that GetWindowRect includes.
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(hwnd, out rect))
            return default;
        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static unsafe string? GetProcessPath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
            return null; // elevated or protected processes can't be opened from a normal session
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
