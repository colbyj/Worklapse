using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>The recording preview in Settings: the part of the screen shown, and that part as stored.</summary>
public class StoragePreviewTests
{
    private const int Width = 640, Height = 360;

    // A flat screen with one patch of fine detail ("text") at 400,200.
    private static readonly PixelRect Text = new(400, 200, 160, 96);

    [Fact]
    public void ThePreviewShowsThePartOfTheScreenWithTheMostDetail()
    {
        var region = StoragePreview.MostDetailed(Screen(), Width, Height, 160, 96);
        Assert.Equal((Text.Width, Text.Height), (region.Width, region.Height));
        Assert.InRange(region.X, Text.X - 16, Text.X + 16);
        Assert.InRange(region.Y, Text.Y - 16, Text.Y + 16);
        Assert.Equal(0, region.X % 2);
        Assert.Equal(0, region.Y % 2);
    }

    [Fact]
    public void APreviewLargerThanTheScreenShowsAllOfIt()
    {
        Assert.Equal(new PixelRect(0, 0, Width, Height), StoragePreview.MostDetailed(Screen(), Width, Height, 2000, 1000));
    }

    [Fact]
    public void ACropHasThePixelsOfThatPart()
    {
        var screen = Screen();
        var part = new PixelRect(10, 4, 31, 17); // odd size
        var crop = I420.Crop(screen, Width, Height, part);

        Assert.Equal(I420.Size(part.Width, part.Height), crop.Length);
        for (var y = 0; y < part.Height; y++)
        for (var x = 0; x < part.Width; x++)
            Assert.Equal(screen[(part.Y + y) * Width + part.X + x], crop[y * part.Width + x]);
        var chroma = Width * Height;
        var cropChroma = part.Width * part.Height;
        Assert.Equal(screen[chroma + (part.Y / 2) * I420.ChromaWidth(Width) + part.X / 2], crop[cropChroma]);
        Assert.Throws<ArgumentOutOfRangeException>(() => I420.Crop(screen, Width, Height, new PixelRect(1, 0, 10, 10)));
    }

    [Theory]
    [InlineData(FrameFormat.Jpeg, 15)]
    [InlineData(FrameFormat.WebP, 20)]
    public void AnImagePreviewIsThePartAsStoredAndTheSizeOfTheWholeFrame(FrameFormat format, int quality)
    {
        var screen = Screen();
        var decoded = Array.Empty<byte>();
        var (bytes, region) = StoragePreview.Image(screen, Width, Height, Text, format, quality, ref decoded);

        using (var image = ImageCodec.Encode(format, screen, Width, Height, quality))
            Assert.Equal(image.Length, bytes);
        var stored = new byte[I420.Size(Width, Height)];
        using (var image = ImageCodec.Encode(format, screen, Width, Height, quality))
            ImageCodec.DecodeI420(image.Data, Width, Height, stored);
        Assert.Equal(I420.Crop(stored, Width, Height, Text), region);
    }

    [Fact]
    public void AVideoPreviewIsAKeyFrameOfThePart()
    {
        var part = I420.Crop(Screen(), Width, Height, Text);
        var region = StoragePreview.VideoKeyFrame(part, Text.Width, Text.Height, quality: 20);
        Assert.Equal(part.Length, region.Length);
        double sum = 0;
        for (var i = 0; i < part.Length; i++)
            sum += (part[i] - region[i]) * (part[i] - region[i]);
        Assert.True(10 * Math.Log10(255.0 * 255.0 / (sum / part.Length)) > 30);
    }

    [Theory]
    [InlineData(FrameFormat.Jpeg)]
    [InlineData(FrameFormat.WebP)]
    public void AnImageEstimateCountsAnImagePerChangedCaptureAndNothingForAnUnchangedOne(FrameFormat format)
    {
        var screen = Screen();
        using var estimate = new StorageEstimate(format, 50);
        var first = estimate.Add(screen, Width, Height, changed: true, 0);
        Assert.Equal(0, estimate.Add(screen, Width, Height, changed: false, 10_000));
        Assert.Equal(first, estimate.Add(screen, Width, Height, changed: true, 20_000));
        Assert.Equal(3, estimate.Captures);
        Assert.Equal(2 * first, estimate.Bytes);
    }

    [Fact]
    public void VideosAverageFallsAsLaterFramesStoreOnlyWhatChanged()
    {
        // A screen where one small thing changes each time (a clock, a cursor).
        var screen = Screen();
        using var estimate = new StorageEstimate(FrameFormat.Video, 40);
        var sizes = new List<int>();
        var averages = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            screen[(10 + i) * Width + 10] ^= 0xFF;
            sizes.Add(estimate.Add(screen, Width, Height, changed: true, i * 10_000));
            averages.Add(estimate.AveragePerCapture);
        }
        Assert.All(sizes.Skip(1), size => Assert.True(size < sizes[0] / 4, $"{size} vs key frame {sizes[0]}"));
        Assert.Equal(averages.OrderDescending(), averages);
    }

    [Fact]
    public void AVideoEstimateStartsANewSegmentWhenTheScreenSizeChanges()
    {
        var screen = Screen();
        var smaller = I420.Crop(screen, Width, Height, new PixelRect(0, 0, 320, 180));
        using var estimate = new StorageEstimate(FrameFormat.Video, 40);
        var key = estimate.Add(screen, Width, Height, changed: true, 0);
        var later = estimate.Add(screen, Width, Height, changed: true, 10_000);
        var newKey = estimate.Add(smaller, 320, 180, changed: true, 20_000);
        Assert.True(later < key / 4);
        Assert.True(newKey > later, $"{newKey} vs {later}");
    }

    private static byte[] Screen()
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = (y * Width + x) * 4;
            var text = x >= Text.X && x < Text.Right && y >= Text.Y && y < Text.Bottom && (x * 7 + y * 3) % 5 < 2;
            var value = (byte)(text ? 20 : 235);
            (bgra[p], bgra[p + 1], bgra[p + 2], bgra[p + 3]) = (value, value, value, 255);
        }
        var frame = new byte[I420.Size(Width, Height)];
        I420.FromBgraRows(bgra, Width * 4, Width, Height, 0, Height, frame);
        return frame;
    }
}
