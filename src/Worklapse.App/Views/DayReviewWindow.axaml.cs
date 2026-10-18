using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Worklapse.App.Controls;
using Worklapse.App.Playback;
using Worklapse.App.ViewModels;
using Worklapse.Core.History;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.App.Views;

public partial class DayReviewWindow : Window
{
    private readonly DayReviewViewModel _viewModel;
    private readonly FrameRenderer _renderer = null!;
    private readonly Dictionary<long, WriteableBitmap> _bitmaps = [];
    private int _requestVersion;
    private PlannedFrame? _plan;
    private FrameRef? _reloadedFor;
    private bool _closed;

    // Needed by the XAML loader; the app always uses the other constructor.
    public DayReviewWindow() : this(null!, null!) { }

    public DayReviewWindow(DayReviewViewModel viewModel, string dataFolder)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (viewModel is null)
            return; // designer

        Timeline.ProductiveBrush = DayReviewViewModel.ProductiveBrush;
        Timeline.Clicked += viewModel.Seek;
        Timeline.Dragged += viewModel.DragSelect;
        Frames.ZoomChanged += UpdateViewText;

        // Right-click: mark what the frame shows (its site and app) productive or not; on the timeline, what
        // the selection contains (or the frame's, without a selection).
        Frames.ContextMenu = ClassificationMenu(forSelection: false);
        Timeline.ContextMenu = ClassificationMenu(forSelection: true);

        _renderer = new FrameRenderer(dataFolder);
        _renderer.Rendered += result => Dispatcher.UIThread.Post(() => ShowFrames(result));

        viewModel.TimelineChanged += UpdateTimeline;
        viewModel.FrameChanged += RequestFrames;

        // Arrow keys step through frames wherever focus is (toolbar buttons would otherwise move focus).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Activated += (_, _) => viewModel.RefreshCommand.Execute(null);

