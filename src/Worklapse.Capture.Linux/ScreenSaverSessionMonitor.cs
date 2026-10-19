using Tmds.DBus.Protocol;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Tracks whether the screen is locked, from the desktop's screen saver service (org.freedesktop.ScreenSaver). On
/// Plasma that is the screen locker: "active" means locked.
/// </summary>
internal sealed class ScreenSaverSessionMonitor : ISessionStateMonitor
{
    private const string Service = "org.freedesktop.ScreenSaver";
    private const string ObjectPath = "/org/freedesktop/ScreenSaver";

    private readonly IDisposable? _subscription;
    private volatile bool _locked;

    private ScreenSaverSessionMonitor(DBusConnection connection)
    {
        // Subscribe first, so a change while asking isn't missed.
        _subscription = connection.WatchSignalAsync(Service, ObjectPath, Service, "ActiveChanged",
                static (message, _) => message.GetBodyReader().ReadBool(),
                (Notification<bool> notification) =>
                {
                    if (notification.HasValue)
                        _locked = notification.Value;
                },
                synchronizationContext: null, ObserverFlags.None, state: null)
            .AsTask().GetAwaiter().GetResult();

        _locked = connection.CallAsync(Service, ObjectPath, Service, "GetActive", null, null,
            static reply => reply.GetBodyReader().ReadBool()).GetAwaiter().GetResult();
    }

    public static ScreenSaverSessionMonitor Start(DBusConnection connection) =>
        Task.Run(() => new ScreenSaverSessionMonitor(connection)).GetAwaiter().GetResult();

    public bool IsLocked => _locked;

    public void Dispose() => _subscription?.Dispose();
}
