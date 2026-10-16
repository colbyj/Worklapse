using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Video;

namespace Worklapse.Tests;

public class I420Tests
{
    [Fact]
    public void ConvertingInStripsMatchesConvertingTheWholeFrame()
    {
        // Odd sizes exercise the repeated last row and column.
        const int width = 37, height = 23;
        var bgra = new byte[width * height * 4];
        new Random(5).NextBytes(bgra);

        var whole = new byte[I420.Size(width, height)];
        I420.FromBgraRows(bgra, width * 4, width, height, 0, height, whole);

        var strips = new byte[I420.Size(width, height)];
        for (var y = 0; y < height; y += 6)
        {
            var rows = Math.Min(6, height - y);
            I420.FromBgraRows(bgra.AsSpan(y * width * 4), width * 4, width, height, y, rows, strips);
        }

        Assert.Equal(whole, strips);
    }

    [Theory]
    [InlineData(255, 255, 255, 255, 128, 128)]  // white
    [InlineData(0, 0, 0, 0, 128, 128)]          // black
    [InlineData(255, 0, 0, 54, 99, 255)]        // red: BT.709 full range
    [InlineData(0, 0, 255, 19, 255, 116)]       // blue
    public void KnownColoursConvertAsExpected(byte r, byte g, byte b, byte y, byte u, byte v)
    {
        var bgra = new byte[2 * 2 * 4];
        for (var i = 0; i < 4; i++)
            (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2]) = (b, g, r);
        var frame = new byte[I420.Size(2, 2)];
        I420.FromBgraRows(bgra, 8, 2, 2, 0, 2, frame);
        Assert.Equal(y, frame[0]);
        Assert.InRange(frame[4], u - 1, u + 1);
        Assert.InRange(frame[5], v - 1, v + 1);
    }

    [Fact]
    public void NativeConversionBackToBgraIsClose()
    {
        const int width = 64, height = 32;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (90, 160, 210, 255); // a flat colour survives subsampling
        var frame = new byte[I420.Size(width, height)];
        I420.FromBgraRows(bgra, width * 4, width, height, 0, height, frame);

        var back = new byte[bgra.Length];
        ColorConversion.I420ToBgra(frame, width, height, back, width * 4);
        for (var i = 0; i < bgra.Length; i++)
            Assert.InRange(back[i], bgra[i] - 3, bgra[i] + 3);
    }

    [Fact]
    public void BlackOutsideOfKeepsOnlyTheWindow()
    {
        const int width = 8, height = 6;
        var frame = Enumerable.Repeat((byte)200, I420.Size(width, height)).ToArray();

        I420.BlackOutsideOf(frame, width, height, new PixelRect(2, 2, 4, 2));

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Assert.Equal(x is >= 2 and < 6 && y is >= 2 and < 4 ? 200 : 0, frame[y * width + x]);

        // Chroma: kept for blocks (1..2, 1) of the 4×3 chroma planes, neutral grey (128) elsewhere.
        var chroma = frame.AsSpan(width * height);
        for (var plane = 0; plane < 2; plane++)
        for (var cy = 0; cy < 3; cy++)
        for (var cx = 0; cx < 4; cx++)
            Assert.Equal(cx is 1 or 2 && cy == 1 ? 200 : 128, chroma[plane * 12 + cy * 4 + cx]);
    }
}
