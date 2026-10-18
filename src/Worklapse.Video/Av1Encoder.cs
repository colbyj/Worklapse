using Worklapse.Core.Imaging;

namespace Worklapse.Video;

public readonly record struct EncodedFrame(ReadOnlyMemory<byte> Data, bool IsKey);

/// <summary>
/// A low-delay AV1 encoder: each call returns the frame it was given. The first frame is a key frame and
/// no others are inserted, so one encoder produces one segment.
/// </summary>
public sealed unsafe class Av1Encoder : IDisposable
{
    /// <summary>libaom speed preset; 6 is the fastest that still uses the full "good quality" tool set.</summary>
    public const int DefaultCpuUsed = 6;

    private nint _handle;

    public Av1Encoder(int width, int height, int quality, int cpuUsed = DefaultCpuUsed, int? threads = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        WorklapseNative.EnsureCompatible();
        Width = width;
        Height = height;
        var error = stackalloc byte[WorklapseNative.ErrorBufferLength];
        error[0] = 0;
        _handle = WorklapseNative.EncoderCreate(width, height, quality, cpuUsed, threads ?? DefaultThreads, error);
        if (_handle == 0)
            throw new InvalidOperationException($"Could not create the AV1 encoder ({width}×{height}): {WorklapseNative.Utf8(error)}");

        foreach (var (name, value) in options ?? DefaultOptions)
        {
            if (WorklapseNative.EncoderSetOption(_handle, name, value) != 0)
            {
                var message = WorklapseNative.Utf8(WorklapseNative.EncoderError(_handle));
                Dispose();
                throw new ArgumentException($"Invalid AV1 encoder option {name}={value}: {message}", nameof(options));
            }
        }
    }

    /// <summary>libaom options applied to every encoder unless others are given.</summary>
    public static IReadOnlyDictionary<string, string> DefaultOptions { get; } = new Dictionary<string, string>();

    public int Width { get; }
    public int Height { get; }

    internal static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>
    /// Encodes a packed I420 frame of this encoder's size. The returned data is a copy the caller owns.
    /// <paramref name="quality"/> changes the quantizer from this frame on; null keeps it.
    /// </summary>
    public EncodedFrame Encode(ReadOnlySpan<byte> i420, long ptsMs, int? quality = null)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (i420.Length < I420.Size(Width, Height))
            throw new ArgumentException("The frame is smaller than the encoder's frame size.", nameof(i420));

        fixed (byte* frame = i420)
        {
            var size = WorklapseNative.EncoderEncodeI420(_handle, frame, ptsMs, quality ?? -1, out var data, out var isKey);
            if (size < 0)
                throw new InvalidOperationException("AV1 encoding failed: " + WorklapseNative.Utf8(WorklapseNative.EncoderError(_handle)));
            return new EncodedFrame(new ReadOnlySpan<byte>(data, size).ToArray(), isKey != 0);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            WorklapseNative.EncoderDestroy(_handle);
            _handle = 0;
        }
    }
}
