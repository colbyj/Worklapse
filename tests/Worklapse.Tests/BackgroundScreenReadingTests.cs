using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Core.Suggestions;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Read Past Days in the background: resuming, stopping, and catching up what the recorder didn't read.</summary>
public class BackgroundScreenReadingTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 128, 64), true);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public void ARunReadsEverythingAndRemembersHowFarItCovered()
    {
        using var folder = new TempFolder();
        var ids = Record(folder.Path, 0, 10, 20);
        var settings = Settings(folder.Path);
        var reader = new FakeReader();
        using (var reading = new BackgroundScreenReading(settings, reader))
        {
            var ended = 0;
            reading.Ended += (_, _) => Interlocked.Increment(ref ended);
            reading.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, Patience));
            Assert.Equal(PastReadingState.Done, reading.State);
            Assert.Equal(new PastReadingProgress(0, 0, 3, 3), reading.Progress);
        }

        Assert.All(ids, id => Assert.NotNull(Words(folder.Path, id)));
        Assert.False(settings.Current.PastReadingRunning);
        Assert.NotNull(settings.Current.PastReadingReadUpTo);
    }

    [Fact]
    public void ARunLeftRunningGoesOnAtStart()
    {
        using var folder = new TempFolder();
        var ids = Record(folder.Path, 0, 10);
        var settings = Settings(folder.Path);
        settings.Save(settings.Current with { PastReadingRunning = true });

        using (var reading = new BackgroundScreenReading(settings, new FakeReader()))
            Assert.True(SpinWait.SpinUntil(() => reading.State == PastReadingState.Done, Patience));

        Assert.All(ids, id => Assert.NotNull(Words(folder.Path, id)));
        Assert.False(settings.Current.PastReadingRunning);
    }

    [Fact]
    public void QuittingLeavesARunToGoOnNextTimeAndStoppingDoesnt()
    {
        using var folder = new TempFolder();
        Record(folder.Path, 0, 10, 20);
        var settings = Settings(folder.Path);
        var reader = new FakeReader { Hold = new ManualResetEventSlim() };

        var ended = 0;
        var reading = new BackgroundScreenReading(settings, reader);
        reading.Ended += (_, _) => Interlocked.Increment(ref ended);
        reading.Start();
        Assert.True(reader.Entered.Wait(Patience));
        var quitting = Task.Run(reading.Dispose);
        reader.Hold.Set();
        Assert.True(quitting.Wait(Patience));
        Assert.True(settings.Current.PastReadingRunning);
        Assert.Equal(0, ended); // quitting isn't an end: it goes on next time

        reader.Hold.Reset();
        reader.Entered.Reset();
        using (reading = new BackgroundScreenReading(settings, reader)) // goes on
        {
            reading.Ended += (_, _) => Interlocked.Increment(ref ended);
            Assert.True(reader.Entered.Wait(Patience));
            reading.Stop();
            Assert.Equal(0, Volatile.Read(ref ended)); // its threads are still reading
            reader.Hold.Set();
            Assert.Equal(PastReadingState.Stopped, reading.State);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, Patience));
        }
        Assert.False(settings.Current.PastReadingRunning);
    }

    [Fact]
    public void TurningReadingOffStopsTheRun()
    {
        using var folder = new TempFolder();
        Record(folder.Path, 0, 10);
        var settings = Settings(folder.Path);
        var reader = new FakeReader { Hold = new ManualResetEventSlim() };
        using var reading = new BackgroundScreenReading(settings, reader);
        reading.Start();
        Assert.True(reader.Entered.Wait(Patience));

        settings.Save(settings.Current with { ReadScreenText = false });
        reader.Hold.Set();

        Assert.Equal(PastReadingState.Stopped, reading.State);
        Assert.True(SpinWait.SpinUntil(() => !settings.Current.PastReadingRunning, Patience));
        reading.Start(); // not while reading is off
        Assert.Equal(PastReadingState.Stopped, reading.State);
    }

    [Fact]
    public void CatchingUpReadsWhatTheRecorderDidntSinceTheLastRun()
    {
        using var folder = new TempFolder();
        var ids = Record(folder.Path, 0, 10, 20, 30);
        var settings = Settings(folder.Path);
        var coveredTo = Nine.AddSeconds(15);
        settings.Save(settings.Current with { PastReadingReadUpTo = coveredTo });

        using (new BackgroundScreenReading(settings, new FakeReader(), catchUpEvery: TimeSpan.FromMilliseconds(50)))
            Assert.True(SpinWait.SpinUntil(() => settings.Current.PastReadingReadUpTo != coveredTo, Patience));

        Assert.Equal([false, false, true, true], ids.Select(id => Words(folder.Path, id) is not null));
        Assert.True(settings.Current.PastReadingReadUpTo > DateTimeOffset.Now - BackgroundScreenReading.RecorderMargin - Patience);
    }

    [Fact]
    public void AScreenWithoutWordsCountsAsRead()
    {
        using var folder = new TempFolder();
        var id = Record(folder.Path, 0).Single();
        var reader = new FakeReader { Text = "" };

        Assert.Equal(0, PastScreenReading.Run(folder.Path, reader, Nine, Nine.AddHours(1), default, 1, null, TestContext.Current.CancellationToken));
        Assert.Equal(0, PastScreenReading.Run(folder.Path, reader, Nine, Nine.AddHours(1), default, 1, null, TestContext.Current.CancellationToken));

        Assert.Equal(Array.Empty<string>(), Words(folder.Path, id));
        Assert.Equal(1, reader.Reads); // not read again
    }

    private static SettingsStore Settings(string dataFolder)
    {
        var settings = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
        settings.Save(new WorklapseSettings { DataFolder = dataFolder, SuggestProjects = true, ReadScreenText = true, PastReadingThreads = 2 });
        return settings;
    }

    /// <summary>Active captures with a picture each, at these seconds after nine.</summary>
    private static List<long> Record(string dataFolder, params int[] seconds)
    {
        var ids = new List<long>();
        using var db = WorklapseDatabase.Open(dataFolder);
        var store = new RecordingStore(db);
        using var sink = new FrameSink(store, () => (FrameFormat.Jpeg, 90));
        var monitorId = store.GetOrAddMonitor(Main);
        var app = store.GetOrAddApp("rider64.exe", null);
        foreach (var second in seconds)
        {
            using var transaction = store.BeginTransaction();
            var id = store.InsertCapture(Nine.AddSeconds(second), CaptureKind.Active, app, "Code", new PixelRect(10, 10, 50, 30));
            var pixels = new byte[I420.Size(Main.Bounds.Width, Main.Bounds.Height)];
            Array.Fill(pixels, (byte)(60 + second));
            var frame = sink.Append(monitorId, new ScreenCapture { Monitor = Main, Width = 128, Height = 64, Frame = pixels, Hash = (ulong)second + 1 },
                Nine.AddSeconds(second));
            store.InsertFrame(id, monitorId, frame);
            transaction.Commit();
            sink.Committed();
            ids.Add(id);
        }
        sink.CloseAll();
        sink.Committed();
        return ids;
    }

    private static string[]? Words(string dataFolder, long id)
    {
        using var db = WorklapseDatabase.Open(dataFolder);
        return new ScreenWordStore(db).Get(id);
    }

    /// <summary>Reads every picture as <see cref="Text"/>; with <see cref="Hold"/>, waits for it to be set before answering.</summary>
    private sealed class FakeReader : IScreenTextReader
    {
        public int Reads;

        public string Text { get; init; } = "docket 42";

        public ManualResetEventSlim? Hold { get; init; }

        /// <summary>Set once a reading has begun.</summary>
        public ManualResetEventSlim Entered { get; } = new();

        public string Read(byte[] gray, int width, int height)
        {
            Interlocked.Increment(ref Reads);
            Entered.Set();
            Hold?.Wait(Patience);
            return Text;
        }
    }
}
