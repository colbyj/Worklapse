using System.Runtime.InteropServices;

namespace Worklapse.Video;

/// <summary>
/// Gives freed native memory back to the OS on Linux. glibc's malloc keeps what a thread frees in that thread's arena
/// for its next allocation, so after an encoder (~100–250 MB) or the review's decoders close, their memory stays
/// resident for as long as the thread lives: the batch encoder's thread lives as long as the app. Measured: a 3440×1440
/// encoder left 241 of its 245 MB resident after it was destroyed; `malloc_trim` took that to 50 MB in ~9 ms. Windows'
/// heap returns large blocks by itself, so this does nothing there.
/// </summary>
public static partial class NativeHeap
{
    private static bool _unavailable = !OperatingSystem.IsLinux();

    /// <summary>Returns the free memory of every malloc arena to the OS. A few milliseconds; call it after large frees.</summary>
    public static void Trim()
    {
        if (Volatile.Read(ref _unavailable))
            return;
        try
        {
            MallocTrim(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not glibc (musl, say): nothing to trim with, and nothing kept the same way.
            Volatile.Write(ref _unavailable, true);
        }
    }

    [LibraryImport("libc.so.6", EntryPoint = "malloc_trim")]
    private static partial int MallocTrim(nuint pad);
}
