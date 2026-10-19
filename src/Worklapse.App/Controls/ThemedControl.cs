using Avalonia;
using Avalonia.Controls;
using Worklapse.App.Theming;

namespace Worklapse.App.Controls;

/// <summary>
/// A custom-drawn control that draws with <see cref="AppBrushes"/>: those are recoloured in place when the OS theme
/// changes, so it only needs drawing again, while it's on screen.
/// </summary>
public abstract class ThemedControl : Control
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.Changed -= InvalidateVisual;
    }
}
