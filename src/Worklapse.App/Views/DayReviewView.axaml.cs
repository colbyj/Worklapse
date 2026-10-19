using System.Collections.ObjectModel;
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
using PixelRect = Worklapse.Core.Platform.PixelRect;

namespace Worklapse.App.Views;

/// <summary>
/// Day review inside the main window: the day's timeline, the picture of the current capture, and playback. The
/// main window forwards keys to it while it's shown, and calls <see cref="Close"/> when letting it go.
/// </summary>
public partial class DayReviewView : UserControl
{
    private readonly DayReviewViewModel _viewModel;
    private readonly FrameRenderer _renderer = null!;
    private readonly Dictionary<long, WriteableBitmap> _bitmaps = [];
    private int _requestVersion;
    private PlannedFrame? _plan;
    private (IReadOnlyList<FrameTile> Tiles, PixelRect? Focus) _shown = ([], null);
    private bool _updatingBars;
    private FrameRef? _reloadedFor;
    private bool _closed;
    private (double X, double Width)? _resizingFrom;   // where a drag of the panel's edge started

    // Needed by the XAML loader; the app always uses the other constructor.
    public DayReviewView() : this(null!, null!, null!) { }

    public DayReviewView(DayReviewViewModel viewModel, string dataFolder, RecordingViewModel recording)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        if (viewModel is null)
            return; // designer
        Messages.DataContext = recording;
        Indicator.DataContext = recording;

        Timeline.Clicked += viewModel.Click;
        Timeline.Dragged += viewModel.DragSelect;
        Timeline.EdgeDragged += viewModel.DragEdge;
        Timeline.DoubleClicked += viewModel.DoubleClick;
        Frames.ViewChanged += UpdateView;
        HorizontalBar.ValueChanged += (_, _) => OnBarScrolled();
        VerticalBar.ValueChanged += (_, _) => OnBarScrolled();
        FillWhenOpening((MenuFlyout)MonitorButton.Flyout!, MonitorMenuItems);
        FillWhenOpening((MenuFlyout)SetProjectButton.Flyout!, () => ProjectMenuItems(viewModel.SetSelectionProjectCommand.Execute));

        // Right-click: mark what the frame shows (its site and app) productive or not; on the timeline, set the
        // selection's project or mark it for deletion, and mark what it contains (or the frame's, without one).
        Frames.ContextMenu = ClassificationMenu(forSelection: false);
        Timeline.ContextMenu = ClassificationMenu(forSelection: true);

        _renderer = new FrameRenderer(dataFolder);
        _renderer.Rendered += result => Dispatcher.UIThread.Post(() => ShowFrames(result));

