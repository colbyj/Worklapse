using SkiaSharp;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.Export;
using Worklapse.Video;

namespace Worklapse.App.Export;

/// <summary>
/// Export Frame: saves what a capture shows (as Day review shows it, without the zoom) as a PNG or JPEG image, at the
/// pictures' own resolution. Written to a temporary file beside the target and moved over it at the end, as the
/// timelapse is.
/// </summary>
public static class FrameImageExporter
{
    /// <summary>JPEG's quality: high, since the picture was already compressed once when recorded.</summary>
    private const int JpegQuality = 92;

    /// <summary>JPEG when <paramref name="path"/> ends in .jpg or .jpeg; otherwise PNG.</summary>
    public static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

    public static Task SaveAsync(string dataFolder, TimelapseFrame frame, string path) =>
        Task.Run(() => Save(dataFolder, frame, path));

    private static void Save(string dataFolder, TimelapseFrame frame, string path)
    {
        var temporary = path + ".tmp";
        using var provider = new FrameProvider(dataFolder);
        var pictures = new Dictionary<long, DecodedFrame>();
        try
        {
            foreach (var tile in frame.Tiles)
                if (tile.Frame is { } reference)
                    pictures[tile.Monitor.Id] = provider.Get(reference);

            var (width, height) = TimelapseComposer.FullSize(frame, pictures);
            using (var composer = new TimelapseComposer(width, height, clock: false))
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                composer.Save(frame, pictures, file, IsJpeg(path) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, JpegQuality);
            try
            {
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Couldn't replace {Path.GetFileName(path)}; it may be open in another program.", ex);
            }
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Couldn't delete an unfinished frame image", ex);
            }
            throw;
        }
        finally
        {
            foreach (var picture in pictures.Values)
                picture.Release();
        }
    }
}
