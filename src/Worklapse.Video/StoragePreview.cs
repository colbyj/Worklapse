using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Video;

/// <summary>A screen as a recording setting would store it: for the preview in Settings.</summary>
public static class StoragePreview
{
    private const int Block = 16;

    /// <summary>
    /// The part of a frame of this size with the most detail (sharp edges, so usually text), where the effect of a
    /// setting shows most. Starts on even coordinates; the whole frame if that is smaller.
    /// </summary>
    public static PixelRect MostDetailed(ReadOnlySpan<byte> i420, int frameWidth, int frameHeight, int width, int height)
    {
        width = Math.Min(width, frameWidth) & ~1;
        height = Math.Min(height, frameHeight) & ~1;
        int blocksAcross = frameWidth / Block, blocksDown = frameHeight / Block;
        int windowAcross = Math.Max(1, width / Block), windowDown = Math.Max(1, height / Block);
        if (blocksAcross < windowAcross || blocksDown < windowDown)
            return new PixelRect(0, 0, width, height);

        // Detail per block (luma steps to the right and down), summed so any window's total is four lookups.
        var sums = new long[(blocksDown + 1) * (blocksAcross + 1)];
        long Sum(int bx, int by) => sums[by * (blocksAcross + 1) + bx];
        for (var by = 0; by < blocksDown; by++)
        for (var bx = 0; bx < blocksAcross; bx++)
        {
            long detail = 0;
            for (var y = by * Block; y < (by + 1) * Block && y + 1 < frameHeight; y++)
            {
                var row = i420.Slice(y * frameWidth, frameWidth);
                var below = i420.Slice((y + 1) * frameWidth, frameWidth);
                for (var x = bx * Block; x < (bx + 1) * Block && x + 1 < frameWidth; x++)
                    detail += Math.Abs(row[x + 1] - row[x]) + Math.Abs(below[x] - row[x]);
            }
            sums[(by + 1) * (blocksAcross + 1) + bx + 1] = detail + Sum(bx + 1, by) + Sum(bx, by + 1) - Sum(bx, by);
        }

        var best = (X: 0, Y: 0, Detail: -1L);
        for (var by = 0; by + windowDown <= blocksDown; by++)
        for (var bx = 0; bx + windowAcross <= blocksAcross; bx++)
        {
            var detail = Sum(bx + windowAcross, by + windowDown) - Sum(bx, by + windowDown) - Sum(bx + windowAcross, by) + Sum(bx, by);
            if (detail > best.Detail)
                best = (bx, by, detail);
        }
        var left = Math.Min(best.X * Block, frameWidth - width) & ~1;
        var top = Math.Min(best.Y * Block, frameHeight - height) & ~1;
        return new PixelRect(left, top, width, height);
    }

    /// <summary>
    /// The frame stored as an image: the file's size, and <paramref name="region"/> as read back from it.
    /// <paramref name="decoded"/> holds the whole frame read back; it is reused between calls.
    /// </summary>
    public static (int Bytes, byte[] Region) Image(ReadOnlySpan<byte> i420, int width, int height, PixelRect region,
        FrameFormat format, int quality, ref byte[] decoded)
    {
        using var image = ImageCodec.Encode(format, i420, width, height, quality);
        if (decoded.Length < I420.Size(width, height))
            decoded = GC.AllocateUninitializedArray<byte>(I420.Size(width, height));
        ImageCodec.DecodeI420(image.Data, width, height, decoded);
        return (image.Length, I420.Crop(decoded, width, height, region));
    }

    /// <summary>
    /// A part of a screen stored as video and read back: a segment's first frame (a key frame), made as the batch
    /// encoder makes it. Later frames are coded at the same quantizer from what changed, so they look alike, except
    /// that small text can lag behind. Only a part, since a whole screen takes the encoder seconds and hundreds of MB.
    /// </summary>
    public static byte[] VideoKeyFrame(ReadOnlySpan<byte> i420, int width, int height, int quality)
    {
        using var encoder = new Av1Encoder(width, height, quality, threads: 2);
        var frame = encoder.Encode(i420, 0);
        using var decoder = new Av1Decoder(threads: 1);
        decoder.Decode(frame.Data.Span);
        var decoded = new byte[I420.Size(width, height)];
        decoder.CopyI420(decoded);
        return decoded;
    }
}
