using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Worklapse.App.ViewModels;
using Worklapse.Core.Projects;

namespace Worklapse.App.Views;

/// <summary>
/// Picks a colour off the palette. The colour is applied when the picker closes (Escape puts it back), not while
/// it's dragged: <see cref="ProjectColors"/> keeps a brush for every colour it's given.
/// </summary>
public partial class ColorField : UserControl
{
    public static readonly StyledProperty<int?> RgbProperty =
        AvaloniaProperty.Register<ColorField, int?>(nameof(Rgb), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The colour as 0xRRGGBB; null for none (a project using its category's colour).</summary>
    public int? Rgb
    {
        get => GetValue(RgbProperty);
        set => SetValue(RgbProperty, value);
    }

    // Set by Escape, so closing leaves the colour as it was.
    private bool _cancelled;

    // While the hex field sets the colour, so the field isn't rewritten under the typing.
    private bool _settingFromHex;

    public ColorField()
    {
        InitializeComponent();
        // Tunnelling, so Escape is seen before the flyout closes itself on it.
        PickerPanel.AddHandler(KeyDownEvent, OnPickerKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RgbProperty)
            ShowCustomSwatch();
    }

    // The palette marks its own colours as chosen; the button marks the rest.
    private void ShowCustomSwatch()
    {
        var custom = Rgb is { } rgb && PaletteColor.Find(rgb) is null;
        CustomSwatch.IsVisible = custom;
        CustomSwatch.Fill = custom ? ProjectColors.Brush(Rgb!.Value) : null;
    }

    private void OnPickerOpening(object? sender, EventArgs e)
    {
        _cancelled = false;
        Spectrum.Color = ToColor(Rgb ?? ProjectStore.DefaultColor);
        // Setting the colour it already has raises no change.
        ShowColor(Spectrum.Color);
    }

    private void OnPickerClosed(object? sender, EventArgs e)
    {
        if (!_cancelled)
            Rgb = ToRgb(Spectrum.Color);
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter))
            return;
        _cancelled = e.Key == Key.Escape;
        e.Handled = true;
        CustomButton.Flyout?.Hide();
    }

    private void OnSpectrumColorChanged(object? sender, ColorChangedEventArgs e) => ShowColor(e.NewColor);

    private void ShowColor(Color color)
    {
        Preview.Fill = new ImmutableSolidColorBrush(color);
        if (!_settingFromHex)
            HexBox.Text = ToHex(color);
    }

    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (!TryParseHex(HexBox.Text, out var rgb) || rgb == ToRgb(Spectrum.Color))
            return;
        _settingFromHex = true;
        Spectrum.Color = ToColor(rgb);
        _settingFromHex = false;
    }

    // Whatever was left half-typed goes back to the colour shown.
    private void OnHexLostFocus(object? sender, RoutedEventArgs e) => HexBox.Text = ToHex(Spectrum.Color);

    private static bool TryParseHex(string? text, out int rgb)
    {
        var digits = text?.Trim().TrimStart('#') ?? "";
        rgb = 0;
        return digits.Length == 6 && int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static int ToRgb(Color color) => (color.R << 16) | (color.G << 8) | color.B;
}
