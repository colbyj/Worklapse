using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Worklapse.Core.Diagnostics;

namespace Worklapse.App.Tray;

/// <summary>
/// The Windows tray icon, through Shell_NotifyIcon directly rather than Avalonia's: that's what lets it show
/// notifications (balloons, which Windows 10 and 11 show as toasts in the notification centre). Its messages arrive at a
/// hidden window on the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class Win32Tray : IAppTray
{
    private const string ClassName = "WorklapseTrayWindow";
    private const uint IconId = 1;

    // Messages, and what the icon reports in its callback message (NOTIFYICON_VERSION_4: the event in lParam's low word).
    private const uint WmApp = 0x8000, CallbackMessage = WmApp + 1;
    private const int WmContextMenu = 0x007B, NinSelect = 0x0400, NinKeySelect = 0x0401, NinBalloonUserClick = 0x0405;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4, NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifUser = 0x04, NiifLargeIcon = 0x20, NiifRespectQuietTime = 0x80;
    private const int SmCxIcon = 11, SmCxSmIcon = 49;
    private const int ErrorClassAlreadyExists = 1410;

    /// <summary>The window procedure's tray: there is one.</summary>
    private static Win32Tray? s_instance;

    private readonly TrayCommands _commands;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _window;
    private readonly Dictionary<TrayBadge, IntPtr> _icons = []; // made at _iconSize
    private int _iconSize;
    private IntPtr _balloonIcon;
    private TrayBadge _badge = TrayBadge.None;
    private string _toolTip = "Worklapse";
    private string _pauseHeader = "Pause Recording";
    private TrayMenuWindow? _menu;
    private bool _added;

    public Win32Tray(TrayCommands commands)
    {
        _commands = commands;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var instance = GetModuleHandle(IntPtr.Zero);
        fixed (char* name = ClassName)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WindowProc,
                Instance = instance,
                ClassName = (IntPtr)name,
            };
            if (RegisterClassEx(&windowClass) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
                throw new Win32Exception();
        }
        s_instance = this;
        // Hidden and top-level rather than message-only: only top-level windows hear TaskbarCreated.
        _window = CreateWindowEx(0, ClassName, "Worklapse", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
            throw new Win32Exception();
        AddIcon();
    }

    public event EventHandler? Activated;

    public void Update(TrayBadge badge, string toolTip, string pauseHeader)
    {
        _pauseHeader = pauseHeader; // read when the menu opens
        if (badge == _badge && toolTip == _toolTip)
            return;
        (_badge, _toolTip) = (badge, toolTip);
        if (!_added)
            return;
        var data = NewData(NifIcon | NifTip | NifShowTip);
        data.Icon = IconFor(badge);
        Copy(toolTip, data.Tip, 128);
        Shell_NotifyIcon(NimModify, &data);
    }

    public void Notify(string title, string text)
    {
        if (!_added)
        {
            Log.Info($"No tray icon to show a notification with: {title}");
            return;
        }
        var data = NewData(NifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        data.InfoFlags = NiifUser | NiifLargeIcon | NiifRespectQuietTime;
        data.BalloonIcon = BalloonIcon();
        if (!Shell_NotifyIcon(NimModify, &data))
            Log.Error($"Windows didn't show a notification: {title}");
    }

    public void Dispose()
    {
        _menu?.Close();
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NimDelete, &data);
            _added = false;
        }
        DestroyWindow(_window);
        s_instance = null;
        DestroyIcons();
    }

    /// <summary>Adds the icon: at the start, and again when Explorer restarts (after a crash, or a change of DPI).</summary>
    private void AddIcon()
    {
        // The shell's small-icon size, as the icons are made for it.
        var size = GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForSystem());
        if (size != _iconSize)
        {
            DestroyIcons();
            _iconSize = size;
        }
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = IconFor(_badge);
        Copy(_toolTip, data.Tip, 128);
        _added = Shell_NotifyIcon(NimAdd, &data);
        if (!_added)
        {
            // Explorer isn't up yet (Worklapse started with Windows): TaskbarCreated comes once it is.
            Log.Info("The tray icon couldn't be added yet.");
            return;
        }
        data.VersionOrTimeout = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, &data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)sizeof(NotifyIconData),
        Window = _window,
        Id = IconId,
        Flags = flags,
    };

    private IntPtr IconFor(TrayBadge badge)
    {
        if (!_icons.TryGetValue(badge, out var icon))
            _icons[badge] = icon = CreateIcon(TrayIconImages.Render(badge, _iconSize), _iconSize);
        return icon;
    }

    /// <summary>The app icon without a badge, at the large size notifications show.</summary>
    private IntPtr BalloonIcon()
    {
        if (_balloonIcon == IntPtr.Zero)
        {
            var size = GetSystemMetricsForDpi(SmCxIcon, GetDpiForSystem());
            _balloonIcon = CreateIcon(TrayIconImages.Render(TrayBadge.None, size), size);
        }
        return _balloonIcon;
    }

    private static IntPtr CreateIcon(Bitmap bitmap, int size)
    {
        using var png = new MemoryStream();
        using (bitmap)
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
        fixed (byte* data = png.GetBuffer())
        {
            // Icon resources may hold a PNG (since Windows Vista).
            var icon = CreateIconFromResourceEx(data, (uint)png.Length, true, 0x00030000, size, size, 0);
            return icon != IntPtr.Zero ? icon : throw new Win32Exception();
        }
    }

    private void DestroyIcons()
    {
        foreach (var icon in _icons.Values)
            DestroyIcon(icon);
        _icons.Clear();
        if (_balloonIcon != IntPtr.Zero)
            DestroyIcon(_balloonIcon);
        _balloonIcon = IntPtr.Zero;
    }

    private static void Copy(string text, char* buffer, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(buffer, capacity));
        buffer[length] = '\0';
    }

    private bool OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreated)
        {
            AddIcon();
            return true;
        }
        if (message != CallbackMessage)
            return false;
        switch ((int)((long)lParam & 0xFFFF))
        {
            case NinSelect or NinKeySelect or NinBalloonUserClick:
                Activated?.Invoke(this, EventArgs.Empty);
                break;
            case WmContextMenu:
                // Where to open it, in screen pixels, in wParam.
                ShowMenu(new PixelPoint((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF)));
                break;
        }
        return true;
    }

    private void ShowMenu(PixelPoint anchor)
    {
        _menu?.Close();
        var menu = _menu = new TrayMenuWindow(
        [
            ("Show Worklapse", _commands.Show),
            (_pauseHeader, _commands.TogglePause),
            ("Settings…", _commands.Settings),
            null,
            ("Quit Worklapse", _commands.Quit),
        ]);
        menu.Closed += (_, _) =>
        {
            if (_menu == menu)
                _menu = null;
        };
        menu.ShowAt(anchor);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (s_instance is { } tray && tray._window == window && tray.OnMessage(message, wParam, lParam))
                return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            // It can't unwind into Windows' code: rethrown on the dispatcher, where the app reports the UI's errors.
            var error = ExceptionDispatchInfo.Capture(ex);
            Dispatcher.UIThread.Post(error.Throw);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        public IntPtr ClassName;
        public IntPtr SmallIcon;
    }

    /// <summary>NOTIFYICONDATAW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint VersionOrTimeout;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(WndClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial IntPtr GetModuleHandle(IntPtr moduleName);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIcon(uint message, NotifyIconData* data);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr CreateIconFromResourceEx(byte* data, uint length, [MarshalAs(UnmanagedType.Bool)] bool isIcon,
        uint version, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}
