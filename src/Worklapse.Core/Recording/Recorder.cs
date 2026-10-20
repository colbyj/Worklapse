using Worklapse.Core.Diagnostics;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;

namespace Worklapse.Core.Recording;

public enum RecorderState
{
    Starting,
    Recording,
    Paused,
    Idle,
    Locked,

    /// <summary>An app or site excluded on the Applications page has the focus: nothing is recorded.</summary>
    Excluded,
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
    // Per monitor, the frame an unchanged capture can reuse: always one in its open segment.
    private readonly Dictionary<long, (FrameRef Frame, PixelRect? Mask, ulong Hash)> _lastFrames = [];
    // Per monitor, the last window read off it: a capture showing the same frame and window gets that capture's words.
    private readonly Dictionary<long, (FrameRef Frame, PixelRect Window, long CaptureId)> _lastRead = [];
    private HashSet<long> _attached = [];

    private WorklapseDatabase? _db;
    private RecordingStore? _store;
    private IFrameSink? _sink;
    private ScreenReadingQueue? _screenReading;
    private Task? _loop;
    private volatile bool _paused;
    private volatile bool _closeSegments;
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

    /// <summary>
    /// Finishes the segments being recorded at the next tick, before capturing, so they are encoded now.
    /// Called after captures in them were deleted: encoding leaves the deleted frames out, and until then
    /// they are still on disk in the staging files. Safe to call from any thread.
    /// </summary>
    public void CloseSegmentsSoon() => _closeSegments = true;

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
                SetError(ex.Message, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(cancel));
    }

    internal void Tick()
    {
        var settings = _settings.Current;

        // Opened even while paused or locked: the sink's batch encoder also removes deleted captures' video
        // from disk, which shouldn't wait for recording to resume.
        var store = EnsureStorage(settings.DataFolder);

        if (_closeSegments)
        {
            _closeSegments = false;
            CloseSegments();
        }

        // Said every tick, paused or not: nothing will be recorded until Worklapse runs where it can record.
        if (_platform.CannotRecord is { } reason)
        {
            SetError(reason);
            return;
        }

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

        // Only possible with a settings file from another desktop; Settings doesn't offer the mode here.
        if (settings.CaptureMode == CaptureMode.PrimaryMonitor && !_platform.KnowsPrimaryMonitor)
        {
            SetError("This desktop doesn't say which monitor is primary: choose another capture mode in Settings.");
            return;
        }

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
            var idleLayout = RecordLayout(store, _platform.Screen.GetMonitors());
            store.InsertCapture(now, CaptureKind.Idle, null, null, null, layoutId: idleLayout);
            transaction.Commit();
            _sink!.Committed();
            SetStatus(Status with { State = RecorderState.Idle, Error = null });
            return;
        }
        _wasIdle = false;

        var foreground = _platform.Foreground.GetForegroundWindow();
        if (foreground is not null && store.IsExcluded(foreground.ProcessName, foreground.Site))
        {
            // Nothing at all, not even that the time passed: it shows as not recorded. Unlike a pause the segments
            // stay open, so a moment in an excluded app doesn't end them.
            SetStatus(Status with { State = RecorderState.Excluded, Error = null });
            return;
        }

        var monitors = _platform.Screen.GetMonitors();
        var targets = ChooseMonitors(settings, monitors, foreground);
        var captured = 0;
        // The monitor the focused window is mostly on, whose picture of it is read for suggestions after the commit.
        var readOn = settings.ReadsScreen && _screenReading is not null && foreground is not null
            ? WindowMonitor(monitors, foreground) : null;
        Action? read = null;

        using (var transaction = store.BeginTransaction())
        {
            var layoutId = RecordLayout(store, monitors);
            long? appId = foreground is null ? null : store.GetOrAddApp(foreground.ProcessName, foreground.ProcessPath);
            long? siteId = foreground?.Site is { } site ? store.GetOrAddSite(site) : null;
            var captureId = store.InsertCapture(now, CaptureKind.Active, appId, foreground?.Title, foreground?.Bounds, siteId, layoutId);

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
                if (_lastFrames.TryGetValue(monitorId, out var last) && last.Hash == capture.Hash && last.Mask == mask)
                {
                    frame = last.Frame;
                }
                else
                {
                    // The source captures afresh every time, so its buffer can be masked in place.
                    if (mask is { } keep)
                        I420.BlackOutsideOf(capture.Frame.Span, capture.Width, capture.Height, keep);
                    frame = _sink!.Append(monitorId, capture, now);
                    _lastFrames[monitorId] = (frame, mask, capture.Hash);
                }

                store.InsertFrame(captureId, monitorId, frame);
                captured++;

                if (monitor == readOn)
                    read = QueueReading(monitorId, monitor, capture, frame, foreground!.Bounds, captureId);
            }

            transaction.Commit();
        }
        _sink!.Committed();
        read?.Invoke();

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
                return monitors.Where(m => settings.SelectedMonitors.Contains(m.StableId, StringComparer.OrdinalIgnoreCase)).ToList();

            case CaptureMode.ActiveApplication:
                return foreground is not null && WindowMonitor(monitors, foreground) is { } best ? [best] : [];

            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.CaptureMode, "Unknown capture mode.");
        }
    }

    /// <summary>The monitor showing the largest part of the focused window; null when none shows any.</summary>
    private static MonitorInfo? WindowMonitor(IReadOnlyList<MonitorInfo> monitors, ForegroundWindow foreground) => monitors
        .Select(m => (Monitor: m, Area: (long)m.Bounds.Intersect(foreground.Bounds).Width * m.Bounds.Intersect(foreground.Bounds).Height))
        .Where(x => x.Area > 0)
        .OrderByDescending(x => x.Area)
        .Select(x => x.Monitor)
        .FirstOrDefault();

    /// <summary>
    /// What reading this capture's window takes, to queue once the capture is committed (the reader looks it up): copying
    /// the words of the last capture read off the same frame and window, or reading a copy of the window's pixels, taken now
    /// because the capture's buffer is reused.
    /// </summary>
    private Action? QueueReading(long monitorId, MonitorInfo monitor, ScreenCapture capture, FrameRef frame, PixelRect window, long captureId)
    {
        var queue = _screenReading!;
        var onMonitor = window.Intersect(monitor.Bounds).Offset(-monitor.Bounds.X, -monitor.Bounds.Y);
        if (_lastRead.TryGetValue(monitorId, out var last) && last.Frame == frame && last.Window == onMonitor)
            return () => queue.Copy(last.CaptureId, captureId);

        if (ScreenReadingQueue.CropLuma(capture.Frame.Span, capture.Width, capture.Height, onMonitor) is not { } crop)
            return null;
        var (gray, width, height) = crop;
        _lastRead[monitorId] = (frame, onMonitor, captureId);
        return () => queue.Read(captureId, gray, width, height);
    }

    private static void MarkIdleSince(RecordingStore store, DateTimeOffset since)
    {
        using var update = store.Database.Command("UPDATE captures SET kind = $idle WHERE ts_ms >= $since AND kind = $active;");
        update.Parameters.AddWithValue("$idle", (int)CaptureKind.Idle);
        update.Parameters.AddWithValue("$active", (int)CaptureKind.Active);
        update.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        update.ExecuteNonQuery();
    }

    /// <summary>
    /// Records which monitors are attached (for Day review), and finishes the open segments of those that went
    /// away, so they're encoded now rather than whenever recording next pauses. Call it inside the capture's
    /// transaction, followed by <see cref="IFrameSink.Committed"/>.
    /// </summary>
    private long RecordLayout(RecordingStore store, IReadOnlyList<MonitorInfo> monitors)
    {
        var attached = monitors.Select(store.GetOrAddMonitor).ToHashSet();
        foreach (var gone in _attached.Where(id => !attached.Contains(id)))
        {
            _sink!.Close(gone);
            _lastFrames.Remove(gone);
        }
        _attached = attached;
        return store.GetOrAddLayout(attached);
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
            if (_platform.TextReader is { } reader)
                _screenReading = new ScreenReadingQueue(reader, dataFolder);
        }
        return _store;
    }

    private void CloseSegments()
    {
        _sink?.CloseAll();
        _lastFrames.Clear();
        _lastRead.Clear();
    }

    private void CloseStorage()
    {
        _lastFrames.Clear();
        _lastRead.Clear();
        _attached = [];
        _screenReading?.Dispose();
        _sink?.Dispose();
        _db?.Dispose();
        _screenReading = null;
        _sink = null;
        _store = null;
        _db = null;
    }

    /// <summary>Not recording, and why. Logged when the reason changes rather than at every tick it lasts.</summary>
    private void SetError(string message, Exception? exception = null)
    {
        if (Status.State != RecorderState.Error || Status.Error != message)
            Log.Error($"Not recording: {message}", exception);
        SetStatus(Status with { State = RecorderState.Error, Error = message });
    }

    private void SetStatus(RecorderStatus status)
    {
        if (Status.State == RecorderState.Error && status.State != RecorderState.Error)
            Log.Info($"No longer in error ({status.State}).");
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
