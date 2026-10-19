using System.Runtime.InteropServices;

namespace Worklapse.Capture.Linux;

internal static unsafe partial class Libc
{
    private const short PollIn = 0x001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    /// <summary>
    /// Waits until a file descriptor has data to read or has been closed by the other end (a read then returns 0).
    /// False when the time runs out first.
    /// </summary>
    public static bool WaitReadable(int fd, int timeoutMs)
    {
        const int Interrupted = 4; // EINTR
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var poll = new PollFd { Fd = fd, Events = PollIn };
            var left = (int)Math.Max(0, deadline - Environment.TickCount64);
            var result = Poll(&poll, 1, left);
            if (result > 0)
                return true;
            if (result == 0)
                return false;
            if (Marshal.GetLastPInvokeError() != Interrupted)
                throw new IOException($"poll failed (errno {Marshal.GetLastPInvokeError()}).");
        }
    }
}
