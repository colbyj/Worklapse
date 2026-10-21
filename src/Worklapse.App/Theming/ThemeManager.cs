using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Worklapse.Core.Settings;

namespace Worklapse.App.Theming;

/// <summary>
/// Matches the UI's colours to the OS and keeps them matched: Windows 11's palette with the system accent on
/// Windows, the active colour scheme on KDE, Adwaita on GNOME and the other GTK desktops, and Breeze (light or dark,
/// with the desktop's accent) elsewhere; or another desktop's look, light or dark, as chosen in the settings.
/// Colours are written into the brushes defined in Styles/BreezeResources.axaml and <see cref="AppBrushes"/>,
/// so views refer to those brushes and never to colours.
/// </summary>
public static class ThemeManager
{
    /// <summary>Windows' default blue, which Avalonia also reports as the accent when a desktop doesn't name one.</summary>
    private static readonly Color WindowsDefaultAccent = Color.FromRgb(0, 120, 215);

    private static Application? _app;
    private static FileSystemWatcher? _kdeWatcher;
    private static DispatcherTimer? _kdeDebounce;
    private static ThemeStyle _style;
    private static ThemeMode _mode;

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

    /// <param name="mica">Windows 11's Mica shows through the chrome (<see cref="WindowBackdrop"/>).</param>
    public static void Apply(UiPalette palette, bool mica = false)
    {
        var app = _app ?? Application.Current ?? throw new InvalidOperationException("No application to theme.");
        Current = palette;

        Set(app, "UiWindowBrush", palette.Window);
        Set(app, "UiHeaderBrush", palette.Header);
        Set(app, "UiChromeBrush", mica ? Colors.Transparent : palette.Window);
        Set(app, "UiChromeHeaderBrush", mica ? Colors.Transparent : palette.Header);
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
        WindowBackdrop.Use(mica);
        AppBrushes.Apply(palette);
        Changed?.Invoke();
    }

    /// <summary>Uses the look chosen in the settings (the desktop's own by default).</summary>
    public static void Use(WorklapseSettings settings)
    {
        if (settings.ThemeStyle == _style && settings.ThemeMode == _mode)
            return;
        _style = settings.ThemeStyle;
        _mode = settings.ThemeMode;
        Update();
    }

    /// <summary>
    /// The palette for a chosen look, given the OS's current colour settings. The desktop's accent is used in every
    /// style; light or dark comes from the desktop unless chosen.
    /// </summary>
    public static UiPalette For(PlatformColorValues? values, ThemeStyle style, ThemeMode mode)
    {
        var dark = mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => values?.ThemeVariant == PlatformThemeVariant.Dark,
        };
        // Windows always reports its accent; elsewhere this value means the desktop has none.
        var accent = values?.AccentColor1 is { } a && (OperatingSystem.IsWindows() || a != WindowsDefaultAccent) ? a : (Color?)null;

        switch (Resolve(style))
        {
            case ThemeStyle.Windows:
                return UiPalette.Windows11(dark, WindowsAccent.FillShade(dark, accent ?? WindowsDefaultAccent));
            case ThemeStyle.Gtk:
                return UiPalette.Adwaita(dark, accent);
            default:
                // KDE's own colour scheme, unless light or dark was chosen and the scheme is the other. Without one
                // (KDE before a scheme has ever been applied, Qt desktops such as LXQt, anything unrecognised) Breeze.
                if (IsKde && KdeColors.Load(KdeColors.KdeGlobalsPath) is { } scheme
                    && UiPalette.FromKde(scheme) is var palette && (mode == ThemeMode.Automatic || palette.IsDark == dark))
                    return palette;
                return UiPalette.Breeze(dark, accent);
        }
    }

    private static ThemeStyle Resolve(ThemeStyle style) => style == ThemeStyle.Automatic ? DesktopStyle : style;

    /// <summary>The look of the desktop the app runs on.</summary>
    private static ThemeStyle DesktopStyle =>
        OperatingSystem.IsWindows() ? ThemeStyle.Windows
        : IsKde ? ThemeStyle.Kde
        : GtkDesktops.Any(IsDesktop) ? ThemeStyle.Gtk
        : ThemeStyle.Kde;

    private static bool IsKde => IsDesktop("KDE");

    /// <summary>GTK-based desktops, as XDG_CURRENT_DESKTOP names them; their themes are Adwaita or close to it.</summary>
    private static readonly string[] GtkDesktops = ["GNOME", "XFCE", "X-Cinnamon", "MATE", "Budgie", "Pantheon", "Unity", "LXDE"];

    /// <summary>Whether XDG_CURRENT_DESKTOP names this desktop (it's a list, e.g. "ubuntu:GNOME").</summary>
    private static bool IsDesktop(string name) =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is { } desktop
        && desktop.Split(':').Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Mica only with the Windows look: the other desktops' windows are solid.</summary>
    private static void Update() =>
        Apply(For(_app?.PlatformSettings?.GetColorValues(), _style, _mode), Resolve(_style) == ThemeStyle.Windows && WindowBackdrop.IsSupported);

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
