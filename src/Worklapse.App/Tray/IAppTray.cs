namespace Worklapse.App.Tray;

/// <summary>The dot on the tray icon: whether Worklapse is recording.</summary>
public enum TrayBadge
{
    /// <summary>Not capturing, and nothing to act on: idle, locked, an excluded app, starting.</summary>
    None,
    Recording,
    Paused,
    Error,
}

/// <summary>What the tray icon's menu does.</summary>
public sealed record TrayCommands(Action Show, Action TogglePause, Action Settings, Action Quit);

/// <summary>
/// The tray icon: shows whether Worklapse is recording, offers its menu, and shows desktop notifications. Used from the
/// UI thread only.
/// </summary>
public interface IAppTray : IDisposable
{
    /// <summary>Raised on the UI thread when the icon, or a notification it showed, is clicked.</summary>
    event EventHandler? Activated;

    /// <param name="pauseHeader">The menu's pause item: "Pause Recording" or "Resume Recording".</param>
    void Update(TrayBadge badge, string toolTip, string pauseHeader);

    void Notify(string title, string text);
}
