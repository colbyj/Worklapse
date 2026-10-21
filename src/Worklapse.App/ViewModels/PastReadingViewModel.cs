using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Worklapse.Video;

namespace Worklapse.App.ViewModels;

/// <summary>
/// Read Past Days' progress window: what's read of the time training learns from and of everything else, how long the
/// rest may take, and the threads, changeable while it runs. Follows <see cref="BackgroundScreenReading"/>, which goes on
/// when the window closes.
/// </summary>
public sealed partial class PastReadingViewModel : ObservableObject, IDisposable
{
    /// <summary>Changing the threads waits this long for the next click, since each change starts the workers again.</summary>
    private static readonly TimeSpan ThreadsSettle = TimeSpan.FromSeconds(1);

    /// <summary>Reading for less than this says too little about its speed to tell how long is left.</summary>
    private static readonly TimeSpan MeasuringFor = TimeSpan.FromSeconds(20);

    /// <summary>The speed is that of the last this long (pictures and videos, and other users of the CPU, change it).</summary>
    private static readonly TimeSpan SpeedOver = TimeSpan.FromMinutes(2);

    private const string Done = "✓", Under = "▸", Waiting = "·";

    private readonly BackgroundScreenReading _reading;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _settle;
    private readonly Queue<(DateTime At, int Done)> _samples = new(); // the speed is measured over these
    private bool _loading;

    public PastReadingViewModel(BackgroundScreenReading reading)
    {
        _reading = reading;
        _loading = true;
        Threads = reading.Threads;
        _loading = false;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        _settle = new DispatcherTimer(ThreadsSettle, DispatcherPriority.Normal, (_, _) =>
        {
            _settle!.Stop();
            _reading.SetThreads((int)(Threads ?? _reading.Threads));
        });
        _settle.Stop();
        reading.Changed += OnReadingChanged;
        Refresh();
        _refresh.Start();
    }

    public int MaxThreads { get; } = Environment.ProcessorCount;

    [ObservableProperty] private decimal? _threads;

    [ObservableProperty] private string _trainingMark = Waiting;
    [ObservableProperty] private string _trainingText = "";
    [ObservableProperty] private string _restMark = Waiting;

    /// <summary>0–100, of everything.</summary>
    [ObservableProperty] private double _percent;

    [ObservableProperty] private string _countText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(HasAction))]
    private PastReadingState _state;

    /// <summary>Stop while it runs; start again after it stopped or failed; nothing once it's done.</summary>
    public string ActionText => State == PastReadingState.Running ? "Stop" : "Read Past Days";

    public bool HasAction => State != PastReadingState.Done;

    [RelayCommand]
    private void Act()
    {
        if (State == PastReadingState.Running)
            _reading.Stop();
        else
            _reading.Start();
    }

    public void Dispose()
    {
        _refresh.Stop();
        // A change of threads still settling is made now rather than lost.
        if (_settle.IsEnabled)
        {
            _settle.Stop();
            _reading.SetThreads((int)(Threads ?? _reading.Threads));
        }
        _reading.Changed -= OnReadingChanged;
    }

    partial void OnThreadsChanged(decimal? value)
    {
        if (_loading || value is null)
            return;
        _settle.Stop();
        _settle.Start();
    }

    private void OnReadingChanged(object? sender, EventArgs e)
    {
        _samples.Clear(); // started, stopped or other threads: another speed
        Refresh();
    }

    private void Refresh()
    {
        State = _reading.State;
        var p = _reading.Progress;
        var running = State == PastReadingState.Running;
        var trainingDone = p.Total > 0 && p.TrainingDone >= p.TrainingTotal;
        var (restDone, restTotal) = (p.Done - p.TrainingDone, p.Total - p.TrainingTotal);

        TrainingMark = trainingDone ? Done : running ? Under : Waiting;
        TrainingText = p.Total == 0 ? ""
            : trainingDone ? $"{p.TrainingTotal:N0} read"
            : $"{p.TrainingDone:N0} of {p.TrainingTotal:N0}";
        RestMark = p.Total > 0 && restDone >= restTotal ? Done : running && trainingDone ? Under : Waiting;
        Percent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        CountText = State switch
        {
            PastReadingState.Running when p.Total == 0 => "Finding what to read…",
            PastReadingState.Running => $"{p.Done:N0} of {p.Total:N0}{Left(p)}",
            PastReadingState.Done => p.Total == 0 ? "Nothing left to read" : $"Done: {p.Total:N0} read",
            PastReadingState.Stopped => $"Stopped at {p.Done:N0} of {p.Total:N0}; what was read is kept",
            PastReadingState.Failed => "Couldn't read the history: " + _reading.Problem,
            _ => "",
        };
    }

    /// <summary>" · about 1 h 10 min", once the speed is known.</summary>
    private string Left(PastReadingProgress p)
    {
        var now = DateTime.UtcNow;
        if (_samples.Count > 0 && p.Done < _samples.Peek().Done)
            _samples.Clear();
        _samples.Enqueue((now, p.Done));
        while (now - _samples.Peek().At > SpeedOver)
            _samples.Dequeue();
        var from = _samples.Peek();
        var elapsed = now - from.At;
        var read = p.Done - from.Done;
        if (elapsed < MeasuringFor || read <= 0)
            return "";
        var left = TimeSpan.FromSeconds((p.Total - p.Done) * elapsed.TotalSeconds / read);
        return " · " + (left.TotalMinutes < 1 ? "under a minute"
            : left.TotalHours < 1 ? $"about {Math.Ceiling(left.TotalMinutes).ToString(CultureInfo.CurrentCulture)} min"
            : $"about {(int)left.TotalHours} h {left.Minutes} min");
    }
}
