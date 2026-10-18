using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Worklapse.App.Theming;

/// <summary>
/// Matches the UI's colours to the OS and keeps them matched: Windows 11's palette with the system accent on
/// Windows, the active colour scheme on KDE, Adwaita on GNOME, and Breeze (light or dark, with the desktop's accent)
/// elsewhere.
/// Colours are written into the brushes defined in Styles/BreezeResources.axaml and <see cref="AppBrushes"/>,
/// so views refer to those brushes and never to colours.
/// </summary>
public static class ThemeManager
{
    /// <summary>What Avalonia reports as the accent when the desktop doesn't name one (Windows' default blue).</summary>
    private static readonly Color NoAccent = Color.FromRgb(0, 120, 215);

    private static Application? _app;
    private static FileSystemWatcher? _kdeWatcher;
    private static DispatcherTimer? _kdeDebounce;

    public static UiPalette Current { get; private set; } = UiPalette.Breeze(dark: false);

    /// <summary>Raised on the UI thread after the colours change, for controls that draw with them.</summary>
    public static event Action? Changed;

    /// <summary>Applies the OS's colours, and again whenever they change.</summary>
    public static void Start(Application app)
    {
        _app = app;
        if (app.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        if (IsKde)
            WatchKdeGlobals();
        Update();
    }

    public static void Apply(UiPalette palette)
    {
        var app = _app ?? Application.Current ?? throw new InvalidOperationException("No application to theme.");
        Current = palette;

        Set(app, "UiWindowBrush", palette.Window);
        Set(app, "UiHeaderBrush", palette.Header);
        Set(app, "UiViewBrush", palette.View);
        Set(app, "UiTextBrush", palette.Text);
        Set(app, "UiSecondaryTextBrush", palette.SecondaryText);
        Set(app, "UiDisabledTextBrush", palette.DisabledText);
        Set(app, "UiFrameBrush", palette.Frame);
        Set(app, "UiSeparatorBrush", palette.Separator);
        Set(app, "UiGroupFrameBrush", palette.GroupFrame);
        Set(app, "UiTabMarkBrush", palette.TabMark);
        Set(app, "UiAccentBrush", palette.Accent);
        Set(app, "UiAccentHoverBrush", palette.AccentHover);
        Set(app, "UiAccentTintBrush", palette.AccentTint);
        Set(app, "UiAccentPressedBrush", palette.AccentPressed);
        Set(app, "UiAccentStrongBrush", palette.AccentStrong);
        Set(app, "UiOnAccentBrush", palette.OnAccent);
        Set(app, "UiNegativeBrush", palette.Negative);
        Set(app, "UiToolTipBrush", palette.ToolTip);
        Set(app, "UiScrollTrackBrush", palette.ScrollTrack);
        Set(app, "UiScrollThumbBrush", palette.ScrollThumb);
        Set(app, "UiDefaultButtonFrameBrush", palette.DefaultButtonFrame);
        Set(app, "UiDefaultButtonTextBrush", palette.DefaultButtonText);
        SetGradient(app, "ButtonBackground", palette.ButtonTop, palette.ButtonBottom);
        SetGradient(app, "ButtonBorderBrush", palette.ButtonFrameTop, palette.ButtonFrameBottom);
        SetGradient(app, "AccentButtonBackground", palette.DefaultButtonTop, palette.DefaultButtonBottom);

        // Fluent's own resources for whatever the brushes above don't cover, and dark window title bars.
        app.RequestedThemeVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        AppBrushes.Apply(palette);
        Changed?.Invoke();
    }

    /// <summary>The palette for this OS and its current settings.</summary>
    public static UiPalette ForOs(PlatformColorValues? values)
    {
        var dark = values?.ThemeVariant == PlatformThemeVariant.Dark;
        if (OperatingSystem.IsWindows())
            return UiPalette.Windows11(dark, WindowsAccent.FillShade(dark, values?.AccentColor1 ?? NoAccent));
        if (IsKde && KdeColors.Load(KdeColors.KdeGlobalsPath) is { } scheme)
            return UiPalette.FromKde(scheme);
        var accent = values?.AccentColor1 is { } a && a != NoAccent ? a : (Color?)null;
        if (IsDesktop("GNOME"))
            return UiPalette.Adwaita(dark, accent);
        // Other desktops, and KDE before a colour scheme has ever been applied (Breeze is then in effect).
        return UiPalette.Breeze(dark, accent);
    }

    private static bool IsKde => IsDesktop("KDE");

    /// <summary>Whether XDG_CURRENT_DESKTOP names this desktop (it's a list, e.g. "ubuntu:GNOME").</summary>
    private static bool IsDesktop(string name) =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is { } desktop
        && desktop.Split(':').Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void Update() => Apply(ForOs(_app?.PlatformSettings?.GetColorValues()));

    /// <summary>
    /// Plasma rewrites kdeglobals when the colour scheme or accent changes; a change between two light (or two
    /// dark) schemes reaches apps only through that file, not through the portal's light/dark setting.
    /// </summary>
    private static void WatchKdeGlobals()
    {
        var path = KdeColors.KdeGlobalsPath;
        if (Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder))
            return;

        // Plasma writes the file in a few steps (a temporary file renamed over it); wait for them to settle.
        _kdeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _kdeDebounce.Tick += (_, _) =>
        {
            _kdeDebounce.Stop();
            Update();
        };
        _kdeWatcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _kdeDebounce.Stop();
            _kdeDebounce.Start();
        });
        _kdeWatcher.Changed += changed;
        _kdeWatcher.Created += changed;
        _kdeWatcher.Renamed += (s, e) => changed(s, e);
        _kdeWatcher.EnableRaisingEvents = true;
    }

    private static void Set(Application app, string key, Color color) => Find<SolidColorBrush>(app, key).Color = color;

    private static void SetGradient(Application app, string key, Color top, Color bottom)
    {
        var brush = Find<LinearGradientBrush>(app, key);
        brush.GradientStops[0].Color = top;
        brush.GradientStops[1].Color = bottom;
    }

    private static T Find<T>(Application app, string key) where T : class =>
        app.Resources.TryGetResource(key, null, out var value) && value is T brush
            ? brush
            : throw new InvalidOperationException($"The theme brush '{key}' is missing from the app resources or isn't a {typeof(T).Name}.");
}
