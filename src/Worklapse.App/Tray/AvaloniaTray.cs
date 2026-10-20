using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Worklapse.Capture.Linux;
using Worklapse.Core.Diagnostics;

namespace Worklapse.App.Tray;

/// <summary>
/// Avalonia's tray icon (a StatusNotifierItem on Linux desktops), with notifications through the desktop's notification
/// service where there is one.
/// </summary>
public sealed class AvaloniaTray : IAppTray
{
    /// <summary>Linux panels scale the picture to their own size; the largest the .ico has.</summary>
    private const int IconSize = 48;

    private readonly Application _application;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _pauseItem;
    private readonly DesktopNotifications? _notifications;
    private readonly Dictionary<TrayBadge, WindowIcon> _icons = [];
    private TrayBadge? _badge;

    /// <param name="notifications">Where notifications go (taken over); null where there's no notification service.</param>
    public AvaloniaTray(Application application, TrayCommands commands, DesktopNotifications? notifications)
    {
        _application = application;
        _notifications = notifications;

        var showItem = new NativeMenuItem("Show Worklapse");
        showItem.Click += (_, _) => commands.Show();
        _pauseItem = new NativeMenuItem("Pause Recording");
        _pauseItem.Click += (_, _) => commands.TogglePause();
        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => commands.Settings();
        var quitItem = new NativeMenuItem("Quit Worklapse");
        quitItem.Click += (_, _) => commands.Quit();

        _icon = new TrayIcon
        {
            ToolTipText = "Worklapse",
            Menu = new NativeMenu { Items = { showItem, _pauseItem, settingsItem, new NativeMenuItemSeparator(), quitItem } },
        };
        _icon.Clicked += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        if (_notifications is not null)
            _notifications.Clicked += OnNotificationClicked;
        TrayIcon.SetIcons(application, [_icon]);
        Update(TrayBadge.None, "Worklapse", "Pause Recording");
    }

    public event EventHandler? Activated;

    public void Update(TrayBadge badge, string toolTip, string pauseHeader)
    {
        if (badge != _badge)
        {
            _badge = badge;
            if (!_icons.TryGetValue(badge, out var icon))
                _icons[badge] = icon = new WindowIcon(TrayIconImages.Render(badge, IconSize));
            _icon.Icon = icon;
        }
        _icon.ToolTipText = toolTip;
        _pauseItem.Header = pauseHeader;
    }

    public void Notify(string title, string text)
    {
        if (_notifications is null)
            return;
        _notifications.ShowAsync(title, text).ContinueWith(
            task => Log.Error("Couldn't show a desktop notification.", task.Exception!.GetBaseException()),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnNotificationClicked(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => Activated?.Invoke(this, EventArgs.Empty));

    public void Dispose()
    {
        if (_notifications is not null)
        {
            _notifications.Clicked -= OnNotificationClicked;
            _notifications.Dispose();
        }
        TrayIcon.SetIcons(_application, []);
        _icon.Dispose();
    }
}
