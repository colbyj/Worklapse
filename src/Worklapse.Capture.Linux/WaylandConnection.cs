using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Worklapse.Capture.Linux;

/// <summary>
/// A minimal Wayland client: the wire protocol over the compositor's socket, with no library. Enough for objects whose
/// messages carry only numbers, strings and object ids (no file descriptors), such as idle notifications.
/// </summary>
/// <remarks>
/// A message is the object id (32 bits), then the message size in bytes including this 8-byte header (high 16 bits)
/// with the opcode (low 16 bits), then the arguments: 32-bit numbers, and strings as their length including a
/// terminating zero, the bytes and the zero, padded to a multiple of 4. All in the machine's byte order (little
/// endian here). Requests go to an object; events come from one. Object 1 is the display.
/// </remarks>
internal sealed class WaylandConnection : IDisposable
{
    public const uint Display = 1;

    // wl_display
    public const ushort DisplaySync = 0;
    public const ushort DisplayGetRegistry = 1;
    public const ushort DisplayErrorEvent = 0;

    // wl_registry
    public const ushort RegistryBind = 0;
    public const ushort RegistryGlobalEvent = 0;

    // wl_callback
    public const ushort CallbackDoneEvent = 0;

    private readonly Socket _socket;
    private readonly byte[] _header = new byte[8];
    private uint _nextId = 2;

    public WaylandConnection(Socket socket) => _socket = socket;

    /// <summary>Connects to the compositor named by WAYLAND_DISPLAY.</summary>
    public static WaylandConnection Connect()
    {
        var path = SocketPath(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            ?? throw new PlatformNotSupportedException("There is no Wayland session (WAYLAND_DISPLAY or XDG_RUNTIME_DIR is not set).");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new WaylandConnection(socket);
    }

    /// <summary>WAYLAND_DISPLAY is a socket name in XDG_RUNTIME_DIR, or a full path.</summary>
    internal static string? SocketPath(string? display, string? runtimeDirectory)
    {
        if (string.IsNullOrEmpty(display))
            return null;
        if (display.StartsWith('/'))
            return display;
        return string.IsNullOrEmpty(runtimeDirectory) ? null : Path.Combine(runtimeDirectory, display);
    }

    /// <summary>An id for a new object, to pass in the request that creates it.</summary>
    public uint NewObject() => _nextId++;

    public void Send(uint objectId, ushort opcode, WaylandArguments arguments)
    {
        var body = arguments.Bytes;
        var message = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)message.Length << 16 | opcode);
        body.CopyTo(message.AsSpan(8));
        _socket.Send(message);
    }

    /// <summary>Waits for the next event. Throws <see cref="EndOfStreamException"/> when the compositor closes the connection.</summary>
    public WaylandEvent Receive()
    {
        ReceiveExactly(_header);
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(_header);
        var sizeAndOpcode = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));
        var size = (int)(sizeAndOpcode >> 16);
        if (size < 8)
            throw new InvalidDataException($"The compositor sent a malformed message (size {size}).");
        var arguments = new byte[size - 8];
        ReceiveExactly(arguments);
        return new WaylandEvent(objectId, (ushort)sizeAndOpcode, arguments);
    }

    private void ReceiveExactly(Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var read = _socket.Receive(buffer);
            if (read == 0)
                throw new EndOfStreamException("The compositor closed the Wayland connection.");
            buffer = buffer[read..];
        }
    }

    public void Dispose()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        _socket.Dispose();
    }
}

/// <summary>A request's arguments, in order.</summary>
internal sealed class WaylandArguments
{
    private readonly List<byte> _bytes = [];

    public byte[] Bytes => [.. _bytes];

    public WaylandArguments Uint(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _bytes.AddRange(bytes);
        return this;
    }

    public WaylandArguments String(string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        Uint((uint)text.Length + 1);
        _bytes.AddRange(text);
        _bytes.Add(0);
        while (_bytes.Count % 4 != 0)
            _bytes.Add(0);
        return this;
    }
}

/// <summary>An event: the object it comes from, which event it is, and its arguments, read in order.</summary>
internal sealed class WaylandEvent(uint objectId, ushort opcode, byte[] arguments)
{
    private int _offset;

    public uint ObjectId { get; } = objectId;

    public ushort Opcode { get; } = opcode;

    public byte[] Arguments { get; } = arguments;

    public uint Uint()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(Arguments.AsSpan(_offset));
        _offset += 4;
        return value;
    }

    /// <summary>A string argument; null for a null string.</summary>
    public string? String()
    {
        var length = (int)Uint();
        if (length == 0)
            return null;
        var value = Encoding.UTF8.GetString(Arguments, _offset, length - 1);
        _offset += (length + 3) & ~3;
        return value;
    }
}
