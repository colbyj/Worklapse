using System.IO;

namespace Worklapse.Core.Import;

/// <summary>
/// What the TimeSnapper reader (Worklapse.TimeSnapperReader.exe, a 32-bit helper: TimeSnapper's database opens only
/// with its own 32-bit Firebird engine) writes to its standard output, in <see cref="BinaryWriter"/> encoding. Shared
/// by both sides as source, since the helper is a .NET Framework program.
/// <list type="number">
/// <item>header: <see cref="Magic"/>, <see cref="Version"/> (int32 each);</item>
/// <item>programs: count, then per program its id, name, path (nullable), type (0 = exe, 1 = website);</item>
/// <item>classified programs (TimeSnapper's productive ones): count, then their ids;</item>
/// <item>activities: their count (for progress), then each as <see cref="MoreRows"/> followed by id, SNAPDATE as local
/// ticks, file name, program, window caption (strings nullable), program id, website program id (nullable int32);
/// and <see cref="EndOfRows"/> after the last.</item>
/// </list>
/// </summary>
internal static class TimeSnapperStream
{
    public const int Magic = 0x53544C57; // "WLTS"
    public const int Version = 1;
    public const byte MoreRows = 1;
    public const byte EndOfRows = 0;

    public static void WriteNullable(this BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    public static void WriteNullable(this BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    public static string? ReadNullableString(this BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    public static int? ReadNullableInt32(this BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;
}
