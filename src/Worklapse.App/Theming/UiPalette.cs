using Avalonia.Media;

namespace Worklapse.App.Theming;

/// <summary>
/// The colours the UI is drawn with: one desktop's light or dark look (Windows 11, a KDE colour scheme, Breeze).
/// <see cref="ThemeManager"/> picks one to match the OS and writes it into the app's brushes.
/// </summary>
public sealed record UiPalette
{
    public required bool IsDark { get; init; }

    /// <summary>Window and dialog background.</summary>
    public required Color Window { get; init; }
    /// <summary>Menu bar, tool bar, column headers.</summary>
    public required Color Header { get; init; }
    /// <summary>Text fields, lists, group boxes.</summary>
    public required Color View { get; init; }
    public required Color Text { get; init; }
    public required Color SecondaryText { get; init; }
    public required Color DisabledText { get; init; }
    public required Color Frame { get; init; }
    public required Color Separator { get; init; }
    /// <summary>The frame around group boxes.</summary>
    public required Color GroupFrame { get; init; }
    /// <summary>Selection, focus, checked marks.</summary>
    public required Color Accent { get; init; }
    /// <summary>Marks drawn on an accent fill (the tick in a check box).</summary>
    public required Color OnAccent { get; init; }
    public required Color Negative { get; init; }
    public required Color ToolTip { get; init; }
    /// <summary>The line along the top of the selected tab (Breeze); transparent for none.</summary>
    public required Color TabMark { get; init; }

    public required Color ButtonTop { get; init; }
    public required Color ButtonBottom { get; init; }
    public required Color ButtonFrameTop { get; init; }
    public required Color ButtonFrameBottom { get; init; }
    public required Color DefaultButtonTop { get; init; }
    public required Color DefaultButtonBottom { get; init; }
    public required Color DefaultButtonFrame { get; init; }
    public required Color DefaultButtonText { get; init; }

    public required Color ScrollTrack { get; init; }
    public required Color ScrollThumb { get; init; }

    /// <summary>A frame under the pointer.</summary>
    public Color AccentHover => Mix(View, Accent, 0.55);
    /// <summary>The background of a hovered item.</summary>
    public Color AccentTint => Mix(View, Accent, IsDark ? 0.25 : 0.18);
    /// <summary>The background of a pressed or selected item.</summary>
    public Color AccentPressed => Mix(View, Accent, IsDark ? 0.4 : 0.3);
    /// <summary>An accent fill or frame under the pointer.</summary>
    public Color AccentStrong => IsDark ? Mix(Accent, Colors.White, 0.2) : Mix(Accent, Colors.Black, 0.15);

