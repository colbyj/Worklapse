using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Worklapse.App.Controls;
using Worklapse.App.Playback;
using Worklapse.App.Theming;
using Worklapse.Video;
using PixelRect = Worklapse.Core.Platform.PixelRect;

namespace Worklapse.App.Views;

/// <summary>
/// Plays Worklapse's .ivf files without their database (Settings → About → Open Video Files…): for recordings someone
/// sent, which other players show as a slide every ten seconds or so, since frames carry their real capture times.
/// One monitor, or all of them side by side (left to right in monitor-number order: how they were arranged is in the
/// database, not the files). Steps go from one moment with a new frame to the next, whatever the time between them.
/// </summary>
public partial class VideoFilesWindow : Window
{
    private const string AllMonitors = "All monitors";

    private static readonly (string Label, int FramesPerSecond)[] Speeds =
        [("1 frame/s", 1), ("2 frames/s", 2), ("5 frames/s", 5), ("10 frames/s", 10), ("20 frames/s", 20), ("30 frames/s", 30)];

    private readonly IvfRenderer _renderer = new();
    private readonly DispatcherTimer _playTimer = new(DispatcherPriority.Background);
    private readonly Dictionary<string, WriteableBitmap> _bitmaps = [];
    private IvfRecording? _recording;
    private IvfPlaylist? _playlist;
    private IReadOnlyList<PixelRect> _places = []; // each track's place, in the playlist's order
    private int _index = -1;
    private int _version;
    private bool _waitingForFrame;
    private bool _updatingBars;
    private bool _closed;

    public VideoFilesWindow()
    {
        InitializeComponent();
        WindowBackdrop.Attach(this);

        SpeedBox.ItemsSource = Speeds.Select(s => s.Label).ToList();
        SpeedBox.SelectedIndex = 3; // 10 frames/s
        SpeedBox.SelectionChanged += (_, _) => UpdatePlayInterval();
        UpdatePlayInterval();
        _playTimer.Tick += (_, _) => PlayTick();

        MonitorBox.SelectionChanged += (_, _) => ShowTracks();
        Strip.Picked += GoTo;
        Frames.ViewChanged += UpdateView;
        HorizontalBar.ValueChanged += (_, _) => OnBarScrolled();
        VerticalBar.ValueChanged += (_, _) => OnBarScrolled();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        _renderer.Rendered += result => Dispatcher.UIThread.Post(() => Show(result));
        Closed += (_, _) => OnClosed();

        ShowEmpty("Open a Worklapse .ivf file, or a folder of them (such as video/2026-11-20)");
    }

