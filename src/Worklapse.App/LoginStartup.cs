using System.Runtime.Versioning;
using Microsoft.Win32;
using Worklapse.Capture.Linux;

namespace Worklapse.App;

/// <summary>
/// Starting Worklapse when the user logs in: a value under the Run key on Windows, an XDG autostart entry on Linux.
/// The operating system holds the setting (not settings.json), so turning it off in Task Manager's Startup apps or the
/// desktop's autostart settings shows here too. Started that way, Worklapse is given <see cref="Argument"/> and stays in
/// the tray.
/// </summary>
public static class LoginStartup
{
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager's Startup apps switch: a 12-byte value per Run entry, its first byte odd while switched off.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Worklapse";

    /// <summary>
    /// False where it can't be offered: another OS, or Worklapse running through the dotnet host (dotnet Worklapse.dll),
    /// which has no executable of its own to start.
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() ? Executable is not null
        : OperatingSystem.IsLinux() && AutostartEntry.IsSupported;

    public static bool IsEnabled =>
        OperatingSystem.IsWindows() ? IsEnabledOnWindows()
        : OperatingSystem.IsLinux() && AutostartEntry.IsEnabled;

    /// <summary>Starts this executable at login from now on (switched on again if it was switched off elsewhere).</summary>
    public static void Enable()
    {
        if (OperatingSystem.IsWindows())
            EnableOnWindows();
        else if (OperatingSystem.IsLinux())
            AutostartEntry.Enable(App.LinuxIconPath(), Argument);
    }

    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
            DisableOnWindows();
        else if (OperatingSystem.IsLinux())
            AutostartEntry.Disable();
    }

    private static string? Executable =>
        Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path) != "dotnet" ? path : null;

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledOnWindows()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string)
            return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static void EnableOnWindows()
    {
        var executable = Executable
            ?? throw new InvalidOperationException("Worklapse runs through the dotnet host; there's no executable to start at login.");
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, $"\"{executable}\" {Argument}");
        // No value counts as switched on.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    private static void DisableOnWindows()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
