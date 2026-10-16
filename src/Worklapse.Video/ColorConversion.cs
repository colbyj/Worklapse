using Worklapse.Core.Imaging;

namespace Worklapse.Video;

public static unsafe class ColorConversion
{
    /// <summary>Converts a packed I420 frame to BGRA (BT.709 full range), using the native library.</summary>
    public static void I420ToBgra(ReadOnlySpan<byte> i420, int width, int height, Span<byte> bgra, int stride)
    {
        Av1Native.EnsureCompatible();
        if (i420.Length < I420.Size(width, height))
            throw new ArgumentException("The I420 frame is smaller than its size says.", nameof(i420));
        if (stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
            throw new ArgumentException("The destination is too small.", nameof(bgra));
        fixed (byte* source = i420)
        fixed (byte* destination = bgra)
            Av1Native.I420ToBgra(source, width, height, destination, stride);
    }
}
