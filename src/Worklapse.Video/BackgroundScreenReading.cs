using Worklapse.Core.Diagnostics;
using Worklapse.Core.Platform;
using Worklapse.Core.Settings;

namespace Worklapse.Video;

public enum PastReadingState
{
    /// <summary>Not started in this run of Worklapse.</summary>
    Idle,
    Running,
    Done,
    Stopped,
    Failed,
}

/// <summary>
/// Read Past Days, owned by the app rather than a window: <see cref="PastScreenReading"/> over the whole history, on the
/// threads asked for, until it's done or stopped; one that was running goes on when Worklapse starts again. Once one has
/// finished, it catches up every few minutes, on one thread, the captures since that the recorder didn't get to read (it
/// drops pictures when it falls behind). Reading turned off stops both. Thread-safe; <see cref="Changed"/> and the
/// settings it saves go through the post it's given (to the UI thread, where the app's other saves are made).
/// </summary>
public sealed class BackgroundScreenReading : IDisposable
{
    /// <summary>How often catching up looks for captures the recorder didn't read.</summary>
    public static readonly TimeSpan DefaultCatchUpEvery = TimeSpan.FromMinutes(5);

    /// <summary>Captures newer than this are left to the recorder's own reading, which may still be busy with them.</summary>
    public static readonly TimeSpan RecorderMargin = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _settings;
    private readonly IScreenTextReader _reader;
    private readonly TimeSpan _catchUpEvery;
    private readonly Action<Action>? _post;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _disposing = new();
    private readonly Thread _thread;
    private CancellationTokenSource _current; // the work under way; cancelled to change course
    private bool _wanted;                     // a run was started and hasn't finished or been stopped
    private int _run;                         // counts Start: an attempt of an earlier run doesn't add to this one's counts
    private string? _folder;                  // the data folder of the work under way
    private int _threads;
    private PastReadingState _state;
    private PastReadingProgress _progress;    // the current attempt's
    private (int TrainingDone, int Done) _earlier; // earlier attempts' of the same run (before a change of threads)
    private string? _problem;
    private bool _reportedCatchUpFailure;

    /// <param name="post">Runs an action on the thread settings are saved on; null runs it at once (tests).</param>
    public BackgroundScreenReading(SettingsStore settings, IScreenTextReader reader, Action<Action>? post = null, TimeSpan? catchUpEvery = null)
    {
        _settings = settings;
        _reader = reader;
        _post = post;
        _catchUpEvery = catchUpEvery ?? DefaultCatchUpEvery;
        _current = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
        var current = settings.Current;
        _wanted = current.PastReadingRunning && current.ReadsScreen;
        _state = _wanted ? PastReadingState.Running : PastReadingState.Idle;
        _threads = current.PastReadingThreads;
        settings.Changed += OnSettingsChanged;
        _thread = new Thread(Loop) { Name = "Worklapse background screen reading", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The state, threads or problem changed (progress doesn't raise it: read <see cref="Progress"/> as often as wanted).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A run's threads have all ended (it finished, was stopped or failed; not when it starts again on other threads or
    /// Worklapse quits): the memory their pictures and OCR engines held can be given back. Stopping returns before this.
    /// </summary>
    public event EventHandler? Ended;

    public PastReadingState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>How far the run got, counting what it read before a change of threads.</summary>
    public PastReadingProgress Progress
    {
        get
        {
            lock (_gate)
            {
                var (training, done) = _earlier;
                return new PastReadingProgress(training + _progress.TrainingDone, training + _progress.TrainingTotal,
                    done + _progress.Done, done + _progress.Total);
            }
        }
    }

    /// <summary>Why the run failed.</summary>
    public string? Problem
    {
        get { lock (_gate) return _problem; }
    }

    public int Threads
    {
        get { lock (_gate) return _threads; }
    }

    /// <summary>Starts reading the whole history (what's left of it); nothing when it's running already or reading is off.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_wanted || !_settings.Current.ReadsScreen)
                return;
            _wanted = true;
            _run++;
            _state = PastReadingState.Running;
            (_earlier, _progress, _problem) = (default, default, null);
            _current.Cancel(); // a catch-up under way
        }
        Save(s => s with { PastReadingRunning = true });
        _wake.Release();
        RaiseChanged();
    }

    /// <summary>Stops the run; what was read is kept.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_wanted)
                return;
            _wanted = false;
            _state = PastReadingState.Stopped;
            _current.Cancel();
        }
        Save(s => s with { PastReadingRunning = false });
        RaiseChanged();
    }

    /// <summary>Reads on this many threads from now on (and saves it); a run under way starts again with them.</summary>
    public void SetThreads(int threads)
    {
        threads = Math.Clamp(threads, 1, Environment.ProcessorCount);
        lock (_gate)
        {
            if (threads == _threads)
                return;
            _threads = threads;
            if (_wanted)
                _current.Cancel();
        }
        Save(s => s with { PastReadingThreads = threads });
        RaiseChanged();
    }

