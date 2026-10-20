namespace Worklapse.Capture.Linux;

/// <summary>
/// Starting Worklapse when the user logs in, through an XDG autostart entry (~/.config/autostart/worklapse.desktop),
/// which KDE Plasma and the other desktops honour. The desktop's own autostart settings can switch it off (Hidden=true).
/// </summary>
public static class AutostartEntry
{
    public static string FilePath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configHome, "autostart", "worklapse.desktop");
        }
    }

    /// <summary>False when Worklapse runs through the dotnet host: there's no executable of its own to start.</summary>
    public static bool IsSupported => DesktopEntry.Executable is not null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                return File.Exists(FilePath) && !File.ReadLines(FilePath).Any(line =>
                    line.Trim() is "Hidden=true" or "X-GNOME-Autostart-enabled=false");
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <param name="iconPath">An image file for the entry; null for none.</param>
    /// <param name="argument">Added to the command line, so Worklapse knows it was started at login.</param>
    public static void Enable(string? iconPath, string argument)
    {
        var executable = DesktopEntry.Executable
            ?? throw new InvalidOperationException("Worklapse runs through the dotnet host; there's no executable to start at login.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, DesktopEntry.Contents(executable, iconPath, argument));
    }

    public static void Disable() => File.Delete(FilePath);
}
