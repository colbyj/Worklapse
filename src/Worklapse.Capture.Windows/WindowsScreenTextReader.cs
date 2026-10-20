using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Windows;

/// <summary>
/// Reads text with Windows' own OCR (Windows.Media.Ocr) in the user's languages: nothing to install or ship. Measured at
/// ~80 ms for a window-sized picture on one core.
/// </summary>
public sealed class WindowsScreenTextReader : IScreenTextReader
{
    private readonly OcrEngine _engine;

    private WindowsScreenTextReader(OcrEngine engine) => _engine = engine;

    /// <summary>A reader, or null when none of the user's languages can be read.</summary>
    public static WindowsScreenTextReader? Create() =>
        OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new WindowsScreenTextReader(engine) : null;

    public string Read(byte[] gray, int width, int height)
    {
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
            return "";
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(gray), BitmapPixelFormat.Gray8, width, height);
        var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        return string.Join('\n', result.Lines.Select(line => line.Text));
    }
}
