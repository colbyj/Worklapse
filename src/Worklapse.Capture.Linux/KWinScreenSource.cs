using System.IO.Hashing;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Linux;

/// <summary>
/// Captures screens through KWin's screenshot interface (org.kde.KWin.ScreenShot2), at their full pixel size and
/// without the cursor, like the Windows capture.
/// </summary>
/// <remarks>
/// <para>
/// KWin answers with the image's size and pixel format, then writes the pixels into a pipe that Worklapse passes
/// along with the call. They are read a strip of rows at a time, hashed (to tell whether the screen changed) and
/// converted straight into one shared packed I420 frame, so a capture holds about 1.5 bytes per pixel of the largest
/// screen rather than a full-size copy.
/// </para>
/// <para>
/// KWin only answers programs whose desktop file lists the interface under X-KDE-DBUS-Restricted-Interfaces (see
/// <see cref="DesktopEntry"/>).
/// </para>
/// </remarks>
internal sealed class KWinScreenSource(KWinSession session) : IScreenSource
{
    /// <summary>Rows per strip. Even, so strips cover whole chroma rows.</summary>
    private const int StripRows = 64;

    /// <summary>How long KWin may take to answer and to deliver the pixels.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly XxHash3 _hasher = new();
    private byte[] _strip = [];
    private byte[] _frame = [];

    public IReadOnlyList<MonitorInfo> GetMonitors() => session.Layout.Monitors;

    public ScreenCapture? Capture(MonitorInfo monitor)
    {
        lock (_gate)
        {
            if (session.Layout.ScreenOf(monitor) is not { } screen)
                return null; // unplugged since the list was made

            using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
            Dictionary<string, VariantValue> results;
            try
            {
                results = RequestScreenshot(screen.Name, pipe.ClientSafePipeHandle).WaitAsync(Timeout).GetAwaiter().GetResult();
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName.EndsWith(".NoAuthorized", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "KWin refused to take a screenshot. It only allows programs installed with a desktop file that lists " +
                    $"org.kde.KWin.ScreenShot2 under X-KDE-DBUS-Restricted-Interfaces, run from the file it names ({DesktopEntry.FilePath}).", ex);
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName.EndsWith(".Cancelled", StringComparison.Ordinal))
            {
                // KWin cancels when it has no OpenGL context to render the screenshot with, which lasts.
                throw new InvalidOperationException(
                    "KWin couldn't take a screenshot: it only can while compositing with OpenGL (System Settings → Display and Monitor → Compositor).", ex);
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName.EndsWith(".InvalidScreen", StringComparison.Ordinal))
            {
                return null; // unplugged just now
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                // KWin has its own copy now (or never will); without ours, the pipe ends when KWin closes it.
                pipe.DisposeLocalCopyOfClientHandle();
            }

            var image = ImageInfo.From(results);
            if (image.Width <= 0 || image.Height <= 0 || image.Stride < image.Width * 4)
                throw new InvalidOperationException($"KWin's screenshot of {screen.Name} has an unexpected size ({image.Width}×{image.Height}, stride {image.Stride}).");

            var capture = Read(pipe, monitor, image);
            if (capture is not null)
                session.ReportCapturedSize(screen, capture.Width, capture.Height);
            return capture;
        }
    }

    private Task<Dictionary<string, VariantValue>> RequestScreenshot(string screenName, SafePipeHandle pipe) =>
        session.Connection.CallAsync(KWinSession.KWinService, "/org/kde/KWin/ScreenShot2", "org.kde.KWin.ScreenShot2",
            "CaptureScreen", "sa{sv}h",
            (ref MessageWriter w) =>
            {
                w.WriteString(screenName);
                var options = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("native-resolution");
                w.WriteVariantBool(true);
                w.WriteDictionaryEntryStart();
                w.WriteString("include-cursor");
                w.WriteVariantBool(false);
                w.WriteDictionaryEnd(options);
                w.WriteHandle(pipe);
            },
            static reply => reply.GetBodyReader().ReadDictionaryOfStringToVariantValue());

