using System.Net.Sockets;
using Worklapse.Capture.Linux;

namespace Worklapse.Tests;

/// <summary>Idle detection against a fake compositor that speaks the Wayland wire protocol over a Unix socket.</summary>
public class WaylandIdleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(2u, 2, true)]   // version 2: input-only notifications
    [InlineData(1u, 1, false)]  // version 1: the plain kind
    public async Task IdleTimeComesFromTheCompositorsNotifications(uint notifierVersion, int expectedOpcode, bool inputOnly)
    {
        using var folder = new TempFolder();
        using var pair = SocketPair.Create(Path.Combine(folder.Path, "wayland-test"));
        using var compositor = new WaylandConnection(pair.Server);
        var now = 100_000L;

        var handshake = Task.Run(() =>
        {
            var getRegistry = compositor.Receive();
            Assert.Equal((WaylandConnection.Display, WaylandConnection.DisplayGetRegistry), (getRegistry.ObjectId, getRegistry.Opcode));
            var registry = getRegistry.Uint();
            var sync = compositor.Receive();
            Assert.Equal((WaylandConnection.Display, WaylandConnection.DisplaySync), (sync.ObjectId, sync.Opcode));
            var callback = sync.Uint();

            compositor.Send(registry, WaylandConnection.RegistryGlobalEvent, new WaylandArguments().Uint(7).String("wl_compositor").Uint(6));
            compositor.Send(registry, WaylandConnection.RegistryGlobalEvent, new WaylandArguments().Uint(8).String("wl_seat").Uint(9));
            compositor.Send(registry, WaylandConnection.RegistryGlobalEvent, new WaylandArguments().Uint(9).String("ext_idle_notifier_v1").Uint(notifierVersion));
            compositor.Send(callback, WaylandConnection.CallbackDoneEvent, new WaylandArguments().Uint(0));

            var bindSeat = compositor.Receive();
            Assert.Equal((registry, WaylandConnection.RegistryBind), (bindSeat.ObjectId, bindSeat.Opcode));
            Assert.Equal((8u, "wl_seat", 1u), (bindSeat.Uint(), bindSeat.String(), bindSeat.Uint()));
            var seat = bindSeat.Uint();

            var bindNotifier = compositor.Receive();
            Assert.Equal((9u, "ext_idle_notifier_v1", notifierVersion), (bindNotifier.Uint(), bindNotifier.String(), bindNotifier.Uint()));
            var notifier = bindNotifier.Uint();

            var request = compositor.Receive();
            Assert.Equal((notifier, (ushort)expectedOpcode), (request.ObjectId, request.Opcode));
            var notification = request.Uint();
            Assert.Equal(5000u, request.Uint());
            Assert.Equal(seat, request.Uint());
            return notification;
        }, TestContext.Current.CancellationToken);

        using var detector = new WaylandIdleDetector(new WaylandConnection(pair.Client), () => now);
        var notification = await handshake.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(inputOnly, detector.IsInputOnly);
        Assert.Equal(TimeSpan.Zero, detector.GetIdleTime());

        // Idle: the last input was the notification's delay ago, and the idle time grows from there.
        compositor.Send(notification, 0, new WaylandArguments());
        WaitUntil(() => detector.GetIdleTime() == TimeSpan.FromSeconds(5));
        now += 60_000;
        Assert.Equal(TimeSpan.FromSeconds(65), detector.GetIdleTime());

        // Back.
        compositor.Send(notification, 1, new WaylandArguments());
        WaitUntil(() => detector.GetIdleTime() == TimeSpan.Zero);

        // The compositor going away is reported rather than read as "active forever".
        pair.Server.Shutdown(SocketShutdown.Both);
        WaitUntil(() =>
        {
            try
            {
                detector.GetIdleTime();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        });
    }

    [Fact]
    public void ACompositorWithoutIdleNotificationsIsNotSupported()
    {
        using var folder = new TempFolder();
        using var pair = SocketPair.Create(Path.Combine(folder.Path, "wayland-test"));
        using var compositor = new WaylandConnection(pair.Server);
        _ = Task.Run(() =>
        {
            var registry = compositor.Receive().Uint();
            var callback = compositor.Receive().Uint();
            compositor.Send(registry, WaylandConnection.RegistryGlobalEvent, new WaylandArguments().Uint(1).String("wl_seat").Uint(9));
            compositor.Send(callback, WaylandConnection.CallbackDoneEvent, new WaylandArguments().Uint(0));
        }, TestContext.Current.CancellationToken);

        using var client = new WaylandConnection(pair.Client);
        Assert.Throws<PlatformNotSupportedException>(() => new WaylandIdleDetector(client));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            Thread.Sleep(5);
        }
    }

    /// <summary>Both ends of a Unix socket connection.</summary>
    private sealed class SocketPair(Socket client, Socket server) : IDisposable
    {
        public Socket Client { get; } = client;
        public Socket Server { get; } = server;

        public static SocketPair Create(string path)
        {
            var endPoint = new UnixDomainSocketEndPoint(path);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endPoint);
            listener.Listen(1);
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(endPoint);
            return new SocketPair(client, listener.Accept());
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }
}
