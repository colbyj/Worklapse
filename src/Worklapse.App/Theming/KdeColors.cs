using Avalonia.Media;

namespace Worklapse.App.Theming;

/// <summary>
/// The colours of a KDE colour scheme that Worklapse uses, as Plasma writes the active scheme into kdeglobals
/// (the file Qt and KDE apps read too). Optional ones are derived from the others when the scheme lacks them.
/// </summary>
public sealed record KdeColors
{
    public required Color WindowBackground { get; init; }
    public required Color WindowText { get; init; }
    public Color? WindowInactiveText { get; init; }
    public Color? HeaderBackground { get; init; }
    public Color? ViewBackground { get; init; }
    public Color? ButtonBackground { get; init; }
    public Color? TooltipBackground { get; init; }
    public Color? SelectionText { get; init; }
    public Color? Negative { get; init; }
    public required Color Accent { get; init; }

    private static readonly Color BreezeBlue = Color.FromRgb(61, 174, 233);

    /// <summary>Plasma 6's Breeze Light.</summary>
    public static KdeColors BreezeLight { get; } = new()
    {
        WindowBackground = Color.FromRgb(239, 240, 241),
        WindowText = Color.FromRgb(35, 38, 41),
        WindowInactiveText = Color.FromRgb(112, 125, 138),
        HeaderBackground = Color.FromRgb(222, 224, 226),
        ViewBackground = Color.FromRgb(255, 255, 255),
        ButtonBackground = Color.FromRgb(252, 252, 252),
        TooltipBackground = Color.FromRgb(247, 247, 247),
        SelectionText = Colors.White,
        Negative = Color.FromRgb(218, 68, 83),
        Accent = BreezeBlue,
    };

    /// <summary>Plasma 6's Breeze Dark.</summary>
    public static KdeColors BreezeDark { get; } = new()
    {
        WindowBackground = Color.FromRgb(32, 35, 38),
        WindowText = Color.FromRgb(252, 252, 252),
        WindowInactiveText = Color.FromRgb(161, 169, 177),
        HeaderBackground = Color.FromRgb(41, 44, 48),
        ViewBackground = Color.FromRgb(20, 22, 24),
        ButtonBackground = Color.FromRgb(41, 44, 48),
        TooltipBackground = Color.FromRgb(41, 44, 48),
        SelectionText = Colors.White,
        Negative = Color.FromRgb(218, 68, 83),
        Accent = BreezeBlue,
    };

    /// <summary>kdeglobals in the user's config folder ($XDG_CONFIG_HOME, normally ~/.config).</summary>
    public static string KdeGlobalsPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "kdeglobals");

    /// <summary>
    /// The scheme in a kdeglobals file, or null if the file doesn't hold one (Plasma only writes the colours once
    /// a scheme has been applied; until then its default, Breeze, is in effect).
    /// </summary>
    public static KdeColors? Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(text);
    }

    public static KdeColors? Parse(string kdeglobals)
    {
        var values = ReadIni(kdeglobals);
        Color? Get(string group, string key) =>
            values.TryGetValue((group, key), out var value) ? ParseColor(value) : null;

        if (Get("Colors:Window", "BackgroundNormal") is not { } window || Get("Colors:Window", "ForegroundNormal") is not { } text)
            return null;

        return new KdeColors
        {
            WindowBackground = window,
            WindowText = text,
            WindowInactiveText = Get("Colors:Window", "ForegroundInactive"),
            HeaderBackground = Get("Colors:Header", "BackgroundNormal"),
            ViewBackground = Get("Colors:View", "BackgroundNormal"),
            ButtonBackground = Get("Colors:Button", "BackgroundNormal"),
            TooltipBackground = Get("Colors:Tooltip", "BackgroundNormal"),
            SelectionText = Get("Colors:Selection", "ForegroundNormal"),
            Negative = Get("Colors:View", "ForegroundNegative"),
            // A custom accent (Plasma's accent setting) is written to General; a scheme's own is its selection colour.
            Accent = Get("General", "AccentColor")
                     ?? Get("Colors:Selection", "BackgroundNormal")
                     ?? Get("Colors:View", "DecorationFocus")
                     ?? BreezeBlue,
        };
    }

    /// <summary>
    /// Key/value pairs by group. Groups with a sub-group (such as [Colors:Header][Inactive], the colours of
    /// inactive windows) are skipped, and so are KConfig's [$e]-style key flags.
    /// </summary>
    private static Dictionary<(string Group, string Key), string> ReadIni(string text)
    {
        var values = new Dictionary<(string, string), string>();
        string? group = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                group = end > 0 && end == line.Length - 1 ? line[1..end] : null;
                continue;
            }
            var equals = line.IndexOf('=');
            if (group is null || equals <= 0)
                continue;
            var key = line[..equals].Trim();
            var flags = key.IndexOf('[');
            if (flags > 0)
                key = key[..flags];
            values[(group, key)] = line[(equals + 1)..].Trim();
        }
        return values;
    }

    /// <summary>KConfig colours: "r,g,b", "r,g,b,a" or "#rrggbb".</summary>
    private static Color? ParseColor(string value)
    {
        if (value.StartsWith('#'))
            return Color.TryParse(value, out var hex) ? hex : null;

        var parts = value.Split(',');
        if (parts.Length is not (3 or 4))
            return null;
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i].Trim(), out bytes[i]))
                return null;
        }
        return Color.FromArgb(parts.Length == 4 ? bytes[3] : (byte)255, bytes[0], bytes[1], bytes[2]);
    }
}
