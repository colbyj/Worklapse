using Worklapse.Core.Platform;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Recording;

public enum RecorderState
{
    Starting,
    Recording,
    Paused,
    Idle,
    Locked,
    Error,
}

public sealed record RecorderStatus(
    RecorderState State,
    DateTimeOffset? LastCapture,
    int MonitorsCaptured,
    string? Error);

/// <summary>
/// Takes a capture every interval: records what has focus, and encodes the screens chosen in settings.
/// Runs its loop on a background task; all database and encoder work happens on that loop.
/// </summary>
public sealed class Recorder : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly PlatformServices _platform;
    private readonly Func<RecordingStore, IFrameSink> _createSink;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<long, (FrameRef Frame, PixelRect? Mask)> _lastFrames = [];
    private readonly Dictionary<long, byte[]> _maskBuffers = [];

    private WorklapseDatabase? _db;
    private RecordingStore? _store;
    private IFrameSink? _sink;
    private Task? _loop;
    private volatile bool _paused;
    private bool _wasIdle;
    private RecorderStatus _status = new(RecorderState.Starting, null, 0, null);

    public Recorder(SettingsStore settings, PlatformServices platform, Func<RecordingStore, IFrameSink> createSink, TimeProvider? time = null)
    {
        _settings = settings;
        _platform = platform;
        _createSink = createSink;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on the recorder's background thread.</summary>
    public event EventHandler<RecorderStatus>? StatusChanged;

    public RecorderStatus Status => Volatile.Read(ref _status);

    public bool IsPaused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
                SetStatus(Status with { State = RecorderState.Paused });
        }
    }

    public void Start()
    {
        if (_loop is not null)
            throw new InvalidOperationException("The recorder is already running.");
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        CloseStorage();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.Current.IntervalSeconds), _time);
        do
        {
            var interval = TimeSpan.FromSeconds(_settings.Current.IntervalSeconds);
            if (timer.Period != interval)
                timer.Period = interval;

            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // One bad capture (a display being reconfigured, a full disk) must not end recording;
                // close everything so the next tick starts from a clean state, and report the error.
                CloseStorage();
                SetStatus(Status with { State = RecorderState.Error, Error = ex.Message });
            }
        }
        while (await timer.WaitForNextTickAsync(cancel));
    }

    private void Tick()
    {
        var settings = _settings.Current;

        if (_paused)
        {
            CloseSegments();
            SetStatus(Status with { State = RecorderState.Paused, Error = null });
            return;
        }

        if (settings.PauseWhenLocked && _platform.Session.IsLocked)
        {
            CloseSegments();
            SetStatus(Status with { State = RecorderState.Locked, Error = null });
            return;
        }

        var store = EnsureStorage(settings.DataFolder);
        var now = _time.GetLocalNow();
        var idleFor = _platform.Idle.GetIdleTime();

        if (idleFor >= TimeSpan.FromMinutes(settings.IdleMinutes))
        {
            using var transaction = store.BeginTransaction();
            if (!_wasIdle)
            {
                // The captures since the last input were idle too; they only looked active because the
                // threshold had not passed yet.
                MarkIdleSince(store, now - idleFor);
                _wasIdle = true;
            }
            store.InsertCapture(now, CaptureKind.Idle, null, null, null);
            transaction.Commit();
            SetStatus(Status with { State = RecorderState.Idle, Error = null });
            return;
        }
        _wasIdle = false;

        var foreground = _platform.Foreground.GetForegroundWindow();
        var monitors = _platform.Screen.GetMonitors();
        var targets = ChooseMonitors(settings, monitors, foreground);
        var captured = 0;

        using (var transaction = store.BeginTransaction())
        {
            long? appId = foreground is null ? null : store.GetOrAddApp(foreground.ProcessName, foreground.ProcessPath);
            var captureId = store.InsertCapture(now, CaptureKind.Active, appId, foreground?.Title, foreground?.Bounds);

            foreach (var monitor in targets)
            {
                var monitorId = store.GetOrAddMonitor(monitor);
                var capture = _platform.Screen.Capture(monitor);
                if (capture is null)
                    continue;

                PixelRect? mask = settings.CaptureMode == CaptureMode.ActiveApplication && foreground is not null
                    ? foreground.Bounds.Offset(-monitor.Bounds.X, -monitor.Bounds.Y)
                    : null;

                FrameRef frame;
                if (!capture.Changed && _lastFrames.TryGetValue(monitorId, out var last) && last.Mask == mask)
                {
                    frame = last.Frame;
                }
                else
                {
                    if (mask is { } keep)
                        capture = MaskedCopy(monitorId, capture, keep);
                    frame = _sink!.Append(monitorId, capture, now);
                    _lastFrames[monitorId] = (frame, mask);
                }

                store.InsertFrame(captureId, monitorId, frame);
                captured++;
            }

            transaction.Commit();
        }
        _sink!.Committed();

        SetStatus(new RecorderStatus(RecorderState.Recording, now, captured, null));
    }

    internal static IReadOnlyList<MonitorInfo> ChooseMonitors(
        WorklapseSettings settings, IReadOnlyList<MonitorInfo> monitors, ForegroundWindow? foreground)
    {
        switch (settings.CaptureMode)
        {
            case CaptureMode.AllMonitors:
                return monitors;

            case CaptureMode.PrimaryMonitor:
                return monitors.Where(m => m.IsPrimary).Take(1).ToList();

            case CaptureMode.SelectedMonitors:
                return monitors.Where(m => settings.SelectedMonitors.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase)).ToList();

            case CaptureMode.ActiveApplication:
                if (foreground is null)
                    return [];
                // The monitor showing the largest part of the focused window.
                var best = monitors
                    .Select(m => (Monitor: m, Area: (long)m.Bounds.Intersect(foreground.Bounds).Width * m.Bounds.Intersect(foreground.Bounds).Height))
                    .Where(x => x.Area > 0)
                    .OrderByDescending(x => x.Area)
                    .Select(x => x.Monitor)
                    .FirstOrDefault();
                return best is null ? [] : [best];

            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.CaptureMode, "Unknown capture mode.");
        }
    }

    /// <summary>
    /// Copies the capture with everything outside the focused window blacked out. The source buffer is
    /// left alone: a later unchanged capture of this monitor may need its pixels with a different mask.
    /// </summary>
    private ScreenCapture MaskedCopy(long monitorId, ScreenCapture capture, PixelRect keep)
    {
        if (!_maskBuffers.TryGetValue(monitorId, out var buffer) || buffer.Length != capture.Pixels.Length)
            _maskBuffers[monitorId] = buffer = new byte[capture.Pixels.Length];
        capture.Pixels.Span.CopyTo(buffer);

        var copy = new ScreenCapture
        {
            Monitor = capture.Monitor,
            Width = capture.Width,
            Height = capture.Height,
            Stride = capture.Stride,
            Pixels = buffer,
            Changed = capture.Changed,
        };
        BlackOutsideOf(copy, keep);
        return copy;
    }

    /// <summary>Blacks out everything outside <paramref name="keep"/> (monitor-relative) in place.</summary>
    internal static void BlackOutsideOf(ScreenCapture capture, PixelRect keep)
    {
        keep = keep.Intersect(new PixelRect(0, 0, capture.Width, capture.Height));
        var pixels = capture.Pixels.Span;
        for (var y = 0; y < capture.Height; y++)
        {
            var row = pixels.Slice(y * capture.Stride, capture.Width * 4);
            if (keep.IsEmpty || y < keep.Y || y >= keep.Bottom)
            {
                row.Clear();
                continue;
            }
            row[..(keep.X * 4)].Clear();
            row[(keep.Right * 4)..].Clear();
        }
    }

    private static void MarkIdleSince(RecordingStore store, DateTimeOffset since)
    {
        using var update = store.Database.Command("UPDATE captures SET kind = $idle WHERE ts_ms >= $since AND kind = $active;");
        update.Parameters.AddWithValue("$idle", (int)CaptureKind.Idle);
        update.Parameters.AddWithValue("$active", (int)CaptureKind.Active);
        update.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        update.ExecuteNonQuery();
    }

    private RecordingStore EnsureStorage(string dataFolder)
    {
        if (_db is not null && !string.Equals(_db.DataFolder, dataFolder, StringComparison.OrdinalIgnoreCase))
            CloseStorage();

        if (_store is null)
        {
            _db = WorklapseDatabase.Open(dataFolder);
            _store = new RecordingStore(_db);
            _sink = _createSink(_store);
        }
        return _store;
    }

    private void CloseSegments()
    {
        _sink?.CloseAll();
        _lastFrames.Clear();
    }

    private void CloseStorage()
    {
        _lastFrames.Clear();
        _sink?.Dispose();
        _db?.Dispose();
        _sink = null;
        _store = null;
        _db = null;
    }

    private void SetStatus(RecorderStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
