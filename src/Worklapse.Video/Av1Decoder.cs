using Worklapse.Core.Imaging;

namespace Worklapse.Video;

/// <summary>
/// Decodes the frames of one segment, in order from its key frame. Use a new decoder (or call
/// <see cref="Reset"/>) when jumping to another segment.
/// </summary>
public sealed unsafe class Av1Decoder : IDisposable
{
    private nint _handle;

    public Av1Decoder(int? threads = null)
    {
        WorklapseNative.EnsureCompatible();
        _threads = threads ?? Av1Encoder.DefaultThreads;
        _handle = Create(_threads);
    }

    private readonly int _threads;

    /// <summary>Size of the most recently decoded frame.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Decodes a frame without converting it, to advance through the frames before the one wanted.</summary>
    public void Decode(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (byte* p = data)
        {
            if (WorklapseNative.DecoderDecode(_handle, p, data.Length, out var width, out var height) != 0)
                throw new InvalidDataException("AV1 decoding failed: " + WorklapseNative.Utf8(WorklapseNative.DecoderError(_handle)));
            Width = width;
            Height = height;
        }
    }

    /// <summary>Converts the most recently decoded frame to BGRA.</summary>
    public void CopyBgra(Span<byte> destination, int stride)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (stride < Width * 4 || destination.Length < (long)stride * (Height - 1) + Width * 4)
            throw new ArgumentException("The destination is smaller than the decoded frame.", nameof(destination));
        fixed (byte* p = destination)
        {
            if (WorklapseNative.DecoderCopyBgra(_handle, p, stride) != 0)
                throw new InvalidOperationException(WorklapseNative.Utf8(WorklapseNative.DecoderError(_handle)));
        }
    }

    /// <summary>Copies the most recently decoded frame as packed I420.</summary>
    public void CopyI420(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (destination.Length < I420.Size(Width, Height))
            throw new ArgumentException("The destination is smaller than the decoded frame.", nameof(destination));
        fixed (byte* p = destination)
        {
            if (WorklapseNative.DecoderCopyI420(_handle, p) != 0)
                throw new InvalidOperationException(WorklapseNative.Utf8(WorklapseNative.DecoderError(_handle)));
        }
    }

    /// <summary>Discards decoder state so the next frame must be a key frame.</summary>
    public void Reset()
    {
        Dispose();
        _handle = Create(_threads);
        Width = Height = 0;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            WorklapseNative.DecoderDestroy(_handle);
            _handle = 0;
        }
    }

    private static nint Create(int threads)
    {
        var error = stackalloc byte[WorklapseNative.ErrorBufferLength];
        error[0] = 0;
        var handle = WorklapseNative.DecoderCreate(threads, error);
        if (handle == 0)
            throw new InvalidOperationException("Could not create the AV1 decoder: " + WorklapseNative.Utf8(error));
        return handle;
    }
}