        UpdateTimeline();
        RequestFrames();
        UpdateViewText();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_viewModel is null)
            return;
        _closed = true;
        _viewModel.TimelineChanged -= UpdateTimeline;
        _viewModel.FrameChanged -= RequestFrames;
        _renderer.Dispose();
        _viewModel.Dispose();

        // The bitmaps hold a full-size copy of each monitor; don't leave them to the finalizer.
        _plan = null;
        Frames.SetContent([], null);
        foreach (var bitmap in _bitmaps.Values)
            bitmap.Dispose();
        _bitmaps.Clear();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Leave keys alone while a drop-down list is open.
        if (e.Source is ComboBox { IsDropDownOpen: true } || e.Source is ComboBoxItem)
            return;

        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                _viewModel.Step(e.Key == Key.Right ? 1 : -1, extendSelection: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.ClearSelection();
                e.Handled = true;
                break;
        }
    }

    /// <summary>A menu filled when it opens, from the capture or selection at that moment; empty means none.</summary>
    private ContextMenu ClassificationMenu(bool forSelection)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            var (choices, more) = _viewModel.ClassificationChoices(forSelection && _viewModel.HasSelection);
            if (choices.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            var items = new List<Control>();
            foreach (var choice in choices)
            {
                var item = new MenuItem { Header = choice.Label };
                item.Click += (_, _) => _viewModel.SetProductive(choice.Program, choice.MakeProductive);
                items.Add(item);
            }
            if (more > 0)
            {
                items.Add(new Separator());
                items.Add(new MenuItem { Header = $"{more} more in Settings → Programs", IsEnabled = false });
            }
            menu.ItemsSource = items;
        };
        return menu;
    }

    private void UpdateTimeline()
    {
        Timeline.SetData(_viewModel.Spans, _viewModel.View.Start, _viewModel.View.End);
        Timeline.SetMarkers(_viewModel.SelectionSeconds, _viewModel.PlayheadSeconds);
    }

    /// <summary>What to show for the current capture: which frame per monitor, and any overlay.</summary>
    private sealed record PlannedFrame(int Version, IReadOnlyList<(MonitorRecord Monitor, FrameRef? Frame, string? Label)> Tiles, string? Overlay);

    private void RequestFrames()
    {
        var captures = _viewModel.Captures;
        var index = _viewModel.Current;
        // Only the monitors attached then: those of other times can be in the same place.
        var monitors = _viewModel.SelectedMonitor?.MonitorId is { } only
            ? _viewModel.CurrentMonitors.Where(m => m.Id == only).ToList()
            : _viewModel.CurrentMonitors;
        if (index < 0 || index >= captures.Count || monitors.Count == 0)
        {
            _plan = null;
            Frames.SetContent([], captures.Count == 0 ? "No recordings this day" : null);
            return;
        }

        var capture = captures[index];
        string? overlay = capture.PendingDelete ? "Marked for deletion" : !capture.IsActive ? "Idle — no keyboard or mouse input" : null;

        var tiles = new List<(MonitorRecord, FrameRef?, string?)>();
        foreach (var monitor in monitors)
        {
            var frame = FrameOf(capture, monitor.Id);
            // Idle captures have no frames; show the screen as it was last captured, under the idle overlay.
            if (frame is null && !capture.IsActive)
                frame = LastFrameBefore(index, monitor.Id);
            tiles.Add((monitor, frame, frame is null ? "Not recorded" : null));
        }
        UpdateViewText();

        var version = ++_requestVersion;
        _plan = new PlannedFrame(version, tiles, overlay);
        _renderer.Request(new FrameRequest(version, tiles.Where(t => t.Item2 is not null).Select(t => (t.Item1.Id, t.Item2!.Value)).ToList()));
        // Until the new frames are decoded, keep showing the current pictures with the new labels.
        ApplyPlan(null);
    }

    private static FrameRef? FrameOf(CaptureRecord capture, long monitorId)
    {
        foreach (var f in capture.Frames)
            if (f.MonitorId == monitorId)
                return f.Frame;
        return null;
    }

    private FrameRef? LastFrameBefore(int index, long monitorId)
    {
        var captures = _viewModel.Captures;
        for (var i = index - 1; i >= 0; i--)
            if (FrameOf(captures[i], monitorId) is { } frame)
                return frame;
        return null;
    }

    private void ShowFrames(FrameResult result)
    {
        try
        {
            if (_closed || _plan is null || result.Version != _plan.Version)
                return; // superseded by a newer request

            // Reclaiming the space of deleted captures moves frames to rebuilt segments, so the day loaded here
            // can refer to frames that are gone. Reload it and ask again, once per missing frame: if the
            // reloaded day still refers to it, the error is shown.
            if (result.Error is FrameUnavailableException missing && _reloadedFor != missing.Frame)
            {
                _reloadedFor = missing.Frame;
                _viewModel.RefreshCommand.Execute(null);
                return;
            }

            foreach (var (monitorId, frame) in result.Frames)
                CopyToBitmap(monitorId, frame);
            ApplyPlan(result.Error);
        }
        finally
        {
            result.Release();
        }
    }

    private void ApplyPlan(Exception? error)
    {
        if (_plan is null)
            return;
        var tiles = _plan.Tiles.Select(t => new FrameTile(
            t.Monitor.Bounds,
            t.Frame is not null && _bitmaps.TryGetValue(t.Monitor.Id, out var bitmap) ? bitmap : null,
            t.Label)).ToList();
        var overlay = error is not null ? "Couldn't read this frame: " + error.Message : _plan.Overlay;
        Frames.SetContent(tiles, overlay);
        UpdateViewText();

        // Each bitmap is a full-size copy of a monitor; drop those of monitors not shown now (unplugged, or
        // another monitor chosen).
        foreach (var id in _bitmaps.Keys.Where(id => _plan.Tiles.All(t => t.Monitor.Id != id)).ToList())
        {
            _bitmaps.Remove(id, out var unused);
            unused!.Dispose();
        }
    }

    private void CopyToBitmap(long monitorId, DecodedFrame frame)
    {
        if (!_bitmaps.TryGetValue(monitorId, out var bitmap) || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
        {
            bitmap?.Dispose();
            _bitmaps[monitorId] = bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        // Converted straight into the bitmap: no full-size BGRA copy of the frame exists anywhere else.
        using var buffer = bitmap.Lock();
        frame.CopyBgra(buffer.Address, buffer.RowBytes);
    }

    private void UpdateViewText()
    {
        // 100% = the recording's actual pixels.
        var percent = $"{Frames.Magnification * 100:0}%";
        ZoomText.Text = percent;
        var monitor = _viewModel?.SelectedMonitor?.Label ?? "All monitors";
        ViewText.Text = $"{monitor} · {percent}{(Frames.Zoom == 1 ? " (fit)" : "")}";
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateViewText();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Frames.ZoomBy(1.5);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Frames.ZoomBy(1 / 1.5);

    private void OnFit(object? sender, RoutedEventArgs e) => Frames.Fit();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
