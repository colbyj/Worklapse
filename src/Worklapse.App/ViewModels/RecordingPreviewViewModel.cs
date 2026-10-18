using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Worklapse.Core.Imaging;
using Worklapse.Core.Storage;
using Worklapse.Video;
using PixelRect = Worklapse.Core.Platform.PixelRect;

namespace Worklapse.App.ViewModels;

/// <summary>
/// The preview in Settings, as in TimeSnapper. A picture: the most detailed part of the primary monitor, captured when
/// Settings opens, as the chosen format and quality would store it, at 1:1 so text can be judged. And a running
/// estimate of the space: while Settings is open the monitor is captured every interval and stored the way the
/// recorder would (<see cref="StorageEstimate"/>); its average per capture is shown. Changing the format, quality or
/// interval starts both over. The work runs on a thread of its own; only the latest change counts.
/// </summary>
public sealed partial class RecordingPreviewViewModel : ObservableObject, IDisposable
{
    private readonly PrimaryScreenSampler _sampler; // the worker's, which disposes it
    private readonly CapturedScreen _screen;         // when Settings opened: the picture shown
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private Thread? _thread;
    private PixelRect _region;
    private byte[] _regionI420 = [];
    private double _scaling = 1;
    private Setting? _pending;  // under _gate
    private Setting? _latest;   // UI thread
    private bool _disposed;     // UI thread
    private volatile bool _stopping;

    // The worker's.
    private byte[] _decoded = [];
    private StorageEstimate? _estimate;
    private CapturedScreen _last;

    private readonly record struct Setting(FrameFormat Format, int Quality, TimeSpan Interval);

    public RecordingPreviewViewModel(PrimaryScreenSampler sampler, CapturedScreen screen)
    {
        _sampler = sampler;
        _screen = _last = screen;
    }

    /// <summary>The region as stored and read back, one bitmap pixel per screen pixel.</summary>
    [ObservableProperty] private WriteableBitmap? _image;

    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Picks the region shown, the most detailed part of the screen that fills <paramref name="pixelWidth"/> ×
    /// <paramref name="pixelHeight"/> device pixels, and starts. Called by the view once it's laid out.
    /// </summary>
    public void Start(double scaling, int pixelWidth, int pixelHeight, FrameFormat format, int quality, TimeSpan interval)
    {
        if (_thread is not null || _disposed)
            return;
        _scaling = scaling;
        _region = StoragePreview.MostDetailed(_screen.I420, _screen.Width, _screen.Height, pixelWidth, pixelHeight);
        _regionI420 = I420.Crop(_screen.I420, _screen.Width, _screen.Height, _region);
        _thread = new Thread(Run) { Name = "Worklapse recording preview", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Request(format, quality, interval);
    }

    public void Request(FrameFormat format, int quality, TimeSpan interval)
    {
        var setting = new Setting(format, quality, interval);
        if (_thread is null || _disposed || _latest == setting)
            return;
        _latest = setting;
        IsBusy = true;
        lock (_gate)
            _pending = setting;
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_thread is null)
            _sampler.Dispose();
        else
        {
            // It finishes what it's doing (a video key frame can take a few seconds), then lets go of everything.
            _stopping = true;
            _wake.Set();
        }
        Image?.Dispose();
        Image = null;
    }

    private void Run()
    {
        try
        {
            Setting? setting = null;
            long next = 0; // Environment.TickCount64 of the next capture
            while (!_stopping)
            {
                var wait = setting is null ? Timeout.Infinite : (int)Math.Clamp(next - Environment.TickCount64, 0, int.MaxValue);
                _wake.WaitOne(wait);
                if (_stopping)
                    return;

                Setting? changed;
                lock (_gate)
                {
                    changed = _pending;
                    _pending = null;
                }
                try
                {
                    if (changed is { } fresh)
                    {
                        setting = fresh;
                        ShowPicture(fresh);
                        _estimate?.Dispose();
                        _estimate = null;
                        lock (_gate)
                        {
                            if (_pending is not null)
                                continue; // changed again meanwhile: don't spend a video key frame on this one
                        }
                        _estimate = new StorageEstimate(fresh.Format, fresh.Quality);
                        Sample(fresh, _last, changed: true);
                        next = Environment.TickCount64 + (long)fresh.Interval.TotalMilliseconds;
                    }
                    else if (setting is { } current && _estimate is not null && Environment.TickCount64 >= next)
                    {
                        next = Environment.TickCount64 + (long)current.Interval.TotalMilliseconds;
                        if (_sampler.Capture() is { } capture)
                        {
                            var screenChanged = capture.Hash != _last.Hash || capture.Width != _last.Width || capture.Height != _last.Height;
                            _last = capture;
                            Sample(current, capture, screenChanged);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Only the preview fails; recording goes on.
                    var shown = setting;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (shown == _latest && !_disposed)
                        {
                            SizeText = "No preview: " + ex.Message;
                            IsBusy = false;
                        }
                    });
                }
            }
        }
        finally
        {
            _estimate?.Dispose();
            _sampler.Dispose();
        }
    }

    private void ShowPicture(Setting setting)
    {
        var region = setting.Format == FrameFormat.Video
            ? StoragePreview.VideoKeyFrame(_regionI420, _region.Width, _region.Height, setting.Quality)
            : StoragePreview.Image(_screen.I420, _screen.Width, _screen.Height, _region, setting.Format, setting.Quality, ref _decoded).Region;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || setting != _latest)
                return; // a newer change is on its way
            var dpi = 96 * _scaling;
            var bitmap = new WriteableBitmap(new PixelSize(_region.Width, _region.Height), new Vector(dpi, dpi), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var buffer = bitmap.Lock())
            unsafe
            {
                ColorConversion.I420ToBgra(region, _region.Width, _region.Height,
                    new Span<byte>((void*)buffer.Address, buffer.RowBytes * _region.Height), buffer.RowBytes);
            }
            var old = Image;
            Image = bitmap;
            old?.Dispose();
        });
    }

    private void Sample(Setting setting, CapturedScreen capture, bool changed)
    {
        _estimate!.Add(capture.I420, capture.Width, capture.Height, changed, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var (average, captures) = (_estimate.AveragePerCapture, _estimate.Captures);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || setting != _latest)
                return;
            SizeText = $"Average {FormatSize(average)} per capture, over {captures} {(captures == 1 ? "capture" : "captures")} ({capture.Width} × {capture.Height})";
            IsBusy = false;
        });
    }

    private static string FormatSize(double bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes / (1024 * 1024):0.0} MB",
    };
}
