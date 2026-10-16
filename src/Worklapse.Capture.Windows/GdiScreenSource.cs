using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Worklapse.Core.Platform;
using static Worklapse.Capture.Windows.NativeMethods;

namespace Worklapse.Capture.Windows;

/// <summary>
/// Captures monitors by copying from the screen with GDI into a DIB section per monitor. Unlike Desktop
/// Duplication this needs no Direct3D device, which on a multi-GPU laptop saves well over 100 MB of
/// driver memory; the copy costs tens of milliseconds every few seconds. Changes are detected by hashing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenSource : IScreenSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MonitorBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var r = info.Monitor;
                monitors.Add(new MonitorInfo(info.DeviceName, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    (info.Flags & MonitorInfoPrimary) != 0));
            }
            return true;
        }, 0);
        return monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    public ScreenCapture? Capture(MonitorInfo monitor)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(monitor.DeviceName, out var buffer)
                || buffer.Width != monitor.Bounds.Width || buffer.Height != monitor.Bounds.Height)
            {
                buffer?.Dispose();
                _buffers[monitor.DeviceName] = buffer = new MonitorBuffer(monitor.Bounds.Width, monitor.Bounds.Height);
            }

            var screen = GetDC(0);
            if (screen == 0)
                return null;
            try
            {
                // CAPTUREBLT includes layered windows. BitBlt fails while the secure desktop is shown.
                if (!BitBlt(buffer.Dc, 0, 0, buffer.Width, buffer.Height, screen, monitor.Bounds.X, monitor.Bounds.Y, SrcCopy | CaptureBlt))
                    return null;
            }
            finally
            {
                ReleaseDC(0, screen);
            }

            var pixels = buffer.Memory.Memory;
            var hash = XxHash3.HashToUInt64(pixels.Span);
            var changed = !buffer.HasImage || hash != buffer.Hash;
            buffer.Hash = hash;
            buffer.HasImage = true;

            return new ScreenCapture
            {
                Monitor = monitor,
                Width = buffer.Width,
                Height = buffer.Height,
                Stride = buffer.Width * 4,
                Pixels = pixels,
                Changed = changed,
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var buffer in _buffers.Values)
                buffer.Dispose();
            _buffers.Clear();
        }
    }

    /// <summary>A memory DC with a top-down 32-bit DIB section the size of one monitor.</summary>
    private sealed class MonitorBuffer : IDisposable
    {
        private readonly nint _bitmap;
        private readonly nint _previousBitmap;

        public MonitorBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Dc = CreateCompatibleDC(0);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // negative: rows top-down, like every other buffer in the app
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, 0, 0);
            if (_bitmap == 0)
            {
                DeleteDC(Dc);
                throw new InvalidOperationException($"Could not allocate a {width}×{height} capture bitmap.");
            }
            _previousBitmap = SelectObject(Dc, _bitmap);
            Memory = new NativeMemory(bits, width * height * 4);
        }

        public int Width { get; }
        public int Height { get; }
        public nint Dc { get; }
        public NativeMemory Memory { get; }
        public bool HasImage { get; set; }
        public ulong Hash { get; set; }

        public void Dispose()
        {
            SelectObject(Dc, _previousBitmap);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
    }

    /// <summary>Exposes the DIB section's memory as <see cref="Memory{T}"/> without copying it.</summary>
    private sealed unsafe class NativeMemory(nint pointer, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new((void*)pointer, length);

        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)pointer + elementIndex);

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
