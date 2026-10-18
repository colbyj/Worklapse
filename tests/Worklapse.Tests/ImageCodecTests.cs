using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>JPEG and WebP images of captures: what Worklapse shows, and what other programs show.</summary>
public class ImageCodecTests
{
    // Odd sizes, so the chroma planes round up.
    private const int Width = 321, Height = 181;

    // Saturated colours, where a BT.709 / BT.601 mix-up shows most (green would come out with ~20 levels of red).
    private static readonly (byte R, byte G, byte B)[] Colours =
        [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255), (255, 128, 0), (40, 40, 40)];
    private const int PatchSize = 36, PatchTop = 120;

    [Theory]
    [InlineData(FrameFormat.Jpeg)]
    [InlineData(FrameFormat.WebP)]
    public void AnImageDecodesToTheCapturedFrame(FrameFormat format)
    {
        var frame = ToI420(Screen());
        using var image = ImageCodec.Encode(format, frame, Width, Height, quality: 95);
        Assert.Equal((format, Width, Height), ImageCodec.Identify(image.Data));

        var decoded = new byte[frame.Length];
        ImageCodec.DecodeI420(image.Data, Width, Height, decoded);
        var psnr = Psnr(frame, decoded);
        Assert.True(psnr > 35, $"PSNR {psnr:0.0} dB");
    }

    [Theory]
    [InlineData(FrameFormat.Jpeg)]
    [InlineData(FrameFormat.WebP)]
    public void OtherProgramsAndWorklapseShowTheColoursCaptured(FrameFormat format)
    {
        var screen = Screen();
        using var image = ImageCodec.Encode(format, ToI420(screen), Width, Height, quality: 95);

        // Decoded by the libraries themselves, the way any viewer does it.
        var elsewhere = new byte[screen.Length];
        ImageCodec.DecodeBgra(image.Data, Width, Height, elsewhere, Width * 4);
        // Decoded the way Day review does it.
        var i420 = new byte[I420.Size(Width, Height)];
        ImageCodec.DecodeI420(image.Data, Width, Height, i420);
        var inWorklapse = new byte[screen.Length];
        ColorConversion.I420ToBgra(i420, Width, Height, inWorklapse, Width * 4);

        foreach (var (shown, where) in new[] { (elsewhere, "elsewhere"), (inWorklapse, "in Worklapse") })
        {
            for (var i = 0; i < Colours.Length; i++)
            {
                // The middle of each patch: its edges are blurred by chroma subsampling and compression.
                var p = ((PatchTop + PatchSize / 2) * Width + i * PatchSize + PatchSize / 2) * 4;
                var (r, g, b) = Colours[i];
                var actual = (R: shown[p + 2], G: shown[p + 1], B: shown[p]);
                Assert.True(Math.Abs(actual.R - r) <= 8 && Math.Abs(actual.G - g) <= 8 && Math.Abs(actual.B - b) <= 8,
                    $"{format} {where}: ({r}, {g}, {b}) shows as {actual}");
            }
        }
    }

    [Theory]
    [InlineData(FrameFormat.Jpeg, 1, 100)]
    [InlineData(FrameFormat.WebP, 0, 100)]
    public void LowerQualityMakesSmallerFiles(FrameFormat format, int lowest, int highest)
    {
        var frame = ToI420(Screen());
        var sizes = new[] { lowest, 15, 50, highest }.Select(q =>
        {
            using var image = ImageCodec.Encode(format, frame, Width, Height, q);
            return image.Length;
        }).ToList();
        Assert.Equal(sizes.Order(), sizes);
        Assert.True(sizes[0] < sizes[^1] / 4, string.Join(", ", sizes));
    }

    [Fact]
    public void SomethingElseIsNotAnImage()
    {
        Assert.Throws<InvalidDataException>(() => ImageCodec.Identify("not a picture"u8));
        Assert.Throws<InvalidDataException>(() => ImageCodec.Identify([]));
    }

    [Theory]
    [InlineData(FrameFormat.Jpeg)]
    [InlineData(FrameFormat.WebP)]
    public void ADamagedFileIsReportedOrShownInPartButNeverCrashes(FrameFormat format)
    {
        var frame = ToI420(Screen());
        using var image = ImageCodec.Encode(format, frame, Width, Height, quality: 80);
        var cut = image.Data[..(image.Length / 2)].ToArray();
        var decoded = new byte[frame.Length];
        try
        {
            // libjpeg fills in what's missing (TimeSnapper's archive may well have such files); libwebp refuses.
            var (_, width, height) = ImageCodec.Identify(cut);
            ImageCodec.DecodeI420(cut, width, height, decoded);
        }
        catch (InvalidDataException)
        {
        }
    }

    [Fact]
    public void AnImageOfAnotherSizeIsRefused()
    {
        var frame = ToI420(Screen());
        using var image = ImageCodec.Encode(FrameFormat.WebP, frame, Width, Height, quality: 50);
        var small = new byte[I420.Size(Width - 1, Height)];
        Assert.Throws<InvalidDataException>(() => ImageCodec.DecodeI420(image.Data, Width - 1, Height, small));
    }

    /// <summary>A screen-like picture: a light background with lines of dark "text", and a row of colour patches.</summary>
    private static byte[] Screen()
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = (y * Width + x) * 4;
            byte r = 245, g = 245, b = 240;
            if (y < 100 && y % 12 is >= 4 and < 10 && (x * 7 + y * 3) % 11 < 4)
                (r, g, b) = (30, 30, 35);
            var patch = x / PatchSize;
            if (y >= PatchTop && y < PatchTop + PatchSize && patch < Colours.Length)
                (r, g, b) = Colours[patch];
            (bgra[p], bgra[p + 1], bgra[p + 2], bgra[p + 3]) = (b, g, r, 255);
        }
        return bgra;
    }

    private static byte[] ToI420(byte[] bgra)
    {
        var frame = new byte[I420.Size(Width, Height)];
        I420.FromBgraRows(bgra, Width * 4, Width, Height, 0, Height, frame);
        return frame;
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        var mse = sum / a.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