    /// <summary>Stops reading. A run under way isn't marked stopped: it goes on when Worklapse starts again.</summary>
    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _disposing.Cancel();
        _wake.Release();
        _thread.Join();
        _current.Dispose();
        _disposing.Dispose();
        _wake.Dispose();
    }

    private void Loop()
    {
        while (!_disposing.IsCancellationRequested)
        {
            bool full;
            CancellationToken cancel;
            lock (_gate)
            {
                _current.Dispose();
                _current = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
                cancel = _current.Token;
                full = _wanted;
            }

            if (full)
            {
                ReadAll(cancel);
                continue;
            }
            CatchUp(cancel);
            try
            {
                _wake.Wait(_catchUpEvery, _disposing.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>One attempt at the whole run: it ends done, stopped, failed, or to start again (threads or folder changed).</summary>
    private void ReadAll(CancellationToken cancel)
    {
        var settings = _settings.Current;
        var to = DateTimeOffset.Now;
        int threads, run;
        lock (_gate)
        {
            (threads, run, _folder) = (threads: _threads, _run, settings.DataFolder);
            _progress = default;
        }
        try
        {
            var kept = PastScreenReading.Run(settings.DataFolder, _reader, DateTimeOffset.MinValue.AddYears(1), to, settings.DayStart,
                threads, new Reporter(this), cancel);
            lock (_gate)
            {
                if (_run != run)
                    return; // stopped and started again just as it finished: the new run reads what's left
                _wanted = false;
                _state = PastReadingState.Done;
            }
            Save(s => s with { PastReadingRunning = false, PastReadingReadUpTo = to });
            Log.Info($"Read Past Days finished; {kept:N0} captures got words in its last stretch.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Stopped, Worklapse quitting, or starting again (threads or folder changed): then what this attempt read still
            // counts, unless it was another data folder's or the run was stopped and started anew.
            bool again;
            lock (_gate)
            {
                again = _wanted && _run == run;
                if (again && !_disposing.IsCancellationRequested)
                {
                    _earlier = SameFolder(_settings.Current.DataFolder, settings.DataFolder)
                        ? (_earlier.TrainingDone + _progress.TrainingDone, _earlier.Done + _progress.Done)
                        : default;
                    _progress = default;
                }
            }
            if (!again && !_disposing.IsCancellationRequested)
                Raise(Ended);
            return;
        }
        catch (Exception ex)
        {
            // The history can't be read (a folder gone, the database locked or damaged): said once, not tried again until
            // started again.
            Log.Error("Read Past Days failed.", ex);
            lock (_gate)
            {
                if (_run != run)
                    return;
                _wanted = false;
                _state = PastReadingState.Failed;
                _problem = ex.Message;
            }
            Save(s => s with { PastReadingRunning = false });
        }
        RaiseChanged();
        Raise(Ended);
    }

    /// <summary>Reads the captures since the last finished run that the recorder didn't, on one thread.</summary>
    private void CatchUp(CancellationToken cancel)
    {
        var settings = _settings.Current;
        if (!settings.ReadsScreen || settings.PastReadingReadUpTo is not { } from)
            return;
        var to = DateTimeOffset.Now - RecorderMargin;
        if (to <= from)
            return;
        lock (_gate)
            _folder = settings.DataFolder;
        try
        {
            var found = new CountingProgress();
            PastScreenReading.Run(settings.DataFolder, _reader, from, to, settings.DayStart, threads: 1, found, cancel);
            // Each capture gets one try here: one that couldn't be decoded or read isn't tried every few minutes.
            if (found.Total > 0)
                Save(s => s.PastReadingReadUpTo == from ? s with { PastReadingReadUpTo = to } : s);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_reportedCatchUpFailure)
                Log.Error("Catching up screen reading failed; it's tried again every few minutes.", ex);
            _reportedCatchUpFailure = true;
        }
    }

    private void OnSettingsChanged(object? sender, WorklapseSettings settings)
    {
        bool stopped;
        lock (_gate)
        {
            stopped = !settings.ReadsScreen && _wanted;
            if (stopped)
            {
                _wanted = false;
                _state = PastReadingState.Stopped;
            }
            // Reading turned off, or another data folder: whatever is under way stops (the run starts again on the new one).
            if (!settings.ReadsScreen || (_folder is not null && !SameFolder(settings.DataFolder, _folder)))
                _current.Cancel();
        }
        if (stopped)
        {
            Save(s => s with { PastReadingRunning = false });
            RaiseChanged();
        }
    }

    private static bool SameFolder(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves a change to the settings where the others are saved, so none is lost.</summary>
    private void Save(Func<WorklapseSettings, WorklapseSettings> change)
    {
        void Apply()
        {
            var current = _settings.Current;
            var changed = change(current);
            if (changed != current)
                _settings.Save(changed);
        }
        if (_post is null)
            Apply();
        else
            _post(Apply);
    }

    private void RaiseChanged() => Raise(Changed);

    private void Raise(EventHandler? handler)
    {
        if (handler is null)
            return;
        if (_post is null)
            handler(this, EventArgs.Empty);
        else
            _post(() => handler(this, EventArgs.Empty));
    }

    /// <summary>Keeps the run's progress (reported on the reading threads, under the run's own lock).</summary>
    private sealed class Reporter(BackgroundScreenReading owner) : IProgress<PastReadingProgress>
    {
        public void Report(PastReadingProgress value)
        {
            lock (owner._gate)
                owner._progress = value;
        }
    }

    private sealed class CountingProgress : IProgress<PastReadingProgress>
    {
        public int Total { get; private set; }

        public void Report(PastReadingProgress value) => Total = value.Total;
    }
}
