using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Worklapse.App.Controls;

/// <summary>
/// A grid inside a <see cref="ScrollViewer"/> whose star rows fill the scroll viewer's height, like a grid outside one,
/// until its other rows and the star rows' MinHeight no longer fit; then it scrolls instead of its rows overlapping.
/// </summary>
/// <remarks>A plain Grid in a ScrollViewer is measured with an infinite height, so its star rows grow to their content.</remarks>
public class ViewportGrid : Grid
{
    private ScrollViewer? _scrollViewer;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollViewer is not null)
            _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scrollViewer is not null)
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        _scrollViewer = null;
    }

    // The scroll viewer measures this with the same infinite height whatever its own height, so it has to be told.
    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty
            && e.GetOldValue<Size>().Height != e.GetNewValue<Size>().Height)
            InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Measured as if as tall as the viewport (0 before the first layout: the smallest the rows fit in), it asks for
        // more than the viewport only when the rows need more, and the scroll viewer then scrolls.
        if (double.IsInfinity(availableSize.Height) && _scrollViewer is not null)
            availableSize = availableSize.WithHeight(_scrollViewer.Viewport.Height);
        return base.MeasureOverride(availableSize);
    }
}
