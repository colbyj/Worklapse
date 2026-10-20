using System.Collections.Concurrent;
using Tmds.DBus.Protocol;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Desktop notifications through org.freedesktop.Notifications (KDE Plasma, GNOME and most other desktops). The session
/// bus is connected at the first notification, on a connection of its own, so this works whether or not recording could
/// start.
/// </summary>
public sealed class DesktopNotifications : IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string DefaultAction = "default"; // the spec's name for clicking the notification itself

    private readonly string? _iconPath;
    private readonly Lazy<Task<DBusConnection>> _connection;
    private readonly ConcurrentDictionary<uint, bool> _shown = new(); // ids of ours; the signals reach every client

    /// <param name="iconPath">A picture file shown with each notification; null for none.</param>
    public DesktopNotifications(string? iconPath)
    {
        _iconPath = iconPath;
        _connection = new Lazy<Task<DBusConnection>>(ConnectAsync);
    }

    /// <summary>Raised on a D-Bus thread when one of these notifications is clicked.</summary>
    public event EventHandler? Clicked;

    public async Task ShowAsync(string summary, string body)
    {
        var connection = await _connection.Value.ConfigureAwait(false);
        var id = await connection.CallAsync(Service, ObjectPath, Service, "Notify", "susssasa{sv}i",
            (ref MessageWriter w) =>
            {
                w.WriteString("Worklapse");
                w.WriteUInt32(0); // replaces no earlier notification
                w.WriteString(_iconPath ?? "");
                w.WriteString(summary);
                w.WriteString(body);
                w.WriteArray(new[] { DefaultAction, "Open Worklapse" });
                var hints = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("desktop-entry"); // worklapse.desktop (see DesktopEntry): the desktop's settings for the app apply
                w.WriteVariantString("worklapse");
                w.WriteDictionaryEnd(hints);
                w.WriteInt32(-1); // the server's usual timeout
            },
            static reply => reply.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
        _shown[id] = true;
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        var address = DBusAddress.Session
            ?? throw new PlatformNotSupportedException("There is no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            await connection.WatchSignalAsync(Service, ObjectPath, Service, "ActionInvoked",
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Action: reader.ReadString());
                },
                (Notification<(uint Id, string Action)> notification) =>
                {
                    if (notification.HasValue && notification.Value.Action == DefaultAction && _shown.ContainsKey(notification.Value.Id))
                        Clicked?.Invoke(this, EventArgs.Empty);
                },
                synchronizationContext: null, ObserverFlags.None, state: null).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
            _connection.Value.Result.Dispose();
    }
}
