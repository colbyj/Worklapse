using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>An encoded image in native memory, written out from there; dispose it to free it.</summary>
public sealed unsafe class EncodedImage : IDisposable
{
    private byte* _data;

    internal EncodedImage(FrameFormat format, byte* data, int length)
    {
        Format = format;
        _data = data;
        Length = length;
    }

    public FrameFormat Format { get; }
    public int Length { get; }

    public ReadOnlySpan<byte> Data => _data != null ? new(_data, Length) : throw new ObjectDisposedException(nameof(EncodedImage));

    public void Dispose()
    {
        if (_data != null)
        {
            WorklapseNative.ImageFree(_data, (int)Format);
            _data = null;
        }
    }
}

/// <summary>
/// JPEG and WebP images of packed I420 frames (see <see cref="I420"/>). The files show the right colours in any
/// viewer (JPEG and WebP are BT.601; Worklapse's frames BT.709), and other programs' JPEGs decode too.
/// </summary>
public static unsafe class ImageCodec
{
    public static string Extension(FrameFormat format) => format switch
    {
        FrameFormat.Jpeg => ".jpg",
        FrameFormat.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an image format."),
    };

    /// <param name="quality">JPEG 1–100, WebP 0–100; higher is sharper and larger.</param>
    public static EncodedImage Encode(FrameFormat format, ReadOnlySpan<byte> i420, int width, int height, int quality)
    {
        WorklapseNative.EnsureCompatible();
        if (i420.Length < I420.Size(width, height))
            throw new ArgumentException("The frame is smaller than its size says.", nameof(i420));

        var error = stackalloc byte[WorklapseNative.ErrorBufferLength];
        error[0] = 0;
        byte* data = null;
        int size;
        fixed (byte* frame = i420)
        {
            size = format switch
            {
                FrameFormat.Jpeg => WorklapseNative.JpegEncodeI420(frame, width, height, quality, out data, error),
                FrameFormat.WebP => WorklapseNative.WebPEncodeI420(frame, width, height, quality, out data, error),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an image format."),
            };
        }
        if (size < 0)
            throw new InvalidOperationException($"Could not encode a {width}×{height} {format} image: {WorklapseNative.Utf8(error)}");
        return new EncodedImage(format, data, size);
    }

    /// <summary>The format and size of an image file's contents (JPEG or WebP).</summary>
    public static (FrameFormat Format, int Width, int Height) Identify(ReadOnlySpan<byte> image)
    {
        WorklapseNative.EnsureCompatible();
        var error = stackalloc byte[WorklapseNative.ErrorBufferLength];
        error[0] = 0;
        fixed (byte* data = image)
        {
            var format = WorklapseNative.ImageInfo(data, image.Length, out var width, out var height, error);
            if (format < 0)
                throw new InvalidDataException("Not a readable image: " + WorklapseNative.Utf8(error));
            return ((FrameFormat)format, width, height);
        }
    }

    /// <summary>Decodes a JPEG or WebP image of this size (see <see cref="Identify"/>) into packed I420.</summary>
    public static void DecodeI420(ReadOnlySpan<byte> image, int width, int height, Span<byte> i420)
    {
        WorklapseNative.EnsureCompatible();
        if (i420.Length < I420.Size(width, height))
            throw new ArgumentException("The destination is smaller than the image.", nameof(i420));
        var error = stackalloc byte[WorklapseNative.ErrorBufferLength];
        error[0] = 0;
        fixed (byte* data = image)
        fixed (byte* destination = i420)
        {
            if (WorklapseNative.ImageDecodeI420(data, image.Length, width, height, destination, error) != 0)
                throw new InvalidDataException("Could not decode the image: " + WorklapseNative.Utf8(error));
        }
    }

    /// <summary>
    /// Decodes a JPEG or WebP image of this size to BGRA with the libraries' own colour conversion: what other
    /// programs show. Worklapse itself decodes to I420 (<see cref="DecodeI420"/>).
    /// </summary>
    public static void DecodeBgra(ReadOnlySpan<byte> image, int width, int height, Span<byte> bgra, int stride)
    {
        WorklapseNative.EnsureCompatible();
        if (stride < width * 4 || bgra.Length < (long)stride * height)
            throw new ArgumentException("The destination is smaller than the image.", nameof(bgra));
        var error = stackalloc byte[WorklapseNative.ErrorBufferLength];
        error[0] = 0;
        fixed (byte* data = image)
        fixed (byte* destination = bgra)
        {
            if (WorklapseNative.ImageDecodeBgra(data, image.Length, width, height, destination, stride, error) != 0)
                throw new InvalidDataException("Could not decode the image: " + WorklapseNative.Utf8(error));
        }
    }
}