    private int Count => _playlist?.Times.Count ?? 0;

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Video File",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Worklapse video (*.ivf)") { Patterns = ["*.ivf"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await OpenAsync(path);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Folder of Video Files" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await OpenAsync(path);
    }

    /// <summary>Opens a file or a folder (and the folders in it), replacing what was open. Several monitors start shown together.</summary>
    public async Task OpenAsync(string path)
    {
        Pause();
        ShowEmpty("Reading the files…");
        IvfRecording recording;
        try
        {
            recording = await Task.Run(() => IvfLibrary.Open(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowEmpty($"Couldn't read {path}: {ex.Message}");
            return;
        }
        if (_closed)
            return;

        _recording = recording;
        Title = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))} — Video Files — Worklapse";
        var names = recording.Tracks.Select(t => t.Name).ToList();
        MonitorBox.ItemsSource = names.Count > 1 ? [AllMonitors, .. names] : names;
        MonitorTools.IsVisible = names.Count > 1;
        if (names.Count > 0)
            MonitorBox.SelectedIndex = 0;
        ShowTracks();
    }

    /// <summary>Plays the monitor picked, or all of them.</summary>
    private void ShowTracks()
    {
        Pause();
        if (_recording is not { } recording || MonitorBox.SelectedItem is not string picked)
            return;
        var tracks = picked == AllMonitors ? recording.Tracks : recording.Tracks.Where(t => t.Name == picked).ToList();
        if (tracks.Count == 0)
        {
            ShowEmpty(recording.Skipped.Count == 0
                ? "No .ivf files there"
                : $"No Worklapse video in these files:\n{string.Join("\n", recording.Skipped.Take(8))}");
            return;
        }

        _playlist = new IvfPlaylist(tracks);
        _places = Places(tracks);
        DropBitmaps(keep: tracks.Select(t => t.Name).ToHashSet());
        Strip.SetFrames(_playlist.Times);
        _index = -1;
        Frames.Fit();
        GoTo(0);
    }

    /// <summary>Monitors side by side, left to right and top-aligned, each as large as its largest frame.</summary>
    private static List<PixelRect> Places(IReadOnlyList<IvfTrack> tracks)
    {
        var places = new List<PixelRect>();
        var x = 0;
        foreach (var track in tracks)
        {
            var width = track.Frames.Max(f => f.File.Width);
            var height = track.Frames.Max(f => f.File.Height);
            places.Add(new PixelRect(x, 0, width, height));
            x += width;
        }
        return places;
    }

    private void GoTo(int index)
    {
        if (_playlist is not { } playlist || Count == 0)
            return;
        index = Math.Clamp(index, 0, Count - 1);
        if (index == _index && !_waitingForFrame)
            return;
        _index = index;
        Strip.Current = index;
        _waitingForFrame = true;
        var frames = playlist.FramesAt(index);
        var request = new List<(string, IvfFrame)>();
        for (var t = 0; t < frames.Length; t++)
        {
            if (frames[t] is { } frame)
                request.Add((playlist.Tracks[t].Name, frame));
        }
        _renderer.Request(++_version, request);
        UpdateControls();
    }

    private void Show(IvfFrameResult result)
    {
        try
        {
            if (_closed || result.Version != _version || _playlist is not { } playlist)
                return;
            _waitingForFrame = false;

            var tiles = new List<FrameTile>();
            for (var t = 0; t < playlist.Tracks.Count; t++)
            {
                var name = playlist.Tracks[t].Name;
                var place = _places[t];
                if (result.Frames.TryGetValue(name, out var frame))
                {
                    var bitmap = CopyToBitmap(name, frame);
                    tiles.Add(new FrameTile(place with { Width = frame.Width, Height = frame.Height }, bitmap, null));
                }
                else
                {
                    var why = playlist.FramesAt(_index)[t] is null ? "nothing recorded yet" : "couldn't be decoded";
                    tiles.Add(new FrameTile(place, null, $"{name}: {why}"));
                }
            }

            if (result.Error is not null)
                Pause();
            Frames.SetContent(tiles, result.Error is { } error ? $"A frame couldn't be decoded: {error.Message}" : null);
            UpdateView();
        }
        finally
        {
            result.Release();
        }
    }

    private WriteableBitmap CopyToBitmap(string track, DecodedFrame frame)
    {
        if (!_bitmaps.TryGetValue(track, out var bitmap) || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
        {
            bitmap?.Dispose();
            _bitmaps[track] = bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        // Converted straight into the bitmap: no full-size BGRA copy of the frame exists anywhere else.
        using var buffer = bitmap.Lock();
        frame.CopyBgra(buffer.Address, buffer.RowBytes);
        return bitmap;
    }

    /// <summary>Each bitmap is a full-size copy of a monitor; let go of those not shown.</summary>
    private void DropBitmaps(IReadOnlySet<string> keep)
    {
        foreach (var name in _bitmaps.Keys.Where(n => !keep.Contains(n)).ToList())
        {
            _bitmaps.Remove(name, out var bitmap);
            bitmap!.Dispose();
        }
    }

    private void ShowEmpty(string text)
    {
        _playlist = null;
        _places = [];
        _index = -1;
        Strip.SetFrames([]);
        Frames.SetContent([], text);
        DropBitmaps(keep: new HashSet<string>());
        UpdateControls();
        UpdateView();
    }

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playTimer.IsEnabled)
        {
            Pause();
            return;
        }
        if (Count == 0)
            return;
        if (_index >= Count - 1)
            GoTo(0);
        _playTimer.Start();
        UpdateControls();
    }

    private void Pause()
    {
        _playTimer.Stop();
        UpdateControls();
    }

    /// <summary>Waits for the frames asked for before moving on, so a slow decode slows playback rather than skipping frames.</summary>
    private void PlayTick()
    {
        if (_waitingForFrame)
            return;
        if (_index >= Count - 1)
        {
            Pause();
            return;
        }
        GoTo(_index + 1);
    }

    private void UpdatePlayInterval() =>
        _playTimer.Interval = TimeSpan.FromSeconds(1.0 / Speeds[Math.Max(0, SpeedBox.SelectedIndex)].FramesPerSecond);

    private void OnFirst(object? sender, RoutedEventArgs e) => GoTo(0);
    private void OnPrevious(object? sender, RoutedEventArgs e) => Step(-1);
    private void OnNext(object? sender, RoutedEventArgs e) => Step(1);
    private void OnLast(object? sender, RoutedEventArgs e) => GoTo(Count - 1);

    private void Step(int by)
    {
        Pause();
        GoTo(_index + by);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Leave the keys to an open drop-down.
        if (e.KeyModifiers != KeyModifiers.None || MonitorBox.IsDropDownOpen || SpeedBox.IsDropDownOpen)
            return;
        switch (e.Key)
        {
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            case Key.Home: Pause(); GoTo(0); break;
            case Key.End: Pause(); GoTo(Count - 1); break;
            case Key.Space: TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void UpdateControls()
    {
        var playing = _playTimer.IsEnabled;
        PlayIcon.IsVisible = !playing;
        PauseIcon.IsVisible = playing;
        PlayText.Text = playing ? "Pause" : "Play";
        var has = Count > 0;
        PlayButton.IsEnabled = has;
        FirstButton.IsEnabled = PreviousButton.IsEnabled = has && _index > 0;
        NextButton.IsEnabled = LastButton.IsEnabled = has && _index < Count - 1;

        if (_playlist is not { } playlist || _index < 0)
        {
            FrameTimeText.Text = "";
            FrameText.Text = SkippedText();
            return;
        }
        var time = playlist.Times[_index];
        FrameTimeText.Text = time.ToString("ddd d MMM yyyy  HH:mm:ss", CultureInfo.CurrentCulture);
        var gap = _index > 0 ? time - playlist.Times[_index - 1] : TimeSpan.Zero;
        var gapText = gap >= TimeSpan.FromMinutes(1) ? $" · {Duration(gap)} after the previous frame" : "";

        // One monitor: which file and its size. Several: which of them changed at this moment.
        string detail;
        if (playlist.Tracks.Count == 1)
        {
            var frame = playlist.FramesAt(_index)[0]!;
            detail = $"{Path.GetFileName(frame.File.Path)} · {frame.File.Width}×{frame.File.Height}";
        }
        else
        {
            var changed = Enumerable.Range(0, playlist.Tracks.Count)
                .Where(t => playlist.ChangesAt(t, _index))
                .Select(t => playlist.Tracks[t].Name);
            detail = $"New: {string.Join(", ", changed)}";
        }
        FrameText.Text = $"Frame {_index + 1:N0} of {Count:N0}{gapText} · {detail} · UTC{Offset(time.Offset)}{SkippedText(" · ")}";
    }

    private string SkippedText(string prefix = "") =>
        _recording is { Skipped.Count: > 0 } r ? $"{prefix}{r.Skipped.Count} file(s) skipped" : "";

    private static string Duration(TimeSpan span) =>
        span >= TimeSpan.FromHours(1) ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{span.Minutes}m {span.Seconds:00}s";

    private static string Offset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():h\\:mm}";

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Frames.ZoomBy(1.5);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Frames.ZoomBy(1 / 1.5);
    private void OnFit(object? sender, RoutedEventArgs e) => Frames.Fit();

    /// <summary>Shows the magnification, and the scroll bars while the picture is larger than the view.</summary>
    private void UpdateView()
    {
        var percent = $"{Frames.Magnification * 100:0}%";
        ZoomText.Text = percent;
        ViewText.Text = Count == 0 ? "" : $"{percent}{(Frames.IsFitted ? " (fit)" : "")}";

        _updatingBars = true;
        try
        {
            UpdateBar(HorizontalBar, Frames.MaxOffset.X, Frames.Offset.X, Frames.Bounds.Width);
            UpdateBar(VerticalBar, Frames.MaxOffset.Y, Frames.Offset.Y, Frames.Bounds.Height);
        }
        finally
        {
            _updatingBars = false;
        }

        static void UpdateBar(Avalonia.Controls.Primitives.ScrollBar bar, double max, double value, double viewport)
        {
            bar.Maximum = max;
            bar.Value = value;
            bar.ViewportSize = viewport;
            bar.LargeChange = viewport * 0.9;
            bar.SmallChange = 40;
        }
    }

    private void OnBarScrolled()
    {
        if (!_updatingBars)
            Frames.ScrollTo(new Vector(HorizontalBar.Value, VerticalBar.Value));
    }

    private void OnClosed()
    {
        _closed = true;
        _playTimer.Stop();
        _renderer.Dispose();
        Frames.SetContent([], null);
        DropBitmaps(keep: new HashSet<string>());
    }
}
