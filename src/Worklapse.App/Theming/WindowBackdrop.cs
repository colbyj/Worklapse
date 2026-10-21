using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace Worklapse.App.Theming;

/// <summary>
/// Windows 11's Mica behind the main window: the desktop's wallpaper, blurred and tinted, showing through its title
/// bar, rail and tool bars while the window is active, as in Windows' own apps. Settings and the dialogs stay solid,
/// like Windows' own tabbed dialogs. The compositor (DWM) draws it, so it works with software rendering too; Avalonia's
/// own Mica needs the GPU. The chrome brushes are transparent while it's on (<see cref="ThemeManager"/>); everything
/// else keeps its solid colours.
/// </summary>
public static partial class WindowBackdrop
{
    private const int DwmSystemBackdropType = 38; // DWMWA_SYSTEMBACKDROP_TYPE
    private const int BackdropNone = 1;           // DWMSBT_NONE
    private const int BackdropMica = 2;           // DWMSBT_MAINWINDOW

    private static readonly List<Window> Windows = [];
    private static bool _on;

    /// <summary>Windows 11 22H2 and later, the first to take a backdrop type from apps.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>
    /// Puts Mica behind <paramref name="window"/> from when it opens until it closes, whenever it's on. The window's
    /// background and chrome should be <c>UiChromeBrush</c> or <c>UiChromeHeaderBrush</c>, its content solid.
    /// </summary>
    public static void Attach(Window window)
    {
        Windows.Add(window);
        window.Opened += (_, _) => Apply(window);
        window.Closed += (_, _) => Windows.Remove(window);
    }

    /// <summary>Turns Mica on or off for the attached windows.</summary>
    public static void Use(bool on)
    {
        if (on == _on)
            return;
        _on = on;
        foreach (var window in Windows)
            Apply(window);
    }

    private static void Apply(Window window)
    {
        // Before it opens, Opened applies it.
        if (!IsSupported || window.TryGetPlatformHandle()?.Handle is not { } handle)
            return;

        // Avalonia's Transparent leaves the transparent parts of the window to DWM, which draws the backdrop there
        // once the frame covers the whole window. ClearType needs a solid background to blend with; on Mica it smears,
        // so text is antialiased in grey, as in Windows' own Mica apps.
        if (_on)
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        }
        else
        {
            window.ClearValue(TopLevel.TransparencyLevelHintProperty);
            RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Unspecified);
        }
        var frame = _on ? new Margins(-1) : new Margins(0);
        DwmExtendFrameIntoClientArea(handle, ref frame);
        var backdrop = _on ? BackdropMica : BackdropNone;
        DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Margins(int all)
    {
        public readonly int Left = all, Right = all, Top = all, Bottom = all;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
