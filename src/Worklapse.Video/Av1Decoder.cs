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
        Av1Native.EnsureCompatible();
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
            if (Av1Native.DecoderDecode(_handle, p, data.Length, out var width, out var height) != 0)
                throw new InvalidDataException("AV1 decoding failed: " + Av1Native.Utf8(Av1Native.DecoderError(_handle)));
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
            if (Av1Native.DecoderCopyBgra(_handle, p, stride) != 0)
                throw new InvalidOperationException(Av1Native.Utf8(Av1Native.DecoderError(_handle)));
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
            Av1Native.DecoderDestroy(_handle);
            _handle = 0;
        }
    }

    private static nint Create(int threads)
    {
        var error = stackalloc byte[Av1Native.ErrorBufferLength];
        error[0] = 0;
        var handle = Av1Native.DecoderCreate(threads, error);
        if (handle == 0)
            throw new InvalidOperationException("Could not create the AV1 decoder: " + Av1Native.Utf8(error));
        return handle;
    }
}