    /// <summary>Reads the pixels KWin writes into the pipe and converts them; null if they don't all arrive in time.</summary>
    private ScreenCapture? Read(AnonymousPipeServerStream pipe, MonitorInfo monitor, ImageInfo image)
    {
        var (width, height, stride) = (image.Width, image.Height, image.Stride);
        if (_strip.Length < stride * StripRows)
            _strip = new byte[stride * StripRows];
        var size = I420.Size(width, height);
        if (_frame.Length < size)
            _frame = new byte[size];

        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;
        _hasher.Reset();
        for (var y = 0; y < height; y += StripRows)
        {
            var rows = Math.Min(StripRows, height - y);
            var strip = _strip.AsSpan(0, stride * rows);
            if (!PipeReader.ReadExactly(pipe, strip, deadline))
                return null;

            for (var row = 0; row < rows; row++)
            {
                var pixels = strip.Slice(row * stride, width * 4);
                if (image.Order == ByteOrder.Rgba)
                    SwapRedAndBlue(pixels);
                _hasher.Append(pixels);
            }
            I420.FromBgraRows(strip, stride, width, height, y, rows, _frame);
        }

        return new ScreenCapture
        {
            Monitor = monitor,
            Width = width,
            Height = height,
            Frame = _frame.AsMemory(0, size),
            Hash = _hasher.GetCurrentHashAsUInt64(),
        };
    }

    private static void SwapRedAndBlue(Span<byte> pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    public void Dispose()
    {
    }

    internal enum ByteOrder
    {
        /// <summary>B, G, R, A in memory: QImage's 32-bit (A)RGB formats on a little-endian machine.</summary>
        Bgra,

        /// <summary>R, G, B, A in memory: QImage's RGBA8888 formats.</summary>
        Rgba,
    }

    /// <summary>What KWin says about the image it writes into the pipe.</summary>
    internal readonly record struct ImageInfo(int Width, int Height, int Stride, ByteOrder Order)
    {
        // QImage::Format values.
        private const int FormatRgb32 = 4;
        private const int FormatArgb32 = 5;
        private const int FormatArgb32Premultiplied = 6;
        private const int FormatRgbx8888 = 16;
        private const int FormatRgba8888 = 17;
        private const int FormatRgba8888Premultiplied = 18;

        public static ImageInfo From(IReadOnlyDictionary<string, VariantValue> results)
        {
            int Get(string key) => results.TryGetValue(key, out var value) && value.AsInteger() is { } number ? (int)number : 0;

            if (results.TryGetValue("type", out var type) && type.Type == VariantValueType.String && type.GetString() != "raw")
                throw new InvalidOperationException($"KWin sent a screenshot of type \"{type.GetString()}\"; Worklapse reads only raw images.");

            // Premultiplied alpha makes no difference: screens are opaque.
            var format = Get("format");
            var order = format switch
            {
                FormatRgb32 or FormatArgb32 or FormatArgb32Premultiplied when BitConverter.IsLittleEndian => ByteOrder.Bgra,
                FormatRgbx8888 or FormatRgba8888 or FormatRgba8888Premultiplied => ByteOrder.Rgba,
                _ => throw new InvalidOperationException(
                    $"KWin sent a screenshot in pixel format {format} (a QImage::Format); Worklapse reads only 8-bit RGB formats. Is HDR on?"),
            };
            return new ImageInfo(Get("width"), Get("height"), Get("stride"), order);
        }
    }

    /// <summary>Reads from a pipe with a deadline (a plain read could wait forever if KWin never writes).</summary>
    private static class PipeReader
    {
        public static bool ReadExactly(AnonymousPipeServerStream pipe, Span<byte> buffer, long deadline)
        {
            var fd = (int)pipe.SafePipeHandle.DangerousGetHandle();
            while (buffer.Length > 0)
            {
                var wait = deadline - Environment.TickCount64;
                if (wait <= 0 || !Libc.WaitReadable(fd, (int)wait))
                    return false;
                var read = pipe.Read(buffer);
                if (read == 0)
                    return false; // KWin closed the pipe early
                buffer = buffer[read..];
            }
            return true;
        }
    }
}
