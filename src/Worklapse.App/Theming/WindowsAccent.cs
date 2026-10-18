using System.Runtime.Versioning;
using Avalonia.Media;
using Microsoft.Win32;

namespace Worklapse.App.Theming;

/// <summary>
/// The shade of the accent Windows 11 fills controls with: AccentDark1 on light, AccentLight2 on dark. Windows
/// keeps its whole accent palette in the registry; Avalonia reports only the base accent.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAccent
{
    // AccentPalette: eight 4-byte colours (R, G, B, unused): Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, and
    // one more.
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const int Light2 = 1;
    private const int Dark1 = 4;

    /// <summary>
    /// The fill shade for a light or dark theme; from <paramref name="accent"/> (the base accent) by Fluent's
    /// steps when the registry has no palette.
    /// </summary>
    public static Color FillShade(bool dark, Color accent) =>
        ReadPalette() is { } palette
            ? palette[dark ? Light2 : Dark1]
            : ShiftLightness(accent, dark ? 70 / 255d : -28.5 / 255d);

    private static Color[]? ReadPalette()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
        if (key?.GetValue("AccentPalette") is not byte[] { Length: >= 32 } bytes)
            return null;
        var colors = new Color[8];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
        return colors;
    }

    private static Color ShiftLightness(Color color, double step)
    {
        var hsl = color.ToHsl();
        return new HslColor(hsl.A, hsl.H, hsl.S, Math.Clamp(hsl.L + step, 0, 1)).ToRgb();
    }
}
