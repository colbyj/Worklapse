using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Worklapse.App.ViewModels;
using Worklapse.Core.Settings;

namespace Worklapse.App.Views;

/// <summary>
/// The main window: a rail of views (Overview, Day review, Timesheet) with recording and Settings below them.
/// Closing it lets the views go; Worklapse keeps recording in the tray.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = null!;
    private readonly SettingsStore _settings = null!;
    private readonly Func<Task>? _quit;
    private readonly PlaceholderView _overview = new();
    private TimesheetView? _timesheet;
    private DayReviewView? _dayView;
    private string _dataFolder = "";

    // Needed by the XAML loader; the app always uses the other constructor.
    public MainWindow() => InitializeComponent();

    /// <param name="quit">Quits Worklapse (File → Quit).</param>
    public MainWindow(MainViewModel viewModel, SettingsStore settings, Func<Task> quit) : this()
    {
        _viewModel = viewModel;
        _settings = settings;
        _quit = quit;
        DataContext = viewModel;
        _dataFolder = settings.Current.DataFolder;

        _overview.Show("Overview", "The week and month overview comes in a later step.", viewModel.Recording);
        _timesheet = new TimesheetView(viewModel.Timesheet, viewModel.Recording);
        Host.Children.Add(_overview);
        Host.Children.Add(_timesheet);
        // Filled when it opens; one entry now so it shows as a submenu.
        SetProjectMenu.ItemsSource = new[] { new MenuItem { Header = "…", IsEnabled = false } };

        viewModel.PropertyChanged += OnViewModelChanged;
        _settings.Changed += OnSettingsChanged;

        // Arrow keys step through frames wherever focus is (tool bar buttons would otherwise move focus).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Activated += (_, _) => RefreshCurrent();

        SyncDayView();
        ShowCurrent();
    }


    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_viewModel is null)
            return;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _settings.Changed -= OnSettingsChanged;
        _dayView?.Close();
        _dayView = null;
        _viewModel.Dispose();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DayReview))
            SyncDayView();
        if (e.PropertyName is nameof(MainViewModel.View) or nameof(MainViewModel.DayReview))
        {
            ShowCurrent();
            RefreshCurrent();
        }
    }

    /// <summary>A view for the Day review the view model has now (none, or a new one after a folder change).</summary>
    private void SyncDayView()
    {
        if (_dayView is not null && _dayView.DataContext != _viewModel.DayReview)
        {
            Host.Children.Remove(_dayView);
            _dayView.Close();
            _dayView = null;
        }
        if (_dayView is null && _viewModel.DayReview is { } day)
        {
            _dayView = new DayReviewView(day, _settings.Current.DataFolder, _viewModel.Recording);
            Host.Children.Add(_dayView);
        }
    }

    private void ShowCurrent()
    {
        _overview.IsVisible = _viewModel.IsOverview;
        _timesheet!.IsVisible = _viewModel.IsTimesheet;
        if (_dayView is not null)
            _dayView.IsVisible = _viewModel.IsDayReview;
    }

    /// <summary>
    /// The day may have grown while the window was in the background, and the views share the history: Day review
    /// changes the Timesheet's logged time and notes, Settings its projects.
    /// </summary>
    private void RefreshCurrent()
    {
        if (_viewModel.IsDayReview)
            _viewModel.DayReview?.RefreshCommand.Execute(null);
        else if (_viewModel.IsTimesheet)
            _viewModel.Timesheet.Refresh();
    }

    private void OnSettingsChanged(object? sender, WorklapseSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        // Day review reads the history in the data folder it was opened on.
        if (string.Equals(settings.DataFolder, _dataFolder, StringComparison.OrdinalIgnoreCase))
            return;
        _dataFolder = settings.DataFolder;
        _viewModel.ReopenDayReview();
        // The Timesheet reads the folder the settings name at each load.
        if (_viewModel.IsTimesheet)
            _viewModel.Timesheet.Refresh();
    });

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel.IsDayReview)
            _dayView?.HandleKey(e);
    }

    /// <summary>The projects, listed when the submenu opens (they can change in Settings meanwhile).</summary>
    private void OnSetProjectMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (_dayView is not null && ReferenceEquals(e.Source, SetProjectMenu))
            SetProjectMenu.ItemsSource = _dayView.ProjectMenuItems();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => _dayView?.ZoomIn();

    private void OnZoomOut(object? sender, RoutedEventArgs e) => _dayView?.ZoomOut();

    private void OnFit(object? sender, RoutedEventArgs e) => _dayView?.Fit();

    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    private async void OnQuit(object? sender, RoutedEventArgs e)
    {
        if (_quit is not null)
            await _quit();
    }
}
