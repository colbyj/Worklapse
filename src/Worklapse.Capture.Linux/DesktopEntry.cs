using System.Diagnostics;
using System.Text;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Worklapse's desktop file (~/.local/share/applications/worklapse.desktop). Besides putting Worklapse in the
/// application menu, it is what lets Worklapse take screenshots: KWin only answers screenshot requests from a program
/// whose executable is the Exec of an installed desktop file listing org.kde.KWin.ScreenShot2 under
/// X-KDE-DBUS-Restricted-Interfaces.
/// </summary>
internal static class DesktopEntry
{
    public static string FilePath
    {
        get
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(dataHome, "applications", "worklapse.desktop");
        }
    }

    /// <summary>
    /// The desktop file KWin checks when Worklapse runs from an AppImage (worklapse-kwin.desktop, hidden from the menu).
    /// The AppImage runs Worklapse from a folder it mounts, a new one each run, and KWin looks for that executable: the
    /// menu entry starts the AppImage instead, so this file names the executable of the current run.
    /// </summary>
    public static string PermissionFilePath => Path.Combine(Path.GetDirectoryName(FilePath)!, "worklapse-kwin.desktop");

    /// <summary>
    /// Writes the desktop file for the running executable, unless it is already there as it should be, and has KDE
    /// read it again. Does nothing when Worklapse runs through the dotnet host (dotnet Worklapse.dll), which KWin
    /// would see as the executable: run the Worklapse executable instead. From an AppImage, the menu entry starts the
    /// AppImage and a hidden second file (<see cref="PermissionFilePath"/>) names this run's executable for KWin.
    /// </summary>
    /// <param name="iconPath">An image file for the menu; null for none.</param>
    public static void Install(string? iconPath)
    {
        if (Executable is not { } executable)
            return;

        var changed = Write(FilePath, Contents(LaunchCommand!, MenuIcon(iconPath)));
        if (AppImage is not null)
            changed |= Write(PermissionFilePath, Contents(executable, null, hidden: true));
        else if (File.Exists(PermissionFilePath))
        {
            File.Delete(PermissionFilePath);
            changed = true;
        }
        if (changed)
            RebuildServiceCache();
    }

    /// <summary>Writes the file unless it already holds these contents; whether it wrote.</summary>
    private static bool Write(string path, string contents)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == contents)
                return false;
        }
        catch (IOException)
        {
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return true;
    }

    /// <summary>The running executable, unless Worklapse runs through the dotnet host (dotnet Worklapse.dll).</summary>
    internal static string? Executable =>
        Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path) != "dotnet" ? path : null;

    /// <summary>The AppImage file Worklapse runs from (Velopack's Linux release), or null.</summary>
    internal static string? AppImage =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>What a menu or autostart entry starts: the AppImage when Worklapse runs from one, else the executable.</summary>
    internal static string? LaunchCommand => Executable is null ? null : AppImage ?? Executable;

    /// <summary>
    /// The icon for a menu or autostart entry. From an AppImage, a copy in ~/.local/share/icons, since the one beside
    /// the executable is gone once Worklapse exits; null when there's none.
    /// </summary>
    internal static string? MenuIcon(string? iconPath)
    {
        if (iconPath is null || AppImage is null)
            return iconPath;
        var copy = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(FilePath)!)!, "icons", "worklapse.png");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(iconPath, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return File.Exists(copy) ? copy : null;
        }
    }

    /// <param name="argument">Added to the command line; null for none.</param>
    /// <param name="hidden">Left out of the application menu (NoDisplay), for KWin's check only.</param>
    internal static string Contents(string executable, string? iconPath, string? argument = null, bool hidden = false)
    {
        var text = new StringBuilder();
        text.Append("[Desktop Entry]\n");
        text.Append("Type=Application\n");
        text.Append("Name=Worklapse\n");
        text.Append("Comment=Records your screen to show where the day went\n");
        text.Append("Exec=").Append(Quote(executable));
        if (argument is not null)
            text.Append(' ').Append(Quote(argument));
        text.Append('\n');
        if (iconPath is not null)
            text.Append("Icon=").Append(iconPath).Append('\n');
        text.Append("Terminal=false\n");
        if (hidden)
            text.Append("NoDisplay=true\n");
        text.Append("Categories=Utility;\n");
        text.Append("X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2\n");
        return text.ToString();
    }

    /// <summary>
    /// An Exec argument as the desktop entry specification wants it: quoted when it has spaces or special characters,
    /// with ", `, $ and \ escaped inside the quotes, and the backslashes doubled again for the file's string escaping.
    /// </summary>
    internal static string Quote(string argument)
    {
        const string Reserved = " \t\n\"'\\><~|&;$*?#()`";
        if (!argument.Any(c => Reserved.Contains(c)))
            return argument;
        var quoted = new StringBuilder("\"");
        foreach (var c in argument)
        {
            if (c == '\\')
                quoted.Append(@"\\\\");
            else if (c is '"' or '`' or '$')
                quoted.Append(@"\\").Append(c);
            else
                quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// KDE looks desktop files up in a cache (sycoca), which it refreshes when it notices a change; refresh it now so
    /// the first screenshot doesn't race it.
    /// </summary>
    private static void RebuildServiceCache()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("kbuildsycoca6")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
                return;
            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            process.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not installed: KDE will notice the file by itself.
        }
    }
}
