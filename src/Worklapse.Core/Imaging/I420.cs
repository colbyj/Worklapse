using Worklapse.Core.Platform;

namespace Worklapse.Core.Imaging;

/// <summary>
/// Packed I420 frames: the Y plane (width × height), then U, then V (each ceil(width/2) × ceil(height/2)),
/// rows packed. BT.709 full range, the format captures are staged in and the AV1 encoder takes. At 1.5
/// bytes per pixel it is less than half the size of BGRA.
/// </summary>
public static class I420
{
    public static int ChromaWidth(int width) => (width + 1) / 2;

    public static int ChromaHeight(int height) => (height + 1) / 2;

    public static int Size(int width, int height) => width * height + 2 * ChromaWidth(width) * ChromaHeight(height);

    /// <summary>
    /// Converts BGRA rows <paramref name="firstRow"/> .. firstRow + rows − 1 of a frame into its packed I420
    /// buffer. Captures are converted a strip at a time; <paramref name="firstRow"/> must be even so each
    /// strip covers whole chroma rows (only the frame's last strip may have an odd row count).
    /// The integer coefficients match the native library's, so either side can convert.
    /// </summary>
    public static unsafe void FromBgraRows(ReadOnlySpan<byte> bgra, int stride, int width, int height, int firstRow, int rows, Span<byte> i420)
    {
        if (firstRow % 2 != 0)
            throw new ArgumentException("Strips must start on an even row.", nameof(firstRow));
        if (firstRow + rows > height || rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (i420.Length < Size(width, height) || bgra.Length < (long)stride * (rows - 1) + width * 4)
            throw new ArgumentException("Buffer too small.");

        var chromaWidth = ChromaWidth(width);
        var uOffset = width * height;
        var vOffset = uOffset + chromaWidth * ChromaHeight(height);

        fixed (byte* src = bgra)
        fixed (byte* dst = i420)
        {
            for (var y = 0; y < rows; y++)
            {
                var row = src + (long)y * stride;
                var luma = dst + (long)(firstRow + y) * width;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    luma[x] = (byte)((54 * p[2] + 183 * p[1] + 19 * p[0] + 128) >> 8);
                }
            }

            // Chroma from the average of each 2×2 block; a frame's odd last row or column repeats.
            for (var cy = 0; cy < (rows + 1) / 2; cy++)
            {
                var r0 = src + (long)(cy * 2) * stride;
                var r1 = cy * 2 + 1 < rows ? r0 + stride : r0;
                var u = dst + uOffset + (long)(firstRow / 2 + cy) * chromaWidth;
                var v = dst + vOffset + (long)(firstRow / 2 + cy) * chromaWidth;
                for (var cx = 0; cx < chromaWidth; cx++)
                {
                    var x0 = cx * 2 * 4;
                    var x1 = (cx * 2 + 1 < width ? cx * 2 + 1 : cx * 2) * 4;
                    var b = r0[x0] + r0[x1] + r1[x0] + r1[x1];
                    var g = r0[x0 + 1] + r0[x1 + 1] + r1[x0 + 1] + r1[x1 + 1];
                    var r = r0[x0 + 2] + r0[x1 + 2] + r1[x0 + 2] + r1[x1 + 2];
                    u[cx] = Clamp(((-29 * r - 99 * g + 128 * b + 512) >> 10) + 128);
                    v[cx] = Clamp(((128 * r - 116 * g - 12 * b + 512) >> 10) + 128);
                }
            }
        }
    }

    /// <summary>
    /// Blacks out everything outside <paramref name="keep"/> (frame coordinates) in place. Chroma is masked
    /// per 2×2 block, keeping a block if any of its pixels is inside.
    /// </summary>
    public static void BlackOutsideOf(Span<byte> i420, int width, int height, PixelRect keep)
    {
        keep = keep.Intersect(new PixelRect(0, 0, width, height));
        var luma = i420[..(width * height)];
        for (var y = 0; y < height; y++)
        {
            var row = luma.Slice(y * width, width);
            if (keep.IsEmpty || y < keep.Y || y >= keep.Bottom)
            {
                row.Clear();
                continue;
            }
            row[..keep.X].Clear();
            row[keep.Right..].Clear();
        }

        var chromaWidth = ChromaWidth(width);
        var chromaHeight = ChromaHeight(height);
        var chroma = i420.Slice(width * height, 2 * chromaWidth * chromaHeight);
        var c = keep.IsEmpty ? default : new PixelRect(keep.X / 2, keep.Y / 2, (keep.Right + 1) / 2 - keep.X / 2, (keep.Bottom + 1) / 2 - keep.Y / 2);
        for (var plane = 0; plane < 2; plane++)
        {
            for (var y = 0; y < chromaHeight; y++)
            {
                var row = chroma.Slice((plane * chromaHeight + y) * chromaWidth, chromaWidth);
                if (c.IsEmpty || y < c.Y || y >= c.Bottom)
                {
                    row.Fill(128);
                    continue;
                }
                row[..c.X].Fill(128);
                row[c.Right..].Fill(128);
            }
        }
    }

