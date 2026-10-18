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

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
