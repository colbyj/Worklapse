using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Worklapse.App.Controls;
using Worklapse.App.Playback;
using Worklapse.App.ViewModels;
using Worklapse.Core.History;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;
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
    private bool _updatingTimelineBar;
    private DateOnly? _timelineDay;            // the day the timeline's zoom is for
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
        Timeline.ShownChanged += UpdateTimelineBar;
        TimelineBar.Margin = new Thickness(TimelineControl.Gutter, 0, 0, TimelineBar.Margin.Bottom);
        TimelineBar.ValueChanged += (_, _) =>
        {
            if (!_updatingTimelineBar)
                Timeline.ScrollTo(Timeline.ViewStart + TimelineBar.Value);
        };
        Frames.ViewChanged += UpdateView;
        HorizontalBar.ValueChanged += (_, _) => OnBarScrolled();
        VerticalBar.ValueChanged += (_, _) => OnBarScrolled();
        FillWhenOpening((MenuFlyout)MonitorButton.Flyout!, MonitorMenuItems);
        FillWhenOpening((MenuFlyout)SetProjectButton.Flyout!, SetProjectButtonItems);
        DayCalendar.AddHandler(PointerReleasedEvent, OnDayCalendarPointerReleased, handledEventsToo: true);
        DayCalendar.AddHandler(KeyDownEvent, OnDayCalendarKeyDown, RoutingStrategies.Tunnel);

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

    /// <summary>
    /// F5 refreshes; arrow keys step through frames wherever the focus is, except in text, open lists and the day
    /// calendar.
    /// </summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Source is ComboBox { IsDropDownOpen: true } or ComboBoxItem or TextBox
            || (e.Source as Visual)?.FindAncestorOfType<Calendar>(includeSelf: true) is not null)
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

    // --- going to a day -----------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the calendar at the day shown, from the first day recorded to the last, with the days in between that
    /// have no captures greyed out: the day arrows skip them too.
    /// </summary>
    private void OnDayFlyoutOpening(object? sender, EventArgs e)
    {
        var shown = _viewModel.Day.Day;
        var recorded = _viewModel.Days.ToHashSet();
        recorded.Add(shown);
        var first = recorded.Min();
        var last = recorded.Max();

        // The calendar throws if its selected day falls outside its range or on a greyed-out day, so it has none
        // while those change.
        DayCalendar.SelectedDate = null;
        DayCalendar.BlackoutDates.Clear();
        DayCalendar.DisplayDateStart = null;
        DayCalendar.DisplayDateEnd = null;
        DayCalendar.DisplayDateStart = ToDateTime(first);
        DayCalendar.DisplayDateEnd = ToDateTime(last);
        DateOnly? gapStart = null;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (!recorded.Contains(day))
                gapStart ??= day;
            else if (gapStart is { } start)
            {
                DayCalendar.BlackoutDates.Add(new CalendarDateRange(ToDateTime(start), ToDateTime(day.AddDays(-1))));
                gapStart = null;
            }
        }
        DayCalendar.DisplayMode = CalendarMode.Month;
        DayCalendar.DisplayDate = ToDateTime(shown);
        DayCalendar.SelectedDate = ToDateTime(shown);
    }

    // So the arrow keys move around the calendar straight away.
    private void OnDayFlyoutOpened(object? sender, EventArgs e) => DayCalendar.Focus();

    // A click on a day goes to it (the calendar has selected it by then). The arrow keys only move the selection.
    private void OnDayCalendarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<CalendarDayButton>(includeSelf: true) is { IsEnabled: true } dayButton
            && !dayButton.Classes.Contains(":blackout"))
            GoToCalendarDay();
    }

    // Enter goes to the selected day. Seen before the calendar's own handling, which turns Enter on a month (in the
    // year view) into showing that month.
    private void OnDayCalendarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DayCalendar.DisplayMode == CalendarMode.Month)
        {
            GoToCalendarDay();
            e.Handled = true;
        }
    }

    private void GoToCalendarDay()
    {
        DayButton.Flyout!.Hide();
        if (DayCalendar.SelectedDate is { } picked && DateOnly.FromDateTime(picked) != _viewModel.Day.Day)
            _viewModel.GoToDay(DateOnly.FromDateTime(picked));
    }

    private static DateTime ToDateTime(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);

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
    /// <param name="except">A project left out (the one being replaced).</param>
    private List<Control> ProjectMenuItems(Action<long> choose, long? except = null)
    {
        var items = new List<Control>();
        foreach (var project in _viewModel.AssignableProjects.Where(p => p.Id != except))
        {
            var item = new MenuItem
            {
                Header = project.Category.Length > 0 ? $"{project.Name}  ({project.Category})" : project.Name,
                Icon = ProjectSwatch(project.Brush),
            };
            var id = project.Id!.Value;
            item.Click += (_, _) => choose(id);
            items.Add(item);
        }
        if (items.Count == 0)
            items.Add(new MenuItem { Header = "No projects yet: add them in Settings → Projects", IsEnabled = false });
        return items;
    }

    /// <summary>A project's colour, as a menu entry's icon.</summary>
    private static Avalonia.Controls.Shapes.Rectangle ProjectSwatch(Avalonia.Media.IBrush brush) =>
        new() { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = brush };

    /// <summary>A menu filled when it opens, from the capture or selection at that moment; empty means none.</summary>
    private ContextMenu ClassificationMenu(bool forSelection)
    {
        var menu = new ContextMenu();
        menu.Closed += (_, _) => _viewModel.ShowReach(ReachSource.Menu, ranges: null);
        menu.Opening += (_, e) =>
        {
            var (choices, more) = _viewModel.ClassificationChoices(forSelection && _viewModel.HasSelection);
            var items = new List<Control>();
            // A suggestion right-clicked on the projects lane: decide it there, as on the Suggestions tab.
            if (forSelection && Timeline.ContextPoint is { ProjectLane: true } point && _viewModel.SuggestionAt(point.Seconds) is { } found)
            {
                items.AddRange(SuggestionMenuItems(found.Suggestion, found.Part, found.Piece));
                _viewModel.ShowReach(ReachSource.Menu, (found.Suggestion.Start, found.Suggestion.End));
            }
            if (forSelection && _viewModel.HasSelection)
            {
                items.Add(new MenuItem { Header = "Set Project", ItemsSource = ProjectMenuItems(_viewModel.SetSelectionProjectCommand.Execute) });
                items.Add(SetUnassignedItem(SelectionRanges()));
                items.Add(ReplaceProjectItem(SelectionRanges()));
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

    /// <summary>
    /// A suggestion's entries on the timeline's menu: accept it (whole, or just the piece clicked on when it's drawn in
    /// several), accept the piece as another project, give the suggestion another project, reject it, and decide the
    /// break or switch clicked on.
    /// </summary>
    private List<Control> SuggestionMenuItems(StoredSuggestion suggestion, StoredPart? part, SuggestionPiece? piece)
    {
        var project = _viewModel.ProjectName(suggestion.ProjectId);
        var items = new List<Control>();
        var several = _viewModel.PieceCount(suggestion) > 1;
        if (several)
        {
            items.Add(MenuEntry($"Accept Whole Suggestion: {project}, {DayReviewViewModel.RangeText(suggestion.Start, suggestion.End)}",
                () => _viewModel.AcceptSuggestion(suggestion)));
            if (piece is { CanAccept: true } one)
                items.Add(MenuEntry($"Accept This Piece Only: {_viewModel.ProjectName(one.ProjectId)}, {DayReviewViewModel.RangeText(one.Start, one.End)}",
                    () => _viewModel.AcceptPiece(one)));
        }
        else
            items.Add(MenuEntry($"Accept Suggestion: {project}", () => _viewModel.AcceptSuggestion(suggestion)));
        // Any piece, a break too: its time set to the project picked, the rest of the suggestion left open.
        if (piece is { } clicked)
            items.Add(new MenuItem
            {
                Header = several ? "Accept This Piece as" : "Accept as",
                ItemsSource = ProjectMenuItems(id => _viewModel.AcceptPiece(clicked with { ProjectId = id })),
            });
        items.Add(new MenuItem
        {
            Header = "Change Suggestion to",
            ItemsSource = ProjectMenuItems(id => _viewModel.ChangeSuggestionProject(suggestion, id)),
        });
        items.Add(MenuEntry("Reject Suggestion", () => _viewModel.RejectSuggestion(suggestion)));
        if (part is { } p)
        {
            var header = p.Kind == SuggestionPartKind.Switch && p.ProjectId is { } other
                ? p.Decided ? $"Split Out as {_viewModel.ProjectName(other)}" : $"Keep in {project}"
                : p.Decided ? "Don't Count This Break" : $"Count This Break toward {project}";
            items.Add(MenuEntry(header, () => _viewModel.DecidePart(suggestion, p, !p.Decided)));
        }
        items.Add(new Separator());
        return items;
    }

    private void UpdateTimeline()
    {
        Timeline.SetData(_viewModel.Spans, _viewModel.View.Start, _viewModel.View.End);
        // Another day starts zoomed out; the same day read again keeps its zoom.
        if (_viewModel.Day.Day != _timelineDay)
        {
            _timelineDay = _viewModel.Day.Day;
            Timeline.ShowAll();
        }
        Timeline.SetProjects(_viewModel.ProjectLane);
        Timeline.SetMarkers(_viewModel.SelectionSeconds, _viewModel.PlayheadSeconds);
        Timeline.SetReach(_viewModel.ReachSeconds);
        UpdateTimelineBar();
    }

    /// <summary>The timeline's scroll bar, in seconds from the start of the time it covers; full while zoomed out.</summary>
    private void UpdateTimelineBar()
    {
        var shown = Timeline.ShownEnd - Timeline.ShownStart;
        _updatingTimelineBar = true;
        try
        {
            TimelineBar.Maximum = Timeline.ViewEnd - Timeline.ViewStart - shown;
            TimelineBar.Value = Timeline.ShownStart - Timeline.ViewStart;
            TimelineBar.ViewportSize = shown;
            TimelineBar.SmallChange = shown / 10;
            TimelineBar.LargeChange = shown * 0.9;
        }
        finally
        {
            _updatingTimelineBar = false;
        }
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
            SetUnassignedItem([target]),
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
            SetUnassignedItem(time),
            MenuEntry("Clear Project", () => _viewModel.SetProject(time, null)),
            MenuEntry("Mark for Deletion", () => _viewModel.MarkForDeletion(time)),
            new Separator(),
            MenuEntry($"Mark {program.Name} as {(row.Productive ? "Other" : "Productive")}", () => _viewModel.SetProductive(program, !row.Productive)),
            MenuEntry("Go To", () => _viewModel.GoTo(time[0].Start)),
        ];
        OpenRowMenu(control, items, time);
    }

    /// <summary>The Set Project button's menu: the projects to set the selection to, then the same for what has none.</summary>
    private List<Control> SetProjectButtonItems()
    {
        var items = ProjectMenuItems(_viewModel.SetSelectionProjectCommand.Execute);
        items.Add(new Separator());
        items.Add(SetUnassignedItem(SelectionRanges()));
        return items;
    }

    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> SelectionRanges() =>
        _viewModel.SelectionRange is { } range ? [range] : [];

    /// <summary>"Set Unassigned to ▸": the projects to set what has none in some time to; off when all of it has one.</summary>
    private MenuItem SetUnassignedItem(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges) => new()
    {
        Header = "Set Unassigned to",
        ItemsSource = ProjectMenuItems(id => _viewModel.SetUnassigned(ranges, id)),
        IsEnabled = _viewModel.HasUnassigned(ranges),
    };

    /// <summary>
    /// "Replace Project ▸ (a project with time there) ▸ (the one to give it)": only that project's time changes. With one
    /// project there, "Replace Worklapse with ▸" straight away; off when there's none.
    /// </summary>
    private MenuItem ReplaceProjectItem(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges)
    {
        var present = _viewModel.ProjectsIn(ranges);
        List<Control> Targets(ProjectOption from) =>
            ProjectMenuItems(to => _viewModel.ReplaceProject(ranges, from.Id!.Value, to), except: from.Id);
        if (present.Count == 1)
            return new MenuItem { Header = $"Replace {present[0].Name} with", ItemsSource = Targets(present[0]) };
        return new MenuItem
        {
            Header = "Replace Project",
            IsEnabled = present.Count > 0,
            ItemsSource = present.Select(from => new MenuItem
            {
                Header = from.Name,
                Icon = ProjectSwatch(from.Brush),
                ItemsSource = Targets(from),
            }).ToList(),
        };
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
            // TimeSnapper kept no picture for windows it couldn't name; the time was recorded all the same.
            tiles.Add((monitor, frame, frame is null ? monitor.IsTimeSnapper ? "No picture" : "Not recorded" : null));
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
        // Focused on a window, monitors with nothing recorded are left out: the window's part on them is empty. A
        // frame fills its monitor's place (on a scaled desktop it has more pixels than the place), except a picture
        // imported from TimeSnapper: a window, each its own size, shown at that size.
        var tiles = _plan.Tiles
            .Where(t => _plan.Focus is null || t.Frame is not null)
            .Select(t => t.Frame is not null && _bitmaps.TryGetValue(t.Monitor.Id, out var bitmap)
                ? new FrameTile(t.Monitor.IsTimeSnapper
                    ? new PixelRect(t.Monitor.Bounds.X, t.Monitor.Bounds.Y, bitmap.PixelSize.Width, bitmap.PixelSize.Height)
                    : t.Monitor.Bounds, bitmap, t.Label)
                : new FrameTile(t.Monitor.Bounds, null, t.Label))
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