    /// <summary>A copy of the part of a frame inside <paramref name="rect"/>, which starts on even coordinates.</summary>
    public static byte[] Crop(ReadOnlySpan<byte> i420, int width, int height, PixelRect rect)
    {
        if (rect.X % 2 != 0 || rect.Y % 2 != 0 || rect.X < 0 || rect.Y < 0 || rect.IsEmpty || rect.Right > width || rect.Bottom > height)
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "The part must lie inside the frame and start on even coordinates.");

        var crop = new byte[Size(rect.Width, rect.Height)];
        for (var y = 0; y < rect.Height; y++)
            i420.Slice((rect.Y + y) * width + rect.X, rect.Width).CopyTo(crop.AsSpan(y * rect.Width));

        int sourceWidth = ChromaWidth(width), sourceHeight = ChromaHeight(height);
        int cropWidth = ChromaWidth(rect.Width), cropHeight = ChromaHeight(rect.Height);
        for (var plane = 0; plane < 2; plane++)
        {
            var source = i420[(width * height + plane * sourceWidth * sourceHeight)..];
            var target = crop.AsSpan(rect.Width * rect.Height + plane * cropWidth * cropHeight);
            for (var y = 0; y < cropHeight; y++)
                source.Slice((rect.Y / 2 + y) * sourceWidth + rect.X / 2, cropWidth).CopyTo(target[(y * cropWidth)..]);
        }
        return crop;
    }

    /// <summary>The size of a frame shrunk by <paramref name="factor"/> (<see cref="Reduce"/>).</summary>
    public static (int Width, int Height) ReducedSize(int width, int height, int factor) =>
        ((width + factor - 1) / factor, (height + factor - 1) / factor);

    /// <summary>
    /// Shrinks a frame by a whole factor into <paramref name="reduced"/> (<see cref="ReducedSize"/>): each pixel is the
    /// average of the block it covers, smaller at the right and bottom edges. Scaling a frame far down in one step
    /// skips pixels; this keeps thin text and lines in the picture.
    /// </summary>
    public static void Reduce(ReadOnlySpan<byte> i420, int width, int height, int factor, Span<byte> reduced)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var (reducedWidth, reducedHeight) = ReducedSize(width, height, factor);
        if (i420.Length < Size(width, height) || reduced.Length < Size(reducedWidth, reducedHeight))
            throw new ArgumentException("Buffer too small.");

        // The chroma planes shrink by the same factor: ceil(ceil(w / 2) / f) = ceil(ceil(w / f) / 2).
        int chromaWidth = ChromaWidth(width), chromaHeight = ChromaHeight(height);
        int reducedChromaWidth = ChromaWidth(reducedWidth), reducedChromaHeight = ChromaHeight(reducedHeight);
        var luma = width * height;
        var chroma = chromaWidth * chromaHeight;
        var reducedLuma = reducedWidth * reducedHeight;
        var reducedChroma = reducedChromaWidth * reducedChromaHeight;
        ReducePlane(i420[..luma], width, height, factor, reduced[..reducedLuma]);
        ReducePlane(i420.Slice(luma, chroma), chromaWidth, chromaHeight, factor, reduced.Slice(reducedLuma, reducedChroma));
        ReducePlane(i420.Slice(luma + chroma, chroma), chromaWidth, chromaHeight, factor,
            reduced.Slice(reducedLuma + reducedChroma, reducedChroma));
    }

    private static void ReducePlane(ReadOnlySpan<byte> plane, int width, int height, int factor, Span<byte> target)
    {
        var (targetWidth, targetHeight) = ReducedSize(width, height, factor);
        var sums = new int[targetWidth];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            Array.Clear(sums);
            var rows = Math.Min(factor, height - ty * factor);
            for (var r = 0; r < rows; r++)
            {
                var row = plane.Slice((ty * factor + r) * width, width);
                for (var tx = 0; tx < targetWidth; tx++)
                {
                    var block = row.Slice(tx * factor, Math.Min(factor, width - tx * factor));
                    var sum = 0;
                    foreach (var value in block)
                        sum += value;
                    sums[tx] += sum;
                }
            }
            var targetRow = target.Slice(ty * targetWidth, targetWidth);
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var count = rows * Math.Min(factor, width - tx * factor);
                targetRow[tx] = (byte)((sums[tx] + count / 2) / count);
            }
        }
    }

    /// <summary>
    /// Converts a frame from full range to limited ("studio") range in place: luma 0–255 to 16–235, chroma 0–255 to
    /// 16–240 around 128. Video players assume limited range, so exported videos use it.
    /// </summary>
    public static void ToLimitedRange(Span<byte> i420, int width, int height)
    {
        var lumaSize = width * height;
        var luma = LimitedLuma.Value;
        foreach (ref var y in i420[..lumaSize])
            y = luma[y];
        var chroma = LimitedChroma.Value;
        foreach (ref var c in i420.Slice(lumaSize, 2 * ChromaWidth(width) * ChromaHeight(height)))
            c = chroma[c];
    }

    private static readonly Lazy<byte[]> LimitedLuma = new(() => RangeTable(16, 219));
    private static readonly Lazy<byte[]> LimitedChroma = new(() => RangeTable(16, 224));

    // value → low + value × span / 255, rounded. Chroma's 128 stays 128 (16 + 128 × 224 / 255 = 128.4).
    private static byte[] RangeTable(int low, int span)
    {
        var table = new byte[256];
        for (var value = 0; value < 256; value++)
            table[value] = (byte)(low + (value * span + 127) / 255);
        return table;
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
