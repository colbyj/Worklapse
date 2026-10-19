using System.Text.Json;

namespace Worklapse.Capture.Linux;

/// <summary>A screen (KWin output) as the KWin script reports it, in KWin's logical coordinates.</summary>
/// <param name="Name">The connector, such as DP-1 or eDP-1.</param>
/// <param name="Manufacturer">From the monitor's EDID; empty for a virtual screen.</param>
internal sealed record KWinScreen(
    string Name,
    string Manufacturer,
    string Model,
    string Serial,
    double X,
    double Y,
    double Width,
    double Height,
    double Scale);

/// <remarks>KWin (6.7) doesn't tell scripts which screen is primary.</remarks>
internal sealed record KWinScreens(IReadOnlyList<KWinScreen> Screens);

/// <summary>The active window as the KWin script reports it, in KWin's logical coordinates.</summary>
/// <param name="ResourceClass">The window class (X11 WM_CLASS or the Wayland app id), such as "firefox".</param>
/// <param name="DesktopFile">The desktop file's name without ".desktop", such as "org.kde.dolphin"; often empty.</param>
internal sealed record KWinWindow(
    int Pid,
    string Caption,
    string ResourceClass,
    string DesktopFile,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>Reads the JSON reports the KWin script sends (KWinScript.js).</summary>
internal static class KWinReports
{
    /// <summary>
    /// A "window" report (null inside when no window is active) or a "screens" report. Returns false for anything
    /// else, including JSON that doesn't parse.
    /// </summary>
    public static bool TryParse(string json, out KWinWindow? window, out KWinScreens? screens)
    {
        window = null;
        screens = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            switch (String(root, "kind"))
            {
                case "window":
                    if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True)
                    {
                        window = new KWinWindow(
                            (int)Number(root, "pid"),
                            String(root, "caption"),
                            String(root, "resourceClass"),
                            String(root, "desktopFile"),
                            Number(root, "x"),
                            Number(root, "y"),
                            Number(root, "width"),
                            Number(root, "height"));
                    }
                    return true;

                case "screens":
                    var list = new List<KWinScreen>();
                    if (root.TryGetProperty("screens", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            list.Add(new KWinScreen(
                                String(item, "name"),
                                String(item, "manufacturer"),
                                String(item, "model"),
                                String(item, "serial"),
                                Number(item, "x"),
                                Number(item, "y"),
                                Number(item, "width"),
                                Number(item, "height"),
                                Number(item, "scale") is > 0 and var scale ? scale : 1));
                        }
                    }
                    screens = new KWinScreens(list);
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}
