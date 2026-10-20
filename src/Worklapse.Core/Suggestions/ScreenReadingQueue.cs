using System.Collections.Concurrent;
using Worklapse.Core.Diagnostics;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Suggestions;

/// <summary>
/// Reads the text in captures' active windows one at a time on a low-priority thread, and keeps the words
/// (<see cref="ScreenWordStore"/>, which checks reading is allowed). The recorder hands it a copy of the window's pixels
/// once the capture is committed, or, when the screen didn't change, the capture to copy the words of. When it falls behind
/// it drops new work rather than holding pictures: a capture without words is only guessed from its title.
/// </summary>
public sealed class ScreenReadingQueue : IDisposable
{
    /// <summary>Pictures waiting at most; a 3440×1440 window is 5 MB of grayscale.</summary>
    public const int MaxWaiting = 4;

    /// <summary>Windows smaller than this either way hold no text worth reading.</summary>
    public const int MinSide = 16;

    private readonly IScreenTextReader _reader;
    private readonly BlockingCollection<Job> _jobs = new(MaxWaiting);
    private readonly Thread _thread;
    private bool _failed;

    public ScreenReadingQueue(IScreenTextReader reader, string dataFolder)
    {
        _reader = reader;
        _thread = new Thread(() => Run(dataFolder)) { Name = "Worklapse screen reading", IsBackground = true, Priority = ThreadPriority.Lowest };
        _thread.Start();
    }

    /// <summary>Queues reading a capture's window, whose grayscale pixels <see cref="CropLuma"/> copied.</summary>
    /// <returns>False when too much is waiting already: the capture gets no words.</returns>
    public bool Read(long captureId, byte[] gray, int width, int height) => _jobs.TryAdd(new Job(captureId, gray, width, height, null));

    /// <summary>Queues giving a capture the words of an earlier one showing the same pixels.</summary>
    public bool Copy(long fromCaptureId, long toCaptureId) => _jobs.TryAdd(new Job(toCaptureId, null, 0, 0, fromCaptureId));

    /// <summary>
    /// The luma (grayscale) pixels of <paramref name="rect"/> in a packed I420 frame (<see cref="Imaging.I420"/>), clipped to
    /// the frame; null when what's left is smaller than <see cref="MinSide"/>. A copy: the frame's buffer is reused.
    /// </summary>
    public static (byte[] Gray, int Width, int Height)? CropLuma(ReadOnlySpan<byte> i420, int frameWidth, int frameHeight, PixelRect rect)
    {
        var clip = rect.Intersect(new PixelRect(0, 0, frameWidth, frameHeight));
        if (clip.Width < MinSide || clip.Height < MinSide)
            return null;
        var gray = new byte[clip.Width * clip.Height];
        for (var y = 0; y < clip.Height; y++)
            i420.Slice((clip.Y + y) * frameWidth + clip.X, clip.Width).CopyTo(gray.AsSpan(y * clip.Width, clip.Width));
        return (gray, clip.Width, clip.Height);
    }

    /// <summary>Finishes what's queued, then stops.</summary>
    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join();
        _jobs.Dispose();
    }

    private void Run(string dataFolder)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        var store = new ScreenWordStore(db);
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try
            {
                if (job.CopyFrom is { } from)
                    store.Copy(from, job.CaptureId);
                else if (store.MayRead(job.CaptureId))
                    store.Set(job.CaptureId, ScreenWords.Of(_reader.Read(job.Gray!, job.Width, job.Height)));
            }
            catch (Exception ex)
            {
                // Suggestions do without those words; recording goes on. Logged once, not for every capture it fails for.
                if (!_failed)
                    Log.Error("Reading the screen failed; captures where it fails go without screen words.", ex);
                _failed = true;
            }
        }
    }

    private sealed record Job(long CaptureId, byte[]? Gray, int Width, int Height, long? CopyFrom);
}