        viewModel.TimelineChanged += UpdateTimeline;
        viewModel.FrameChanged += RequestFrames;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DayReviewViewModel.SelectedMonitor) or nameof(DayReviewViewModel.FocusOnApplication))
                UpdateView();
        };

        UpdateTimeline();
        RequestFrames();
        UpdateView();
    }

    /// <summary>Lets go of the renderer and the pictures; the view model is the main window's to dispose.</summary>
    public void Close()
    {
        if (_viewModel is null || _closed)
            return;
        _closed = true;
        _viewModel.TimelineChanged -= UpdateTimeline;
        _viewModel.FrameChanged -= RequestFrames;
        _renderer.Dispose();

        // The bitmaps hold a full-size copy of each monitor; don't leave them to the finalizer.
        _plan = null;
        _shown = ([], null);
        Frames.SetContent([], null);
        foreach (var bitmap in _bitmaps.Values)
            bitmap.Dispose();
        _bitmaps.Clear();
    }

    /// <summary>F5 refreshes; arrow keys step through frames wherever the focus is, except in text and open lists.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Source is ComboBox { IsDropDownOpen: true } or ComboBoxItem or TextBox)
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

    /// <summary>
    /// Fills a menu flyout from the state at the moment it opens. The flyout keeps one collection for good: its popup
    /// takes the collection when it's made, before Opening is raised, so a collection handed over in Opening would
    /// never show (Avalonia 12) — the menu came up empty.
    /// </summary>
    private static void FillWhenOpening(MenuFlyout flyout, Func<List<Control>> items)
    {
        var shown = new ObservableCollection<Control>();
        flyout.ItemsSource = shown;
        flyout.Opening += (_, _) =>
        {
            shown.Clear();
            foreach (var item in items())
                shown.Add(item);
        };
    }

    /// <summary>The monitors to choose between, then whether to show only the window that had the focus.</summary>
    private List<Control> MonitorMenuItems()
    {
        var items = new List<Control>();
        foreach (var choice in _viewModel.MonitorChoices)
        {
            var item = new MenuItem
            {
                Header = choice.Label,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "monitor",
                IsChecked = Equals(choice, _viewModel.SelectedMonitor),
            };
            item.Click += (_, _) => _viewModel.SelectedMonitor = choice;
            items.Add(item);
        }
        items.Add(new Separator());
        var focus = new MenuItem
        {
            Header = "Focus on Active App",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _viewModel.FocusOnApplication,
        };
        ToolTip.SetTip(focus, "Show only the window that had focus, centred, without the monitors around it");
        focus.Click += (_, _) => _viewModel.FocusOnApplication = !_viewModel.FocusOnApplication;
        items.Add(focus);
        return items;
    }

    /// <summary>
    /// The projects to set some time to, each in its colour; for the Set Project button and the timeline's and the
    /// rows' right-click menus.
    /// </summary>
    private List<Control> ProjectMenuItems(Action<long> choose)
    {
        var items = new List<Control>();
        foreach (var project in _viewModel.AssignableProjects)
        {
            var item = new MenuItem
            {
                Header = project.Category.Length > 0 ? $"{project.Name}  ({project.Category})" : project.Name,
                Icon = new Avalonia.Controls.Shapes.Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = project.Brush },
            };
            var id = project.Id!.Value;
            item.Click += (_, _) => choose(id);
            items.Add(item);
        }
        if (items.Count == 0)
            items.Add(new MenuItem { Header = "No projects yet: add them in Settings → Projects", IsEnabled = false });
        return items;
    }

    /// <summary>A menu filled when it opens, from the capture or selection at that moment; empty means none.</summary>
    private ContextMenu ClassificationMenu(bool forSelection)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            var (choices, more) = _viewModel.ClassificationChoices(forSelection && _viewModel.HasSelection);
            var items = new List<Control>();
            if (forSelection && _viewModel.HasSelection)
            {
                items.Add(new MenuItem { Header = "Set Project", ItemsSource = ProjectMenuItems(_viewModel.SetSelectionProjectCommand.Execute) });
                items.Add(new MenuItem { Header = "Clear Project", Command = _viewModel.ClearSelectionProjectCommand });
                items.Add(new MenuItem { Header = "Mark for Deletion", Command = _viewModel.MarkSelectionCommand });
                if (choices.Count > 0)
                    items.Add(new Separator());
            }
            if (items.Count == 0 && choices.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            foreach (var choice in choices)
            {
                var item = new MenuItem { Header = choice.Label };
                item.Click += (_, _) => _viewModel.SetProductive(choice.Program, choice.MakeProductive);
                items.Add(item);
            }
            if (more > 0)
            {
                items.Add(new Separator());
                items.Add(new MenuItem { Header = $"{more} more in Settings → Applications", IsEnabled = false });
            }
            menu.ItemsSource = items;
        };
        return menu;
    }

    private void UpdateTimeline()
    {
        Timeline.SetData(_viewModel.Spans, _viewModel.View.Start, _viewModel.View.End);
        Timeline.SetProjects(_viewModel.ProjectLane);
        Timeline.SetMarkers(_viewModel.SelectionSeconds, _viewModel.PlayheadSeconds);
        Timeline.SetReach(_viewModel.ReachSeconds);
    }

    // --- the side panel ---------------------------------------------------------------------------

    // Pointing at a row's mark (or tabbing to it) shows on the timeline the time it decides; the control's Tag says
    // which mark ("productive", "project"), or "row" for the whole row.
    private void OnReachEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
            _viewModel.ShowReach(ReachSource.Pointer, control.DataContext, control.Tag as string);
    }

    private void OnReachExit(object? sender, PointerEventArgs e)
    {
        // Choosing in its list moves the pointer off the combo box; it's still the one being changed.
        if (sender is ComboBox { IsDropDownOpen: true })
            return;
        _viewModel.ShowReach(ReachSource.Pointer, null, null);
    }

    private void OnReachDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox { IsPointerOver: false })
            _viewModel.ShowReach(ReachSource.Pointer, null, null);
    }

    private void OnReachFocus(object? sender, FocusChangedEventArgs e)
    {
        // Only from the keyboard: a click focuses too, and its highlight would outstay the pointer.
        if (sender is Control control && e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
            _viewModel.ShowReach(ReachSource.Keyboard, control.DataContext, control.Tag as string);
    }

    private void OnReachBlur(object? sender, RoutedEventArgs e) => _viewModel.ShowReach(ReachSource.Keyboard, null, null);

    /// <summary>A segment on the day's list clicked: selects its time; Shift or Ctrl extends from the row picked before.</summary>
    private void OnSegmentPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SegmentRow row } control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed
            || !_viewModel.DaySegments.Contains(row))
            return;
        _viewModel.Pick(row.From, row.To, Extends(e));
    }

    private void OnMarkedPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MarkedRange marked } control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            _viewModel.Pick(marked.Start, marked.End, Extends(e));
    }

    private static bool Extends(PointerPressedEventArgs e) =>
        e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control);

    /// <summary>
    /// A segment's right-click menu, made for the row when asked for: changing the selection's scope, then what to do
    /// with the selection (when the row is inside it) or the row; what it acts on shows on the timeline meanwhile.
    /// </summary>
    private void OnSegmentContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SegmentRow row } control)
            return;
        e.Handled = true;
        var target = _viewModel.MenuTarget(row);
        var items = new List<Control>
        {
            MenuEntry("Select", () => _viewModel.Pick(row.From, row.To, extend: false)),
            MenuEntry("Extend Selection to Here", () => _viewModel.ExtendSelectionTo(row), _viewModel.CanExtendSelectionTo(row)),
            MenuEntry("Start Selection Here", () => _viewModel.StartSelectionAt(row), _viewModel.CanStartSelectionAt(row)),
            MenuEntry("End Selection Here", () => _viewModel.EndSelectionAt(row), _viewModel.CanEndSelectionAt(row)),
            new Separator(),
            new MenuItem { Header = "Set Project", ItemsSource = ProjectMenuItems(id => _viewModel.SetProject(target, id)) },
            MenuEntry("Clear Project", () => _viewModel.SetProject(target, null)),
            MenuEntry("Mark for Deletion", () => _viewModel.MarkForDeletion(target)),
            new Separator(),
        };
        if (row.Program is { } program)
            items.Add(MenuEntry($"Mark {program.Name} as {(row.Productive ? "Other" : "Productive")}",
                () => _viewModel.SetProductive(program, !row.Productive)));
        items.Add(MenuEntry("Go To", () => _viewModel.GoTo(row.From)));
        OpenRowMenu(control, items, [target]);
    }

    /// <summary>
    /// An app's or site's right-click menu on the Applications tab: what to do with all its time there (the day's, or
    /// the selection's on the selection's list), which shows on the timeline meanwhile.
    /// </summary>
    private void OnAppContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: DayAppRow { Program: { } program } row } control)
            return;
        e.Handled = true;
        var time = _viewModel.AppTime(row);
        if (time.Count == 0)
            return;
        List<Control> items =
        [
            new MenuItem { Header = "Set Project", ItemsSource = ProjectMenuItems(id => _viewModel.SetProject(time, id)) },
            MenuEntry("Clear Project", () => _viewModel.SetProject(time, null)),
            MenuEntry("Mark for Deletion", () => _viewModel.MarkForDeletion(time)),
            new Separator(),
            MenuEntry($"Mark {program.Name} as {(row.Productive ? "Other" : "Productive")}", () => _viewModel.SetProductive(program, !row.Productive)),
            MenuEntry("Go To", () => _viewModel.GoTo(time[0].Start)),
        ];
        OpenRowMenu(control, items, time);
    }

    /// <summary>Opens a row's menu, showing on the timeline what it acts on until it closes.</summary>
    private void OpenRowMenu(Control row, List<Control> items, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> actsOn)
    {
        var menu = new ContextMenu { ItemsSource = items };
        menu.Closed += (_, _) => _viewModel.ShowReach(ReachSource.Menu, ranges: null);
        _viewModel.ShowReach(ReachSource.Menu, actsOn);
        menu.Open(row);
    }

    private static MenuItem MenuEntry(string header, Action run, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    // The panel's left edge, dragged to resize it; the width is remembered when the drag ends.
    private void OnPanelEdgePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _resizingFrom = (e.GetPosition(this).X, _viewModel.PanelWidth);
        e.Pointer.Capture(PanelEdge);
        e.Handled = true;
    }

    private void OnPanelEdgeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingFrom is { } from)
            _viewModel.ResizePanel(from.Width - (e.GetPosition(this).X - from.X));
    }

    private void OnPanelEdgeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingFrom is null)
            return;
        _resizingFrom = null;
        e.Pointer.Capture(null);
        _viewModel.SavePanelWidth();
    }

    /// <summary>
    /// What to show for the current capture: which frame per monitor, any overlay, and the window to focus on
    /// (null: the whole arrangement).
    /// </summary>
    private sealed record PlannedFrame(int Version, IReadOnlyList<(MonitorRecord Monitor, FrameRef? Frame, string? Label)> Tiles,
        string? Overlay, PixelRect? Focus);

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
            _shown = ([], null);
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

        // The focused window, where it was recorded. Recording only the active application blacks out the rest of
        // its monitor and leaves the others out, so this shows just what was recorded.
        PixelRect? focus = null;
        if (_viewModel.FocusOnApplication && WindowAt(index) is { } window
            && tiles.Any(t => t.Item2 is not null && !t.Item1.Bounds.Intersect(window).IsEmpty))
            focus = window;

        var version = ++_requestVersion;
        _plan = new PlannedFrame(version, tiles, overlay, focus);
        _renderer.Request(new FrameRequest(version, tiles.Where(t => t.Item2 is not null).Select(t => (t.Item1.Id, t.Item2!.Value)).ToList()));
        // Until the new frames are decoded, keep showing the current pictures (as placed for them) with the new
        // overlay. The new placement applied to the old pictures would show the wrong part of them.
        Frames.SetContent(_shown.Tiles, overlay, _shown.Focus);
        UpdateView();
    }

    /// <summary>
    /// The window that had focus at a capture. Idle captures show the screen as last captured, so for them, the
    /// window of the last active capture before.
    /// </summary>
    private PixelRect? WindowAt(int index)
    {
        var captures = _viewModel.Captures;
        for (var i = index; i >= 0; i--)
            if (captures[i].IsActive)
                return captures[i].Window;
        return null;
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
        // Focused on a window, monitors with nothing recorded are left out: the window's part on them is empty.
        var tiles = _plan.Tiles
            .Where(t => _plan.Focus is null || t.Frame is not null)
            .Select(t => new FrameTile(
                t.Monitor.Bounds,
                t.Frame is not null && _bitmaps.TryGetValue(t.Monitor.Id, out var bitmap) ? bitmap : null,
                t.Label))
            .ToList();
        var overlay = error is not null ? "Couldn't read this frame: " + error.Message : _plan.Overlay;
        Frames.SetContent(tiles, overlay, _plan.Focus);
        _shown = (tiles, _plan.Focus);
        UpdateView();

        // Each bitmap is a full-size copy of a monitor; drop those of monitors not shown now (unplugged, another
        // monitor chosen, or not recorded at this capture).
        foreach (var id in _bitmaps.Keys.Where(id => _plan.Tiles.All(t => t.Monitor.Id != id || t.Frame is null)).ToList())
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

    /// <summary>Shows the magnification, and the scroll bars while the picture is larger than the view.</summary>
    /// <remarks>
    /// A bar shows itself while its maximum is above zero. Showing one only makes the view smaller, so this settles: a
    /// bar shown stays needed.
    /// </remarks>
    private void UpdateView()
    {
        // 100% = the recording's actual pixels.
        var percent = $"{Frames.Magnification * 100:0}%";
        ZoomText.Text = percent;
        var monitor = _viewModel?.SelectedMonitor?.Label ?? "All monitors";
        var focus = _viewModel?.FocusOnApplication == true ? " · Focused on application" : "";
        ViewText.Text = $"{monitor}{focus} · {percent}{(Frames.IsFitted ? " (fit)" : "")}";

        var max = Frames.MaxOffset;
        var offset = Frames.Offset;
        _updatingBars = true;
        try
        {
            UpdateBar(HorizontalBar, max.X, offset.X, Frames.Bounds.Width);
            UpdateBar(VerticalBar, max.Y, offset.Y, Frames.Bounds.Height);
        }
        finally
        {
            _updatingBars = false;
        }

        static void UpdateBar(Avalonia.Controls.Primitives.ScrollBar bar, double max, double value, double viewport)
        {
            bar.Maximum = max;
            bar.Value = value;
            bar.ViewportSize = viewport;
            bar.LargeChange = viewport * 0.9;
            bar.SmallChange = 40;
        }
    }

    private void OnBarScrolled()
    {
        if (!_updatingBars)
            Frames.ScrollTo(new Vector(HorizontalBar.Value, VerticalBar.Value));
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Frames.ZoomBy(1.5);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Frames.ZoomBy(1 / 1.5);

    private void OnFit(object? sender, RoutedEventArgs e) => Frames.Fit();
}
