using Worklapse.Video;

namespace Worklapse.App.Playback;

/// <summary>
/// Decoded frames for a request, by track name, and the first error if a frame couldn't be decoded. The receiver must
/// call <see cref="Release"/> once.
/// </summary>
public sealed record IvfFrameResult(int Version, IReadOnlyDictionary<string, DecodedFrame> Frames, Exception? Error)
{
    public void Release()
    {
        foreach (var frame in Frames.Values)
            frame.Release();
    }
}

/// <summary>
/// Decodes frames of video files opened on their own on a background thread, one <see cref="IvfFrameSource"/> (one
/// decoder) per monitor. As with <see cref="FrameRenderer"/>, only the latest request matters, and decoders left unused
/// for a while are closed.
/// </summary>
public sealed class IvfRenderer : IDisposable
{
    private static readonly TimeSpan DecoderIdleTime = TimeSpan.FromSeconds(15);

    /// <summary>Recent frames kept per monitor; half of Day review's whole budget, since there's one per monitor.</summary>
    private const long CacheBudgetBytes = 32L * 1024 * 1024;

    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _gate = new();
    private (int Version, IReadOnlyList<(string Track, IvfFrame Frame)> Frames)? _pending;
    private volatile bool _stopping;

    public IvfRenderer()
    {
        _thread = new Thread(Run) { Name = "Worklapse video file decoder", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Raised on the decoder thread; the handler takes ownership of the result.</summary>
    public event Action<IvfFrameResult>? Rendered;

    public void Request(int version, IReadOnlyList<(string Track, IvfFrame Frame)> frames)
    {
        lock (_gate)
            _pending = (version, frames);
        _signal.Set();
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _thread.Join();
        _signal.Dispose();
    }

    private void Run()
    {
        var sources = new Dictionary<string, IvfFrameSource>();
        try
        {
            while (true)
            {
                if (!_signal.WaitOne(DecoderIdleTime / 3))
                {
                    foreach (var source in sources.Values)
                        source.CloseIdle(DecoderIdleTime);
                    continue;
                }
                if (_stopping)
                    return;

                (int Version, IReadOnlyList<(string Track, IvfFrame Frame)> Frames)? request;
                lock (_gate)
                {
                    request = _pending;
                    _pending = null;
                }
                if (request is not { } pending)
                    continue;

                var frames = new Dictionary<string, DecodedFrame>();
                Exception? error = null;
                foreach (var (track, frame) in pending.Frames)
                {
                    if (!sources.TryGetValue(track, out var source))
                        sources[track] = source = new IvfFrameSource(CacheBudgetBytes);
                    try
                    {
                        frames[track] = source.Get(frame);
                    }
                    catch (Exception ex)
                    {
                        error ??= ex;
                    }
                }

                var result = new IvfFrameResult(pending.Version, frames, error);
                if (_stopping)
                {
                    result.Release();
                    return;
                }
                if (Rendered is { } rendered)
                    rendered(result);
                else
                    result.Release();
            }
        }
        finally
        {
            foreach (var source in sources.Values)
                source.Dispose();
        }
    }
}
