using Worklapse.Core.Imaging;

namespace Worklapse.Video;

/// <summary>
/// A decoded frame as packed I420, shared by reference count between <see cref="FrameProvider"/>'s cache
/// and whoever is showing it. Every holder calls <see cref="Release"/> once when done; the pixel buffer then
/// goes back to its pool for the next decode, so playback doesn't allocate a new multi-megabyte array per frame.
/// </summary>
public sealed class DecodedFrame
{
    private readonly FramePool _pool;
    private byte[]? _i420;
    private int _references = 1;

    internal DecodedFrame(FramePool pool, int width, int height, byte[] i420)
    {
        _pool = pool;
        _i420 = i420;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }

    public ReadOnlySpan<byte> I420 => Writable;

    /// <summary>For filling a freshly rented frame, before anyone else holds it.</summary>
    internal Span<byte> Writable => (_i420 ?? throw new ObjectDisposedException(nameof(DecodedFrame))).AsSpan(0, Core.Imaging.I420.Size(Width, Height));

    /// <summary>Adds a holder. Only call it while holding a reference already.</summary>
    public DecodedFrame AddReference()
    {
        if (Interlocked.Increment(ref _references) <= 1)
            throw new InvalidOperationException("The frame was already released.");
        return this;
    }

    public void Release()
    {
        var left = Interlocked.Decrement(ref _references);
        if (left < 0)
            throw new InvalidOperationException("The frame was released more often than it was referenced.");
        if (left == 0)
        {
            _pool.Return(_i420!);
            _i420 = null;
        }
    }

    /// <summary>Converts the frame to BGRA at <paramref name="destination"/> (for example a locked bitmap).</summary>
    public unsafe void CopyBgra(nint destination, int stride) =>
        ColorConversion.I420ToBgra(I420, Width, Height, new Span<byte>((void*)destination, stride * Height), stride);
}

/// <summary>
/// Reuses I420 frame buffers. Frames are large-object-heap arrays (several megabytes each); allocating one
/// per decoded frame leaves gigabytes of garbage that the GC reclaims late and keeps committed. Thread-safe:
/// frames are rented on the decoding thread and returned on whichever thread releases them last.
/// </summary>
internal sealed class FramePool
{
    /// <summary>Free buffers kept per frame size; anything beyond is left to the GC.</summary>
    private const int MaxFreePerSize = 2;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Stack<byte[]>> _free = [];

    public DecodedFrame Rent(int width, int height)
    {
        var size = I420.Size(width, height);
        byte[]? buffer = null;
        lock (_gate)
        {
            if (_free.TryGetValue(size, out var stack) && stack.Count > 0)
                buffer = stack.Pop();
        }
        return new DecodedFrame(this, width, height, buffer ?? GC.AllocateUninitializedArray<byte>(size));
    }

    public void Return(byte[] buffer)
    {
        lock (_gate)
        {
            if (!_free.TryGetValue(buffer.Length, out var stack))
                _free[buffer.Length] = stack = new Stack<byte[]>();
            if (stack.Count < MaxFreePerSize)
                stack.Push(buffer);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _free.Clear();
    }
}
