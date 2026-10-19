using Tmds.DBus.Protocol;

namespace Worklapse.Capture.Linux;

/// <summary>Writes a method call's arguments after its header.</summary>
internal delegate void WriteArguments(ref MessageWriter writer);

internal static class DBusCalls
{
    /// <summary>Calls a method and reads its reply with <paramref name="read"/>.</summary>
    /// <param name="signature">The arguments' D-Bus signature; null for none.</param>
    public static Task<T> CallAsync<T>(this DBusConnection connection, string destination, string path,
        string @interface, string member, string? signature, WriteArguments? write, Func<Message, T> read)
    {
        var writer = connection.GetMessageWriter();
        MessageBuffer message;
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
            write?.Invoke(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return connection.CallMethodAsync(message, static (reply, state) => ((Func<Message, T>)state!)(reply), read);
    }

    /// <summary>Calls a method that returns nothing (or whose reply doesn't matter).</summary>
    public static Task CallAsync(this DBusConnection connection, string destination, string path, string @interface,
        string member, string? signature = null, WriteArguments? write = null) =>
        connection.CallAsync(destination, path, @interface, member, signature, write, static _ => true);

    /// <summary>The unique name (":1.23") of the connection that owns a well-known name.</summary>
    public static Task<string> GetNameOwnerAsync(this DBusConnection connection, string name) =>
        connection.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s",
            (ref MessageWriter w) => w.WriteString(name),
            static reply => reply.GetBodyReader().ReadString());

    /// <summary>A number from a variant of any integer type; null for anything else.</summary>
    public static long? AsInteger(this VariantValue value) => value.Type switch
    {
        VariantValueType.Byte => value.GetByte(),
        VariantValueType.Int16 => value.GetInt16(),
        VariantValueType.UInt16 => value.GetUInt16(),
        VariantValueType.Int32 => value.GetInt32(),
        VariantValueType.UInt32 => value.GetUInt32(),
        VariantValueType.Int64 => value.GetInt64(),
        VariantValueType.UInt64 => (long)value.GetUInt64(),
        VariantValueType.Variant => value.GetVariantValue().AsInteger(),
        _ => null,
    };
}
