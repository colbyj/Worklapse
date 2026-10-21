using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Windows;

/// <summary>
/// Reads text with Windows' own OCR (Windows.Media.Ocr) in the user's languages: nothing to install or ship. Measured at
/// ~80 ms for a window-sized picture on one core. Reached through COM (WindowsOcrInterop.cs), so the build stays plain
/// net10.0.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenTextReader : IScreenTextReader
{
    private readonly uint _maxDimension;

    /// <summary>Each reading thread's own engine and factories, made on that thread so no call crosses apartments.</summary>
    private readonly ThreadLocal<Engine> _engines = new(() => new Engine());

    private WindowsScreenTextReader(uint maxDimension) => _maxDimension = maxDimension;

    /// <summary>A reader, or null when none of the user's languages can be read (or this Windows has no OCR).</summary>
    public static WindowsScreenTextReader? Create()
    {
        try
        {
            WinRT.InitializeThread();
            var statics = WinRT.Factory<IOcrEngineStatics>(WinRT.OcrEngineClass);
            try
            {
                if (statics.TryCreateFromUserProfileLanguages() is not { } engine)
                    return null;
                Marshal.ReleaseComObject(engine);
                return new WindowsScreenTextReader(statics.GetMaxImageDimension());
            }
            finally
            {
                Marshal.ReleaseComObject(statics);
            }
        }
        catch (Exception e) when (e is COMException or TypeLoadException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public string Read(byte[] gray, int width, int height)
    {
        if (width > _maxDimension || height > _maxDimension)
            return "";
        var engine = _engines.Value!;
        var buffer = engine.Buffers.CreateFromByteArray((uint)(width * height), gray);
        ISoftwareBitmap? bitmap = null;
        IOcrResultOperation? operation = null;
        IOcrResult? result = null;
        try
        {
            bitmap = engine.Bitmaps.CreateCopyFromBuffer(buffer, WinRT.Gray8PixelFormat, width, height);
            operation = engine.Ocr.RecognizeAsync(bitmap);
            var completed = new Completion();
            operation.SetCompleted(completed);
            completed.Wait();
            result = operation.GetResults();
            return WinRT.TakeString(result.GetText());
        }
        finally
        {
            // Released now rather than by the finalizer: a bitmap holds a copy of the picture.
            foreach (var wrapper in new object?[] { result, operation, bitmap, buffer })
            {
                if (wrapper is not null)
                    Marshal.ReleaseComObject(wrapper);
            }
        }
    }

    /// <summary>One thread's OCR engine and the factories that make its input.</summary>
    private sealed class Engine
    {
        public Engine()
        {
            WinRT.InitializeThread();
            var statics = WinRT.Factory<IOcrEngineStatics>(WinRT.OcrEngineClass);
            Ocr = statics.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException("Windows OCR can't read any of the user's languages.");
            Marshal.ReleaseComObject(statics);
            Bitmaps = WinRT.Factory<ISoftwareBitmapStatics>(WinRT.SoftwareBitmapClass);
            Buffers = WinRT.Factory<ICryptographicBufferStatics>(WinRT.CryptographicBufferClass);
        }

        public IOcrEngine Ocr { get; }
        public ISoftwareBitmapStatics Bitmaps { get; }
        public ICryptographicBufferStatics Buffers { get; }
    }

    /// <summary>
    /// The handler told when a reading ends, from a thread of Windows' choosing. Its event isn't disposed: Windows may
    /// still be inside <see cref="Invoke"/> when the wait returns.
    /// </summary>
    [ComVisible(true)]
    private sealed class Completion : IOcrResultCompletedHandler
    {
        private readonly ManualResetEventSlim _done = new();

        public void Invoke(nint operation, int status) => _done.Set();

        public void Wait() => _done.Wait();
    }
}
