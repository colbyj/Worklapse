using Avalonia.Media;

namespace Worklapse.App.Theming;

/// <summary>
/// Brushes that code draws with or hands to views (the timeline, the status dots). <see cref="ThemeManager"/>
/// recolours these same instances when the OS theme changes, so whatever holds them follows.
/// </summary>
public static class AppBrushes
{
    // What time was spent on: the same colours on every desktop (the meaning shouldn't change with the accent),
    // softer or lighter variants on dark backgrounds.
    public static SolidColorBrush Productive { get; } = new();
    public static SolidColorBrush Other { get; } = new();
    public static SolidColorBrush Idle { get; } = new();
    public static SolidColorBrush Marked { get; } = new();
    public static SolidColorBrush NotRecorded { get; } = new();
    public static SolidColorBrush Recording { get; } = new();
    public static SolidColorBrush Inactive { get; } = new();
    public static SolidColorBrush Error { get; } = new();

    // The timeline's own parts, from the desktop's palette.
    public static SolidColorBrush TimelineTrack { get; } = new();
    public static SolidColorBrush TimelineFrame { get; } = new();
    public static SolidColorBrush TimelineHourLine { get; } = new();
    public static SolidColorBrush TimelineLabel { get; } = new();
    public static SolidColorBrush MarkedFill { get; } = new();
    public static SolidColorBrush SelectionFill { get; } = new();
    public static SolidColorBrush SelectionEdge { get; } = new();

    static AppBrushes() => Apply(ThemeManager.Current);

    internal static void Apply(UiPalette palette)
    {
        var dark = palette.IsDark;
        Productive.Color = dark ? Color.Parse("#3A91D0") : Color.Parse("#2980B9");
        Other.Color = Color.Parse("#F39C1F");
        Idle.Color = dark ? Color.Parse("#5C6166") : Color.Parse("#BDC3C7");
        Marked.Color = dark ? Color.Parse("#E35D6A") : Color.Parse("#DA4453");
        NotRecorded.Color = dark ? Color.Parse("#6E757C") : Color.Parse("#A0A4A8");
        Recording.Color = Marked.Color;
        Inactive.Color = NotRecorded.Color;
        Error.Color = Marked.Color;

        TimelineTrack.Color = palette.View;
        TimelineFrame.Color = palette.Frame;
        TimelineHourLine.Color = UiPalette.Mix(palette.View, palette.Text, 0.08);
        TimelineLabel.Color = palette.SecondaryText;
        MarkedFill.Color = UiPalette.WithAlpha(Marked.Color, 56);
        SelectionFill.Color = UiPalette.WithAlpha(palette.Accent, 46);
        SelectionEdge.Color = palette.Accent;
    }
}
