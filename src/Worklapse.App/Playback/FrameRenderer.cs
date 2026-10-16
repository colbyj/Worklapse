using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.App.Playback;

public sealed record FrameRequest(int Version, IReadOnlyList<(long MonitorId, FrameRef Frame)> Frames);

public sealed record FrameResult(int Version, IReadOnlyDictionary<long, DecodedFrame> Frames, Exception? Error);

/// <summary>
/// Decodes frames for playback on a background thread. Only the most recent request matters: requests
/// that arrive while one is being decoded replace each other, so holding an arrow key or playing fast never
/// builds a backlog.
/// </summary>
public sealed class FrameRenderer : IDisposable
{
    private readonly string _dataFolder;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _gate = new();
    private FrameRequest? _pending;
    private volatile bool _stopping;

    public FrameRenderer(string dataFolder)
    {
        _dataFolder = dataFolder;
        _thread = new Thread(Run) { Name = "Worklapse playback decoder", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Raised on the decoder thread with the frames of a request.</summary>
    public event Action<FrameResult>? Rendered;

    public void Request(FrameRequest request)
    {
        lock (_gate)
            _pending = request;
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
        // The provider's database connection belongs to this thread.
        using var provider = new FrameProvider(_dataFolder);
        while (true)
        {
            _signal.WaitOne();
            if (_stopping)
                return;

            FrameRequest? request;
            lock (_gate)
            {
                request = _pending;
                _pending = null;
            }
            if (request is null)
                continue;

            var frames = new Dictionary<long, DecodedFrame>();
            Exception? error = null;
            foreach (var (monitorId, frame) in request.Frames)
            {
                try
                {
                    frames[monitorId] = provider.Get(frame);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                if (_stopping)
                    return;
            }
            Rendered?.Invoke(new FrameResult(request.Version, frames, error));
        }
    }
}
