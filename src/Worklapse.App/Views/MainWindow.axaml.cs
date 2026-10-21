using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Worklapse.App.Theming;
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
    private readonly OverviewView _overview = null!;
    private readonly TimesheetView _timesheet = null!;
    private DayReviewView? _dayView;
    private string _dataFolder = "";
    private TimeOnly _dayStart;

    // Needed by the XAML loader; the app always uses the other constructor.
    public MainWindow()
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);
    }

    /// <param name="quit">Quits Worklapse (Ctrl+Q).</param>
    public MainWindow(MainViewModel viewModel, SettingsStore settings, Func<Task> quit) : this()
    {
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        _dataFolder = settings.Current.DataFolder;
        _dayStart = settings.Current.DayStart;

        _overview = new OverviewView(viewModel.Overview, viewModel.Recording);
        _timesheet = new TimesheetView(viewModel.Timesheet, viewModel.Recording);
        Host.Children.Add(_overview);
        Host.Children.Add(_timesheet);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Control), Command = new RelayCommand(Close) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Q, KeyModifiers.Control), Command = new AsyncRelayCommand(quit) });

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
        // A new Day review (after a folder or day start change) is shown at once if it's the view; one made by opening a day from
        // another view is shown by the switch that follows.
        if (e.PropertyName == nameof(MainViewModel.View) || (e.PropertyName == nameof(MainViewModel.DayReview) && _viewModel.IsDayReview))
        {
            ShowCurrent();
            _viewModel.RefreshView();
        }
    }

    /// <summary>A view for the Day review the view model has now (none, or a new one after a folder or day start change).</summary>
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
        // Day review reads the history in the data folder it was opened on, with the days starting as they did then.
        var sameFolder = string.Equals(settings.DataFolder, _dataFolder, StringComparison.OrdinalIgnoreCase);
        if (sameFolder && settings.DayStart == _dayStart)
            return;
        _dataFolder = settings.DataFolder;
        _dayStart = settings.DayStart;
        _viewModel.ReopenDayReview(keepDay: sameFolder);
        // The Overview and the Timesheet read the folder and the day start the settings name at each load.
        if (!_viewModel.IsDayReview)
            _viewModel.RefreshView();
    });

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel.IsDayReview)
            _dayView?.HandleKey(e);
    }
}
