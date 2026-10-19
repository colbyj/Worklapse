using Worklapse.Core.Platform;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Time since the last input, from the compositor's idle notifications (the ext-idle-notify-v1 Wayland protocol):
/// it says when the user has been idle for a few seconds and when they're back.
/// </summary>
/// <remarks>
/// Version 2 of the protocol has notifications that only count input, like Windows' last-input time. Version 1's also
/// count programs that keep the screen awake (a video player), so with it watching a video never turns idle.
/// </remarks>
internal sealed class WaylandIdleDetector : IIdleDetector, IDisposable
{
    /// <summary>How long without input before the compositor says so; idle times are only known beyond this.</summary>
    internal static readonly TimeSpan NotifyAfter = TimeSpan.FromSeconds(5);

    // ext_idle_notifier_v1
    private const ushort GetIdleNotification = 1;
    private const ushort GetInputIdleNotification = 2;

    // ext_idle_notification_v1
    private const ushort IdledEvent = 0;
    private const ushort ResumedEvent = 1;

    private readonly WaylandConnection _connection;
    private readonly Func<long> _clock;
    private readonly uint _notification;
    private readonly Thread _reader;

    /// <summary>The clock's time of the last input while idle; -1 while active.</summary>
    private long _lastInput = -1;

    private volatile string? _failure;

    /// <param name="clock">Milliseconds on a steady clock (<see cref="Environment.TickCount64"/> outside tests).</param>
    internal WaylandIdleDetector(WaylandConnection connection, Func<long>? clock = null)
    {
        _connection = connection;
        _clock = clock ?? (() => Environment.TickCount64);

        var globals = ListGlobals(connection, out var registry);
        var seat = globals.FirstOrDefault(g => g.Interface == "wl_seat");
        var notifier = globals.FirstOrDefault(g => g.Interface == "ext_idle_notifier_v1");
        if (seat.Interface is null || notifier.Interface is null)
            throw new PlatformNotSupportedException("The compositor doesn't offer idle notifications (ext-idle-notify-v1).");

        var seatId = Bind(connection, registry, seat, 1);
        var version = Math.Min(notifier.Version, 2u);
        var notifierId = Bind(connection, registry, notifier, version);
        IsInputOnly = version >= 2;

        _notification = connection.NewObject();
        connection.Send(notifierId, IsInputOnly ? GetInputIdleNotification : GetIdleNotification, new WaylandArguments()
            .Uint(_notification)
            .Uint((uint)NotifyAfter.TotalMilliseconds)
            .Uint(seatId));

        _reader = new Thread(ReadEvents) { IsBackground = true, Name = "Wayland idle notifications" };
        _reader.Start();
    }

    public static WaylandIdleDetector Connect()
    {
        var connection = WaylandConnection.Connect();
        try
        {
            return new WaylandIdleDetector(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Whether the notifications ignore programs that keep the screen awake (protocol version 2).</summary>
    public bool IsInputOnly { get; }

    public TimeSpan GetIdleTime()
    {
        if (_failure is { } failure)
            throw new InvalidOperationException(failure);
        var lastInput = Interlocked.Read(ref _lastInput);
        return lastInput < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_clock() - lastInput);
    }

    private void ReadEvents()
    {
        try
        {
            while (true)
            {
                var message = _connection.Receive();
                if (message.ObjectId == _notification && message.Opcode == IdledEvent)
                    Interlocked.Exchange(ref _lastInput, _clock() - (long)NotifyAfter.TotalMilliseconds);
                else if (message.ObjectId == _notification && message.Opcode == ResumedEvent)
                    Interlocked.Exchange(ref _lastInput, -1);
                else if (message.ObjectId == WaylandConnection.Display && message.Opcode == WaylandConnection.DisplayErrorEvent)
                    throw ProtocolError(message);
                // Anything else (the seat's capabilities and name) doesn't matter here.
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed.
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException or InvalidOperationException)
        {
            _failure = $"Idle detection stopped: {ex.Message}";
        }
    }

    /// <summary>The compositor's globals (name, interface, version), with a round trip to be sure all have arrived.</summary>
    private static List<(uint Name, string Interface, uint Version)> ListGlobals(WaylandConnection connection, out uint registry)
    {
        registry = connection.NewObject();
        connection.Send(WaylandConnection.Display, WaylandConnection.DisplayGetRegistry, new WaylandArguments().Uint(registry));
        var done = connection.NewObject();
        connection.Send(WaylandConnection.Display, WaylandConnection.DisplaySync, new WaylandArguments().Uint(done));

        var globals = new List<(uint, string, uint)>();
        while (true)
        {
            var message = connection.Receive();
            if (message.ObjectId == registry && message.Opcode == WaylandConnection.RegistryGlobalEvent)
                globals.Add((message.Uint(), message.String() ?? "", message.Uint()));
            else if (message.ObjectId == done && message.Opcode == WaylandConnection.CallbackDoneEvent)
                return globals;
            else if (message.ObjectId == WaylandConnection.Display && message.Opcode == WaylandConnection.DisplayErrorEvent)
                throw ProtocolError(message);
        }
    }

    private static uint Bind(WaylandConnection connection, uint registry, (uint Name, string Interface, uint Version) global, uint version)
    {
        var id = connection.NewObject();
        connection.Send(registry, WaylandConnection.RegistryBind, new WaylandArguments()
            .Uint(global.Name)
            .String(global.Interface)
            .Uint(version)
            .Uint(id));
        return id;
    }

    private static InvalidOperationException ProtocolError(WaylandEvent error)
    {
        var objectId = error.Uint();
        var code = error.Uint();
        return new InvalidOperationException($"Wayland protocol error on object {objectId} (code {code}): {error.String()}");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _reader.Join(TimeSpan.FromSeconds(1));
    }
}
