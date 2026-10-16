using System.Runtime.InteropServices;

namespace Worklapse.Video;

/// <summary>Bindings for the C wrapper in native/av1/worklapse_av1.c.</summary>
internal static unsafe partial class Av1Native
{
    private const string Library = "worklapse_av1";

    /// <summary>The wrapper ABI these bindings were written for; checked once on first use.</summary>
    public const int ExpectedAbiVersion = 1;

    public const int ErrorBufferLength = 512;

    [LibraryImport(Library, EntryPoint = "wl_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Library, EntryPoint = "wl_aom_version")]
    public static partial nint AomVersion();

    [LibraryImport(Library, EntryPoint = "wl_enc_create")]
    public static partial nint EncoderCreate(int width, int height, int quality, int cpuUsed, int threads, byte* error);

    [LibraryImport(Library, EntryPoint = "wl_enc_encode_bgra")]
    public static partial int EncoderEncodeBgra(nint encoder, byte* bgra, int stride, long ptsMs, int quality, out byte* data, out int isKey);

    [LibraryImport(Library, EntryPoint = "wl_enc_set_option", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int EncoderSetOption(nint encoder, string name, string value);

    [LibraryImport(Library, EntryPoint = "wl_enc_error")]
    public static partial nint EncoderError(nint encoder);

    [LibraryImport(Library, EntryPoint = "wl_enc_destroy")]
    public static partial void EncoderDestroy(nint encoder);

    [LibraryImport(Library, EntryPoint = "wl_dec_create")]
    public static partial nint DecoderCreate(int threads, byte* error);

    [LibraryImport(Library, EntryPoint = "wl_dec_decode")]
    public static partial int DecoderDecode(nint decoder, byte* data, int size, out int width, out int height);

    [LibraryImport(Library, EntryPoint = "wl_dec_copy_bgra")]
    public static partial int DecoderCopyBgra(nint decoder, byte* bgra, int stride);

    [LibraryImport(Library, EntryPoint = "wl_dec_error")]
    public static partial nint DecoderError(nint decoder);

    [LibraryImport(Library, EntryPoint = "wl_dec_destroy")]
    public static partial void DecoderDestroy(nint decoder);

    private static int _checked;

    public static void EnsureCompatible()
    {
        if (Volatile.Read(ref _checked) == 1)
            return;
        var version = AbiVersion();
        if (version != ExpectedAbiVersion)
            throw new InvalidOperationException(
                $"worklapse_av1 has ABI version {version}, but this build expects {ExpectedAbiVersion}. Rebuild the native library.");
        Volatile.Write(ref _checked, 1);
    }

    public static string Utf8(nint text) => Marshal.PtrToStringUTF8(text) ?? "";

    public static string Utf8(byte* text) => Marshal.PtrToStringUTF8((nint)text) ?? "";
}
