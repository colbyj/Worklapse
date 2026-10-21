using System.Collections.Concurrent;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.Export;
using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.App.Export;

/// <summary>A timelapse to make: its frames, the video's size and speed, and whether to show the time.</summary>
/// <param name="Reload">
/// The frames planned again from the day as it is now. Reclaiming the space of deleted captures, and encoding the
/// segment being recorded, move frames; the plan then refers to frames that are gone.
/// </param>
public sealed record TimelapseJob(IReadOnlyList<TimelapseFrame> Frames, Func<IReadOnlyList<TimelapseFrame>> Reload,
    (int Width, int Height) Size, int FramesPerSecond, bool Clock);

/// <summary>
/// Makes a timelapse video: reads each frame's pictures and draws them (<see cref="TimelapseComposer"/>) on one thread,
/// while another encodes the frames drawn as AV1 and writes them to an MP4 file. The video is written to a temporary
/// file beside the target and moved over it at the end, so a cancelled or failed export leaves any earlier file as it
/// was.
/// </summary>
public static class TimelapseExporter
{
    /// <summary>
    /// The quantizer: lower than recording's (40), since a scaled-down screen has smaller text and every frame of a
    /// timelapse changes.
    /// </summary>
    public const int Quality = 32;

    /// <summary>Seconds of video between key frames, where players can seek to.</summary>
    private const int KeyFrameSeconds = 2;

    /// <summary>Frames drawn ahead of the encoder: enough to keep it busy, each a whole I420 frame.</summary>
    private const int FramesAhead = 2;

    /// <summary>
    /// libaom options: the frame split into up to 4 × 2 tiles (log2 counts) encoded side by side. Measured at
    /// 1920 × 776: about 95 ms a frame with 8 threads and no tiles, 57 ms with 12 threads and these, for 3% more data.
    /// </summary>
    private static readonly Dictionary<string, string> EncoderOptions = new() { ["tile-columns"] = "2", ["tile-rows"] = "1" };

    private static int EncoderThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 12);

    /// <param name="progress">Told the frames written so far, and of how many (which a new plan can change).</param>
    public static Task RunAsync(string dataFolder, TimelapseJob job, string path, IProgress<(int Done, int Total)> progress,
        CancellationToken cancel) =>
        Task.Factory.StartNew(() => Run(dataFolder, job, path, progress, cancel), cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Run(string dataFolder, TimelapseJob job, string path, IProgress<(int Done, int Total)> progress, CancellationToken cancel)
    {
        var temporary = path + ".tmp";
        try
        {
            var (width, height) = job.Size;
            var frameSize = I420.Size(width, height);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var drawn = new BlockingCollection<byte[]>(FramesAhead);
            using var free = new BlockingCollection<byte[]>();
            for (var i = 0; i < FramesAhead + 1; i++)
                free.Add(new byte[frameSize]);
            var total = job.Frames.Count;

            var encoding = Task.Factory.StartNew(() =>
            {
                try
                {
                    using var encoder = new Av1Encoder(width, height, Quality, threads: EncoderThreads, options: EncoderOptions,
                        keyFrameInterval: job.FramesPerSecond * KeyFrameSeconds, limitedRange: true);
                    using var writer = new Mp4Writer(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None),
                        width, height, job.FramesPerSecond);
                    foreach (var frame in drawn.GetConsumingEnumerable(stop.Token))
                    {
                        var ptsMs = (long)writer.FrameCount * 1000 / job.FramesPerSecond;
                        writer.WriteFrame(encoder.Encode(frame, ptsMs));
                        free.Add(frame);
                        progress.Report((writer.FrameCount, Math.Max(writer.FrameCount, Volatile.Read(ref total))));
                    }
                    if (writer.FrameCount == 0)
                        throw new InvalidOperationException("Nothing was left to export.");
                    writer.Finish();
                }
                catch
                {
                    stop.Cancel(); // the drawing waits on this thread; let it go
                    throw;
                }
            }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            try
            {
                Draw(dataFolder, job, drawn, free, newTotal => Volatile.Write(ref total, newTotal), stop.Token);
                drawn.CompleteAdding();
            }
            catch (Exception drawing)
            {
                stop.Cancel();
                Wait(encoding);
                // Stopped because encoding failed: that failure is the one to report.
                if (drawing is OperationCanceledException && !cancel.IsCancellationRequested && encoding.IsFaulted)
                    encoding.GetAwaiter().GetResult();
                throw;
            }
            encoding.GetAwaiter().GetResult();
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
                Log.Error("Couldn't delete an unfinished timelapse", ex);
            }
            throw;
        }
    }

    /// <summary>Draws each frame into a buffer from <paramref name="free"/> and hands it to the encoder through <paramref name="drawn"/>.</summary>
    /// <param name="replanned">Told how many frames there are in all after the frames were planned again.</param>
    private static void Draw(string dataFolder, TimelapseJob job, BlockingCollection<byte[]> drawn, BlockingCollection<byte[]> free,
        Action<int> replanned, CancellationToken cancel)
    {
        using var provider = new FrameProvider(dataFolder);
        using var composer = new TimelapseComposer(job.Size.Width, job.Size.Height, job.Clock);
        var frames = job.Frames;
        var pictures = new Dictionary<long, DecodedFrame>();
        FrameRef? reloadedFor = null;
        var done = 0;
        var index = 0;
        while (index < frames.Count)
        {
            cancel.ThrowIfCancellationRequested();
            var frame = frames[index];
            try
            {
                foreach (var tile in frame.Tiles)
                    if (tile.Frame is { } reference)
                        pictures[tile.Monitor.Id] = provider.Get(reference);
            }
            catch (FrameUnavailableException missing) when (reloadedFor != missing.Frame)
            {
                // Plan again and carry on from the same moment; once per missing frame, so a frame still missing after
                // that fails the export.
                Release(pictures);
                reloadedFor = missing.Frame;
                frames = job.Reload();
                index = IndexFrom(frames, frame.Time);
                replanned(done + frames.Count - index);
                continue;
            }
            catch
            {
                Release(pictures);
                throw;
            }

            var buffer = free.Take(cancel);
            try
            {
                composer.Compose(frame, pictures, buffer);
            }
            finally
            {
                Release(pictures);
            }
            drawn.Add(buffer, cancel);
            done++;
            index++;
        }
    }

    /// <summary>The first frame at or after <paramref name="time"/>; the count when there is none.</summary>
    private static int IndexFrom(IReadOnlyList<TimelapseFrame> frames, DateTimeOffset time)
    {
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Time >= time)
                return i;
        return frames.Count;
    }

    private static void Release(Dictionary<long, DecodedFrame> pictures)
    {
        foreach (var picture in pictures.Values)
            picture.Release();
        pictures.Clear();
    }

    /// <summary>Waits for a task that was told to stop, leaving its outcome to the error already being reported.</summary>
    private static void Wait(Task task)
    {
        try
        {
            task.Wait();
        }
        catch (AggregateException)
        {
        }
    }
}
