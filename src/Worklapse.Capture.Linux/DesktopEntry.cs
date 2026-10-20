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
    /// Writes the desktop file for the running executable, unless it is already there as it should be, and has KDE
    /// read it again. Does nothing when Worklapse runs through the dotnet host (dotnet Worklapse.dll), which KWin
    /// would see as the executable: run the Worklapse executable instead.
    /// </summary>
    /// <param name="iconPath">An image file for the menu; null for none.</param>
    public static void Install(string? iconPath)
    {
        if (Executable is not { } executable)
            return;

        var contents = Contents(executable, iconPath);
        var path = FilePath;
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == contents)
                return;
        }
        catch (IOException)
        {
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RebuildServiceCache();
    }

    /// <summary>The running executable, unless Worklapse runs through the dotnet host (dotnet Worklapse.dll).</summary>
    internal static string? Executable =>
        Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path) != "dotnet" ? path : null;

    /// <param name="argument">Added to the command line; null for none.</param>
    internal static string Contents(string executable, string? iconPath, string? argument = null)
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
