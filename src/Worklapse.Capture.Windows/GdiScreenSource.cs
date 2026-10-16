using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using static Worklapse.Capture.Windows.NativeMethods;

namespace Worklapse.Capture.Windows;

/// <summary>
/// Captures monitors with GDI, a strip of rows at a time, into one small bitmap shared by all monitors.
/// Each strip is hashed (to tell whether the monitor changed) and converted straight into a packed I420
/// frame, also shared, so a capture holds about 1.5 bytes per pixel of the largest monitor rather than
/// 4 bytes per pixel of every monitor. GDI needs no Direct3D device, which on a multi-GPU laptop saves
/// well over 100 MB of driver memory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenSource : IScreenSource
{
    /// <summary>Rows per strip. Even, so strips cover whole chroma rows.</summary>
    private const int StripRows = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ulong> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly XxHash3 _hasher = new();
    private StripBitmap? _strip;
    private byte[] _frame = [];

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
            var (width, height) = (monitor.Bounds.Width, monitor.Bounds.Height);
            if (_strip is null || _strip.Width < width)
            {
                _strip?.Dispose();
                _strip = new StripBitmap(width, StripRows);
            }
            var size = I420.Size(width, height);
            if (_frame.Length < size)
                _frame = new byte[size];

            var screen = GetDC(0);
            if (screen == 0)
                return null;
            try
            {
                _hasher.Reset();
                var stride = _strip.Width * 4;
                for (var y = 0; y < height; y += StripRows)
                {
                    var rows = Math.Min(StripRows, height - y);
                    // Fails while the secure desktop (UAC prompt, lock screen) is shown.
                    if (!BitBlt(_strip.Dc, 0, 0, width, rows, screen, monitor.Bounds.X, monitor.Bounds.Y + y, SrcCopy))
                        return null;

                    var strip = _strip.Pixels[..(stride * rows)];
                    for (var row = 0; row < rows; row++)
                        _hasher.Append(strip.Slice(row * stride, width * 4));
                    I420.FromBgraRows(strip, stride, width, height, y, rows, _frame);
                }
            }
            finally
            {
                ReleaseDC(0, screen);
            }

            var hash = _hasher.GetCurrentHashAsUInt64();
            var changed = !_hashes.TryGetValue(monitor.DeviceName, out var previous) || previous != hash;
            _hashes[monitor.DeviceName] = hash;

            return new ScreenCapture
            {
                Monitor = monitor,
                Width = width,
                Height = height,
                Frame = _frame.AsMemory(0, size),
                Changed = changed,
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _strip?.Dispose();
            _strip = null;
        }
    }

    /// <summary>A memory DC with a small top-down 32-bit DIB section that a strip of the screen is copied into.</summary>
    private sealed unsafe class StripBitmap : IDisposable
    {
        private readonly nint _bitmap;
        private readonly nint _previousBitmap;
        private readonly byte* _bits;

        public StripBitmap(int width, int rows)
        {
            Width = width;
            Rows = rows;
            Dc = CreateCompatibleDC(0);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -rows, // negative: rows top-down
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, 0, 0);
            if (_bitmap == 0)
            {
                DeleteDC(Dc);
                throw new InvalidOperationException($"Could not allocate a {width}×{rows} capture bitmap.");
            }
            _bits = (byte*)bits;
            _previousBitmap = SelectObject(Dc, _bitmap);
        }

        public int Width { get; }
        public int Rows { get; }
        public nint Dc { get; }
        public ReadOnlySpan<byte> Pixels => new(_bits, Width * Rows * 4);

        public void Dispose()
        {
            SelectObject(Dc, _previousBitmap);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
    }
}
