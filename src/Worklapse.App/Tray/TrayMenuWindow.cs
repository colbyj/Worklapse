using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Worklapse.App.Tray;

/// <summary>
/// The Windows tray icon's menu: the app's own menu look in a borderless window (as Avalonia's tray icon draws it),
/// closed by a choice, Escape, or clicking elsewhere.
/// </summary>
internal sealed partial class TrayMenuWindow : Window
{
    private bool _closed;

    /// <param name="items">The menu's items in order; null for a separator.</param>
    public TrayMenuWindow(IReadOnlyList<(string Header, Action Click)?> items)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var controls = new List<Control>();
        foreach (var item in items)
        {
            if (item is not { } entry)
            {
                controls.Add(new Separator());
                continue;
            }
            var (header, click) = entry;
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (_, _) =>
            {
                Close();
                click();
            };
            controls.Add(menuItem);
        }
        Content = new MenuPresenter(this) { ItemsSource = controls };

        Deactivated += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Closed += (_, _) => _closed = true;
    }

    public new void Close()
    {
        if (!_closed)
            base.Close();
    }

    /// <summary>
    /// Opens with a corner at <paramref name="anchor"/> (screen pixels): above and to the right of it where that fits
    /// the screen's work area, as the task bar's own menus do.
    /// </summary>
    public void ShowAt(PixelPoint anchor)
    {
        Position = anchor;
        Show(); // lays it out, so its size is known
        if ((Screens.ScreenFromPoint(anchor) ?? Screens.Primary) is { } screen)
        {
            var size = PixelSize.FromSize(ClientSize, DesktopScaling);
            var area = screen.WorkingArea;
            var x = anchor.X + size.Width <= area.Right ? anchor.X : anchor.X - size.Width;
            var y = anchor.Y - size.Height >= area.Y ? anchor.Y - size.Height : anchor.Y;
            Position = new PixelPoint(
                Math.Clamp(x, area.X, Math.Max(area.X, area.Right - size.Width)),
                Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
        }
        // Brought to the front explicitly, or clicking elsewhere wouldn't deactivate it and it would stay open.
        Activate();
        if (TryGetPlatformHandle()?.Handle is { } handle)
            SetForegroundWindow(handle);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    /// <summary>A menu presenter whose closing (after a choice) closes the window it's in.</summary>
    private sealed class MenuPresenter(TrayMenuWindow window) : MenuFlyoutPresenter
    {
        protected override Type StyleKeyOverride => typeof(MenuFlyoutPresenter);

        public override void Close() => window.Close();
    }
}
