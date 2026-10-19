namespace Worklapse.Capture.Linux;

/// <summary>Names the program a window belongs to, as Worklapse lists programs (on Windows: "firefox.exe").</summary>
internal static class ProcessIdentity
{
    /// <summary>
    /// Programs that run other programs: their executable says nothing about the app (python3 runs many), so the
    /// window's class names it instead.
    /// </summary>
    private static readonly string[] Hosts =
        ["python", "python2", "python3", "java", "node", "nodejs", "electron", "perl", "ruby", "dotnet", "mono", "wine", "wine64",
         "wine-preloader", "wine64-preloader"];

    /// <summary>The program's name and the path of its executable (null when it can't be read).</summary>
    public static (string Name, string? Path) Of(int pid, string resourceClass, string desktopFile)
    {
        var path = pid > 0 ? ExecutablePath(pid) : null;
        return (Name(path, resourceClass, desktopFile), path);
    }

    internal static string Name(string? path, string resourceClass, string desktopFile)
    {
        var fallback = resourceClass.Length > 0 ? resourceClass : desktopFile.Length > 0 ? desktopFile : "unknown";
        if (path is null)
            return fallback;
        var name = System.IO.Path.GetFileName(path);
        return IsHost(name) ? fallback : name;
    }

    /// <summary>"python3.12" and "electron37" count as their families.</summary>
    internal static bool IsHost(string executable) =>
        Hosts.Any(host => executable == host
            || executable.StartsWith(host, StringComparison.Ordinal) && executable[host.Length..].All(c => char.IsAsciiDigit(c) || c == '.'));

    /// <summary>
    /// The executable's path from /proc. For a Flatpak or Snap app it is the path inside its sandbox, which still ends
    /// in the program's own name.
    /// </summary>
    private static string? ExecutablePath(int pid)
    {
        try
        {
            var target = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            // The kernel marks an executable replaced by an update since the program started.
            const string Deleted = " (deleted)";
            return target is not null && target.EndsWith(Deleted, StringComparison.Ordinal) ? target[..^Deleted.Length] : target;
        }
        catch (IOException)
        {
            return null; // exited, or another user's
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