    /// <summary>
    /// Windows 11's own colours (its Fluent design tokens, with frames a little stronger so controls stay visible).
    /// <paramref name="accent"/> is the shade Windows fills controls with (<see cref="WindowsAccent.FillShade"/>).
    /// Like Windows 11, dark cards have no visible frame and the selected tab no accent line.
    /// </summary>
    public static UiPalette Windows11(bool dark, Color accent)
    {
        return dark
            ? new UiPalette
            {
                IsDark = true,
                Window = Rgb(0x202020), Header = Rgb(0x282828), View = Rgb(0x2B2B2B),
                Text = Rgb(0xFFFFFF), SecondaryText = Rgb(0xC8C8C8), DisabledText = Rgb(0x6E6E6E),
                Frame = Rgb(0x464646), Separator = Rgb(0x383838), GroupFrame = Rgb(0x2B2B2B),
                Accent = accent, OnAccent = Rgb(0x000000), Negative = Rgb(0xFF99A4), ToolTip = Rgb(0x2C2C2C), TabMark = Colors.Transparent,
                ButtonTop = Rgb(0x353535), ButtonBottom = Rgb(0x2E2E2E),
                ButtonFrameTop = Rgb(0x474747), ButtonFrameBottom = Rgb(0x3A3A3A),
                DefaultButtonTop = accent, DefaultButtonBottom = Mix(accent, Colors.Black, 0.06),
                DefaultButtonFrame = Mix(accent, Colors.Black, 0.12), DefaultButtonText = Rgb(0x000000),
                ScrollTrack = Rgb(0x262626), ScrollThumb = Rgb(0x5E5E5E),
            }
            : new UiPalette
            {
                IsDark = false,
                Window = Rgb(0xF3F3F3), Header = Rgb(0xEBEBEB), View = Rgb(0xFDFDFD),
                Text = Rgb(0x1B1B1B), SecondaryText = Rgb(0x5E5E5E), DisabledText = Rgb(0xA0A0A0),
                Frame = Rgb(0xD1D1D1), Separator = Rgb(0xE3E3E3), GroupFrame = Rgb(0xD1D1D1),
                Accent = accent, OnAccent = Rgb(0xFFFFFF), Negative = Rgb(0xC42B1C), ToolTip = Rgb(0xF9F9F9), TabMark = Colors.Transparent,
                ButtonTop = Rgb(0xFDFDFD), ButtonBottom = Rgb(0xF6F6F6),
                ButtonFrameTop = Rgb(0xDDDDDD), ButtonFrameBottom = Rgb(0xC2C2C2),
                DefaultButtonTop = Mix(accent, Colors.White, 0.06), DefaultButtonBottom = accent,
                DefaultButtonFrame = Mix(accent, Colors.Black, 0.15), DefaultButtonText = Rgb(0xFFFFFF),
                ScrollTrack = Rgb(0xEEEEEE), ScrollThumb = Rgb(0xA8A8A8),
            };
    }

    /// <summary>
    /// A KDE colour scheme's colours, with what KDE derives itself (frames, disabled text) mixed from the window
    /// colours the way Breeze does. Its default button is Breeze's: an accent-tinted face in an accent frame.
    /// </summary>
    public static UiPalette FromKde(KdeColors k)
    {
        var window = k.WindowBackground;
        var text = k.WindowText;
        var button = k.ButtonBackground ?? window;
        var dark = Luma(window) < 0.5;
        return new UiPalette
        {
            IsDark = dark,
            Window = window,
            Header = k.HeaderBackground ?? window,
            View = k.ViewBackground ?? window,
            Text = text,
            SecondaryText = k.WindowInactiveText ?? Mix(window, text, 0.55),
            DisabledText = Mix(window, text, 0.4),
            Frame = Mix(window, text, 0.2),
            Separator = Mix(window, text, 0.12),
            GroupFrame = Mix(window, text, 0.2),
            Accent = k.Accent,
            OnAccent = k.SelectionText ?? Colors.White,
            Negative = k.Negative ?? Rgb(0xDA4453),
            ToolTip = k.TooltipBackground ?? k.ViewBackground ?? window,
            TabMark = k.Accent,
            ButtonTop = dark ? Mix(button, Colors.White, 0.04) : Mix(button, Colors.White, 0.3),
            ButtonBottom = dark ? Mix(button, Colors.Black, 0.08) : Mix(button, Colors.Black, 0.04),
            ButtonFrameTop = Mix(window, text, 0.18),
            ButtonFrameBottom = Mix(window, text, 0.28),
            DefaultButtonTop = Mix(button, k.Accent, 0.12),
            DefaultButtonBottom = Mix(button, k.Accent, 0.2),
            DefaultButtonFrame = k.Accent,
            DefaultButtonText = text,
            ScrollTrack = Mix(window, text, 0.05),
            ScrollThumb = Mix(window, text, 0.3),
        };
    }

    /// <summary>Plasma 6's Breeze Light or Breeze Dark, optionally with another accent.</summary>
    public static UiPalette Breeze(bool dark, Color? accent = null)
    {
        var colors = dark ? KdeColors.BreezeDark : KdeColors.BreezeLight;
        return FromKde(accent is { } a ? colors with { Accent = a } : colors);
    }

    /// <summary><paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * amount),
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;


    private static Color Rgb(uint rgb) => Color.FromUInt32(0xFF000000 | rgb);
}
