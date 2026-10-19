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
    private readonly OverviewView _overview = null!;
    private readonly TimesheetView _timesheet = null!;
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

        _overview = new OverviewView(viewModel.Overview, viewModel.Recording);
        _timesheet = new TimesheetView(viewModel.Timesheet, viewModel.Recording);
        Host.Children.Add(_overview);
        Host.Children.Add(_timesheet);
        // Filled when it opens; one entry now so it shows as a submenu.
        SetProjectMenu.ItemsSource = new[] { new MenuItem { Header = "…", IsEnabled = false } };

        viewModel.PropertyChanged += OnViewModelChanged;
        _settings.Changed += OnSettingsChanged;

        // Arrow keys step through frames wherever focus is (tool bar buttons would otherwise move focus).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Activated += (_, _) => viewModel.RefreshView(activated: true);

        SyncDayView();
        ShowCurrent();
        // Not left to the first activation: a window shown from the tray isn't always brought to the front.
        viewModel.RefreshView();
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
        // A new Day review (after a folder change) is shown at once if it's the view; one made by opening a day from
        // another view is shown by the switch that follows.
        if (e.PropertyName == nameof(MainViewModel.View) || (e.PropertyName == nameof(MainViewModel.DayReview) && _viewModel.IsDayReview))
        {
            ShowCurrent();
            _viewModel.RefreshView();
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
        _timesheet.IsVisible = _viewModel.IsTimesheet;
        if (_dayView is not null)
            _dayView.IsVisible = _viewModel.IsDayReview;
    }

    /// <summary>Brings the view shown up to date (after Settings closes: its projects may have changed).</summary>
    public void RefreshView() => _viewModel.RefreshView();

    private void OnSettingsChanged(object? sender, WorklapseSettings settings) => Dispatcher.UIThread.Post(() =>
    {
        // Day review reads the history in the data folder it was opened on.
        if (string.Equals(settings.DataFolder, _dataFolder, StringComparison.OrdinalIgnoreCase))
            return;
        _dataFolder = settings.DataFolder;
        _viewModel.ReopenDayReview();
        // The Overview and the Timesheet read the folder the settings name at each load.
        if (!_viewModel.IsDayReview)
            _viewModel.RefreshView();
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
