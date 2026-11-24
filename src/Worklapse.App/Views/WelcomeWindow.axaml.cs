using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Worklapse.App.ViewModels;

namespace Worklapse.App.Views;

/// <summary>
/// Opens by itself at the first start, and from the rail: what Worklapse does, what it records (Settings' Recording tab,
/// applied when the window closes) and how a day goes.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly Hints _hints = null!;
    private Control[] _pages = [];
    private int _page;
    private bool _previewStarted;

    // Needed by the XAML loader; the app always uses the other constructor.
    public WelcomeWindow() => InitializeComponent();

    public WelcomeWindow(SettingsViewModel settings, Hints hints) : this()
    {
        DataContext = settings;
        _hints = hints;
        _pages = [IntroPage, RecordingPage, RoutinePage];
        ShowPage(0);
        Closed += (_, _) =>
        {
            // There's no Cancel: closing the window, however it's done, keeps what was chosen.
            if (settings.IsDirty && settings.HasDataFolder)
                settings.Apply();
            settings.Preview?.Dispose();
            settings.Detach();
        };
    }

    /// <summary>Set Up Projects… was chosen: Settings opens on its Projects tab once this window has closed.</summary>
    public bool SetUpProjects { get; private set; }

    private void ShowPage(int page)
    {
        _page = page;
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].IsVisible = i == page;
        var last = page == _pages.Length - 1;
        StepText.Text = $"Step {page + 1} of {_pages.Length}";
        BackButton.IsVisible = page > 0;
        NextButton.Content = last ? "Get Started" : "Next";
        ShowTipsButton.IsVisible = _hints.AnyDismissed;
        // The preview fills its box at 1:1, so it starts once the page is laid out.
        if (_pages[page] == RecordingPage && !_previewStarted)
        {
            _previewStarted = true;
            Dispatcher.UIThread.Post(Recording.StartPreview, DispatcherPriority.Loaded);
        }
    }

    private void OnBack(object? sender, RoutedEventArgs e) => ShowPage(Math.Max(0, _page - 1));

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_page < _pages.Length - 1)
            ShowPage(_page + 1);
        else
            Close();
    }

    private void OnSetUpProjects(object? sender, RoutedEventArgs e)
    {
        SetUpProjects = true;
        Close();
    }

    private void OnShowTips(object? sender, RoutedEventArgs e)
    {
        _hints.ShowAll();
        ShowTipsButton.IsVisible = false;
    }
}
