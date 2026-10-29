using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Platform;
using Worklapse.Core.Recording;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Freeing the disk space of deleted captures: removing and rebuilding segments.</summary>
public class ReclaimTests
{
    private const int Width = 320, Height = 180;
    private static readonly MonitorInfo Monitor = new("TEST", new PixelRect(0, 0, Width, Height), true);
    private static readonly DateTimeOffset Nine = new(2026, 10, 15, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 9, 0, 0)));
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    /// <summary>Re-encoding is lossy; the frames must still be close to what was captured.</summary>
    private const double MinimumPsnr = 30;

    [Fact]
    public void ASegmentNothingRefersToIsDeleted()
    {
        using var recording = new Recording();
        for (var i = 0; i < 6; i++)
            recording.Add(i);
        recording.Sink.CloseAll();
        for (var i = 6; i < 9; i++)
            recording.Add(i);
        recording.FinishRecording();
        var (first, second) = (recording.Captures[0].Frame.SegmentId, recording.Captures[^1].Frame.SegmentId);
        var firstFile = recording.FileOf(first);

        recording.Delete((0, 60));
        recording.Reclaim();

        Assert.Equal([second], recording.SegmentIds());
        Assert.False(File.Exists(firstFile));
        Assert.Equal(0, recording.Count("SELECT count(*) FROM segment_frames WHERE segment_id = " + first));
        recording.AssertTidy();
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Fact]
    public void ASegmentThatCantBeEncodedIsReportedAndStaysStaged()
    {
        using var recording = new Recording();
        for (var i = 0; i < 3; i++)
            recording.Add(i);
        var segment = recording.Captures[0].Frame.SegmentId;
        var stagingFile = StagingFile.PathFor(recording.FileOf(segment));
        recording.Sink.Dispose(); // quit with the segment staged
        File.Delete(stagingFile);

        using var encoder = new BatchEncoder(recording.Folder.Path);
        Assert.True(encoder.WaitUntilIdle(Timeout));

        Assert.NotNull(encoder.EncodingProblem);
        Assert.Contains(Path.GetFileName(recording.FileOf(segment)), encoder.EncodingProblem);
        Assert.Equal((long)SegmentState.Staged,
            recording.Count($"SELECT state FROM segments WHERE id = {segment}"));
        // The thread carries on with other work.
        encoder.RequestReclaim();
        Assert.True(encoder.WaitUntilIdle(Timeout));
    }

    [Fact]
    public void APartlyDeletedSegmentIsRebuiltFromTheFramesStillShown()
    {
        // Frames 0–9, then two captures where the monitor didn't change: they show frame 9.
        using var recording = new Recording();
        for (var i = 0; i < 10; i++)
            recording.Add(i);
        recording.AddUnchanged(100);
        recording.AddUnchanged(110);
        recording.FinishRecording();
        var old = recording.Captures[0].Frame.SegmentId;
        var oldFile = recording.FileOf(old);
        var oldSize = new FileInfo(oldFile).Length;

        // Frame 9's own capture goes, but the later captures still show it.
        recording.Delete((30, 70), (90, 100));
        recording.Reclaim();

        var rebuilt = Assert.Single(recording.SegmentIds());
        Assert.NotEqual(old, rebuilt);
        Assert.False(File.Exists(oldFile));
        Assert.True(new FileInfo(recording.FileOf(rebuilt)).Length < oldSize);
        Assert.Equal([0, 1, 2, 7, 8, 9], recording.FrameIndexes(rebuilt));
        Assert.Equal([0], recording.KeyFrames(rebuilt));
        Assert.Equal(SegmentState.Encoded, recording.State(rebuilt));
        Assert.All(recording.Captures, c => Assert.Equal(rebuilt, recording.CurrentFrame(c.CaptureId).SegmentId));
        Assert.Equal(new FrameRef(rebuilt, 9), recording.CurrentFrame(recording.Captures[^1].CaptureId));
        recording.AssertTidy();
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Fact]
    public void ADeletedPictureIsInNoFileAfterwardsHoweverLittleIsDeleted()
    {
        // One capture in the middle shows something else entirely; only it is deleted.
        using var recording = new Recording();
        for (var i = 0; i < 10; i++)
            recording.Add(i, i == 4 ? Secret() : null);
        recording.FinishRecording();
        Assert.Contains(recording.EveryFrameOnDisk(), frame => Psnr(frame, Secret()) > MinimumPsnr);

        recording.Delete((40, 50));
        recording.Reclaim();

        Assert.Equal([0, 1, 2, 3, 5, 6, 7, 8, 9], recording.FrameIndexes(Assert.Single(recording.SegmentIds())));
        Assert.DoesNotContain(recording.EveryFrameOnDisk(), frame => Psnr(frame, Secret()) > MinimumPsnr);
        recording.AssertTidy();
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Theory]
    [InlineData("staged")]   // the new segment's frames are written, nothing switched yet
    [InlineData("switched")] // references moved to the new segment, the old file not deleted yet
    public void ACrashDuringARebuildLosesNothingAndItIsFinishedOnTheNextStart(string crashAfter)
    {
        using var recording = new Recording();
        for (var i = 0; i < 10; i++)
            recording.Add(i);
        recording.AddUnchanged(100);
        recording.FinishRecording();
        var old = recording.Captures[0].Frame.SegmentId;
        var oldFile = recording.FileOf(old);
        recording.Delete((30, 70));

        // Ending the encoder thread right there, like the process dying, without any cleanup.
        using (var crashed = new ManualResetEventSlim())
        {
            using var encoder = new BatchEncoder(recording.Folder.Path, step =>
            {
                if (step != crashAfter)
                    return;
                crashed.Set();
                throw new OperationCanceledException();
            });
            Assert.True(crashed.Wait(Timeout, TestContext.Current.CancellationToken));
        }

        // Whatever the moment, every capture shows its frame, from one segment or the other.
        Assert.Equal(1, recording.Count("SELECT count(*) FROM dead_segment_files"));
        if (crashAfter == "staged")
        {
            Assert.Equal([old], recording.SegmentIds());
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(oldFile)!, "*-r1.ivf.staging"));
        }
        else
        {
            Assert.NotEqual(old, Assert.Single(recording.SegmentIds()));
            Assert.True(File.Exists(oldFile));
        }
        recording.AssertFramesShowWhatWasCaptured();

        // The next start cleans up and finishes.
        recording.Reclaim();
        var rebuilt = Assert.Single(recording.SegmentIds());
        Assert.Equal(SegmentState.Encoded, recording.State(rebuilt));
        Assert.Equal([0, 1, 2, 7, 8, 9], recording.FrameIndexes(rebuilt));
        Assert.False(File.Exists(oldFile));
        recording.AssertTidy();
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Fact]
    public void ADayAlreadyLoadedKeepsShowingFramesThroughARebuild()
    {
        using var recording = new Recording();
        for (var i = 0; i < 10; i++)
            recording.Add(i);
        recording.FinishRecording();
        recording.Delete((30, 70));

        // The day as Day review loaded it before the rebuild, with the segment open in its provider.
        var loaded = recording.Captures.Select(c => (Frame: recording.CurrentFrame(c.CaptureId), c.Image)).ToList();
        using var provider = new FrameProvider(recording.Folder.Path, cacheBudgetBytes: 1);
        void AssertLoadedFramesShow()
        {
            foreach (var (frame, image) in loaded)
                AssertShows(provider.Get(frame), image);
        }
        AssertLoadedFramesShow();

        // At each step and afterwards, the frames loaded before still read: the old file is still open.
        var steps = new List<string>();
        recording.Reclaim(step =>
        {
            AssertLoadedFramesShow();
            steps.Add(step);
        });
        Assert.Equal(["staged", "switched"], steps);
        AssertLoadedFramesShow();

        // Once the provider lets go of the old segment, those frames are gone; the reloaded day has them.
        provider.CloseIdle(TimeSpan.Zero);
        var missing = Assert.Throws<FrameUnavailableException>(() => provider.Get(loaded[0].Frame));
        Assert.Equal(loaded[0].Frame, missing.Frame);
        foreach (var capture in recording.Captures)
            AssertShows(provider.Get(recording.CurrentFrame(capture.CaptureId)), capture.Image);
    }

    [Fact]
    public void FramesReadWhileARebuildRunsAreAlwaysWhole()
    {
        using var recording = new Recording();
        for (var i = 0; i < 30; i++)
            recording.Add(i);
        recording.FinishRecording();
        recording.Delete((50, 200));
        var remaining = recording.Captures.ToList();

        // Like Day review: read the frame each capture shows now, reloading it when it has moved.
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        var reads = 0;
        var reader = new Thread(() =>
        {
            try
            {
                using var db = WorklapseDatabase.Open(recording.Folder.Path);
                using var provider = new FrameProvider(recording.Folder.Path, cacheBudgetBytes: 1);
                while (!done.IsSet)
                {
                    foreach (var capture in remaining)
                    {
                        DecodedFrame frame;
                        try
                        {
                            frame = provider.Get(Recording.CurrentFrame(db, capture.CaptureId));
                        }
                        catch (FrameUnavailableException)
                        {
                            frame = provider.Get(Recording.CurrentFrame(db, capture.CaptureId));
                        }
                        AssertShows(frame, capture.Image);
                        reads++;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        reader.Start();
        recording.Reclaim();
        done.Set();
        reader.Join();

        Assert.Null(failure);
        Assert.True(reads > 0);
        Assert.Equal([0, 1, 2, 3, 4, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29], recording.FrameIndexes(Assert.Single(recording.SegmentIds())));
    }

    [Fact]
    public void EncodingAStagedSegmentLeavesOutDeletedFramesButNotOnesShownAgain()
    {
        using var recording = new Recording();
        for (var i = 0; i < 8; i++)
            recording.Add(i);
        var segment = recording.Captures[0].Frame.SegmentId;

        // Deleted while the segment is still being recorded into: reclaiming leaves it alone for now.
        recording.Delete((0, 20), (40, 50));
        recording.Sink.Encoder.RequestReclaim();
        Assert.True(recording.Sink.Encoder.WaitUntilIdle(Timeout));
        Assert.Equal(SegmentState.Staged, recording.State(segment));
        Assert.Equal(8, recording.FrameIndexes(segment).Count);

        // Recording carries on. Frame 9's capture is deleted, but the next capture shows it again (unchanged).
        recording.Add(8);
        recording.Add(9);
        recording.Delete((90, 100));
        recording.AddUnchanged(100);
        recording.FinishRecording();

        Assert.Equal(SegmentState.Encoded, recording.State(segment));
        Assert.Equal([2, 3, 5, 6, 7, 8, 9], recording.FrameIndexes(segment));
        Assert.Equal([2], recording.KeyFrames(segment));
        recording.AssertTidy();
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Fact]
    public void AStagedSegmentWithEveryFrameDeletedIsRemovedInsteadOfEncoded()
    {
        using var recording = new Recording();
        for (var i = 0; i < 3; i++)
            recording.Add(i);
        var stagingFile = StagingFile.PathFor(recording.FileOf(recording.Captures[0].Frame.SegmentId));
        recording.Delete((0, 30));
        recording.FinishRecording();

        Assert.Empty(recording.SegmentIds());
        Assert.False(File.Exists(stagingFile));
        recording.AssertTidy();
    }

    [Fact]
    public void FilesListedAsDeadAreDeletedUnlessASegmentUsesThem()
    {
        using var recording = new Recording();
        recording.Add(0);
        recording.FinishRecording();
        var live = recording.FileOf(recording.Captures[0].Frame.SegmentId);
        var orphan = Path.Combine(recording.Folder.Path, "video", "orphan.ivf");
        File.WriteAllText(orphan, "left by a crash");
        File.WriteAllText(StagingFile.PathFor(orphan), "left by a crash");
        recording.Db.Execute($"""
            INSERT INTO dead_segment_files (path) VALUES ('video/orphan.ivf'),
                ('{Path.GetRelativePath(recording.Folder.Path, live).Replace('\\', '/')}');
            """);

        recording.Reclaim();

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(StagingFile.PathFor(orphan)));
        Assert.True(File.Exists(live));
        Assert.Equal(0, recording.Count("SELECT count(*) FROM dead_segment_files"));
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Fact]
    public void UpgradingTheDatabaseQueuesSegmentsWithFramesDeletedBefore()
    {
        using var folder = new TempFolder();
        long partlyDeleted;
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            // Schema 3, as left by deletes made before space was reclaimed.
            db.Execute("""
                DROP INDEX ix_captures_task_mark; ALTER TABLE captures DROP COLUMN task_mark;
                DROP TABLE suggestion_breaks; DROP TABLE suggestions; DROP TABLE screen_words;
                ALTER TABLE apps DROP COLUMN read_screen; ALTER TABLE apps DROP COLUMN learn;
                DROP TABLE timesnapper_imports; DROP TABLE title_rules; DROP TABLE timesheet; DROP TABLE day_notes; DROP TABLE project_notes; DROP TABLE project_spans;
                ALTER TABLE apps DROP COLUMN project_id; ALTER TABLE sites DROP COLUMN project_id; DROP TABLE projects; DROP TABLE categories;
                ALTER TABLE apps DROP COLUMN excluded; DROP INDEX ix_segments_path; ALTER TABLE segments DROP COLUMN format;
                ALTER TABLE captures DROP COLUMN layout_id; DROP TABLE layouts;
                DROP INDEX ix_captures_site; ALTER TABLE captures DROP COLUMN site_id; DROP TABLE sites;
                DROP TABLE reclaim_queue; DROP TABLE dead_segment_files; DROP INDEX ix_frames_segment; DROP INDEX ix_captures_app;
                PRAGMA user_version = 3;
                """);
            var store = new RecordingStore(db);
            var monitor = store.GetOrAddMonitor(Monitor);
            long Segment(string path)
            {
                using var insert = db.Command($"INSERT INTO segments (monitor_id, path, width, height, quality, created_ms, state) VALUES ({monitor}, '{path}', {Width}, {Height}, 40, 0, 1) RETURNING id;");
                return (long)insert.ExecuteScalar()!;
            }
            partlyDeleted = Segment("video/a.ivf");
            var untouched = Segment("video/b.ivf");
            long Capture(int seconds)
            {
                using var insert = db.Command($"INSERT INTO captures (ts_ms, utc_offset_min, kind) VALUES ({Nine.AddSeconds(seconds).ToUnixTimeMilliseconds()}, 0, 0) RETURNING id;");
                return (long)insert.ExecuteScalar()!;
            }
            for (var i = 0; i < 3; i++)
            {
                store.InsertSegmentFrame(partlyDeleted, i, 0, 1, i == 0);
                store.InsertSegmentFrame(untouched, i, 0, 1, i == 0);
                store.InsertFrame(Capture(i), monitor, new FrameRef(untouched, i));
            }
            store.InsertFrame(Capture(0), monitor, new FrameRef(partlyDeleted, 1));
        }

        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            using var version = db.Command("PRAGMA user_version;");
            Assert.Equal(16L, version.ExecuteScalar());
            using var queued = db.Command("SELECT group_concat(segment_id) FROM reclaim_queue;");
            Assert.Equal(partlyDeleted.ToString(), queued.ExecuteScalar());
        }
    }

    [Fact]
    public async Task ADeleteInTheSegmentBeingRecordedLeavesTheDiskAtTheNextTick()
    {
        using var folder = new TempFolder();
        await using var run = RecorderRun.Start(folder.Path);
        run.Capture(0, Image(0));
        run.Capture(10, Secret());
        run.Capture(20, Image(2));
        Assert.Contains(Recording.FramesOnDisk(folder.Path), frame => Psnr(frame, Secret()) > MinimumPsnr);

        DeleteResult result;
        using (var history = new HistoryStore(WorklapseDatabase.Open(folder.Path)))
        {
            history.SetPendingDelete(Nine.AddSeconds(10), Nine.AddSeconds(20), pending: true);
            result = history.DeleteMarked(DateOnly.FromDateTime(Nine.LocalDateTime));
        }
        Assert.Equal(new DeleteResult(1, StagedSegmentsAffected: true), result);

        // What the app does then: the recorder closes the segment before its next capture, and it's encoded
        // without the deleted frame. Recording carries on in a new segment.
        run.Recorder.CloseSegmentsSoon();
        run.Capture(30, Image(3));
        Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout));
        Assert.DoesNotContain(Recording.FramesOnDisk(folder.Path), frame => Psnr(frame, Secret()) > MinimumPsnr);
    }

    [Fact]
    public async Task DeletesLeaveTheDiskWhileRecordingIsPaused()
    {
        using var recording = new Recording();
        for (var i = 0; i < 6; i++)
            recording.Add(i, i == 3 ? Secret() : null);
        recording.FinishRecording();
        recording.Delete((30, 40));

        // Paused from the start: no capture is taken, but the encoder that removes deleted video runs.
        await using var run = RecorderRun.Start(recording.Folder.Path, paused: true);
        Assert.Equal(RecorderState.Paused, run.Recorder.Status.State);
        Assert.True(run.Sink!.Encoder.WaitUntilIdle(Timeout));
        Assert.DoesNotContain(recording.EveryFrameOnDisk(), frame => Psnr(frame, Secret()) > MinimumPsnr);
        recording.AssertFramesShowWhatWasCaptured();
    }

    [Fact]
    public void DeletedTitlesAndAppNamesAreNotLeftInTheDatabaseFiles()
    {
        using var folder = new TempFolder();
        const string SecretTitle = "Quarterly figures for a client nobody else may see";
        const string SecretApp = "secret-game.exe";
        var day = DateOnly.FromDateTime(Nine.LocalDateTime);
        // Like the recorder's: while any connection is open, SQLite keeps the log file (with the pages as
        // they were) instead of folding it in and deleting it when the last one closes.
        using var recorderConnection = WorklapseDatabase.Open(folder.Path);
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            var work = store.GetOrAddApp("work.exe", null);
            var game = store.GetOrAddApp(SecretApp, null);
            var productive = store.GetOrAddApp("editor.exe", null);
            db.Execute($"UPDATE apps SET productive = 1 WHERE id = {productive};");
            for (var i = 0; i < 200; i++)
                store.InsertCapture(Nine.AddSeconds(10 * i), CaptureKind.Active, i is >= 50 and < 60 ? game : work,
                    i is >= 50 and < 60 ? SecretTitle : $"Ordinary window {i}", null);
            store.InsertCapture(Nine.AddSeconds(5000), CaptureKind.Active, productive, SecretTitle, null);
        }
        Assert.True(FilesContain(folder.Path, SecretTitle) && FilesContain(folder.Path, SecretApp));

        using (var history = new HistoryStore(WorklapseDatabase.Open(folder.Path)))
        {
            history.SetPendingDelete(Nine.AddSeconds(500), Nine.AddSeconds(600), pending: true);
            history.SetPendingDelete(Nine.AddSeconds(5000), Nine.AddSeconds(5001), pending: true);
            Assert.Equal(11, history.DeleteMarked(day).Captures);
            // The productive app keeps its row (and its flag) though nothing uses it now; its name isn't private.
            Assert.Equal(["editor.exe", "work.exe"], AppNames(history.Database));
        }

        // The encoder's pass empties the database log once it's done.
        using (var encoder = new BatchEncoder(folder.Path))
            Assert.True(encoder.WaitUntilIdle(Timeout));
        Assert.False(FilesContain(folder.Path, SecretTitle), "the deleted title is still in the database files");
        Assert.False(FilesContain(folder.Path, SecretApp), "the deleted app's name is still in the database files");
    }

    [Fact]
    public void WhatKeepsDeletedVideoOnDiskIsReportedUntilItIsGone()
    {
        using var recording = new Recording();
        for (var i = 0; i < 6; i++)
            recording.Add(i);
        recording.FinishRecording();
        var file = recording.FileOf(recording.Captures[0].Frame.SegmentId);
        recording.Delete((0, 60));

        // Something keeps the file from being deleted.
        string? reported;
        using (HoldAgainstDeletion(file))
        {
            using var encoder = new BatchEncoder(recording.Folder.Path);
            Assert.True(encoder.WaitUntilIdle(Timeout));
            reported = encoder.DeletedContentLeft;
            Assert.True(File.Exists(file));
        }
        Assert.NotNull(reported);
        Assert.Contains(Path.GetFileName(file), reported);

        // Once it's let go, the next pass deletes it and the report clears.
        using (var encoder = new BatchEncoder(recording.Folder.Path))
        {
            Assert.True(encoder.WaitUntilIdle(Timeout));
            Assert.Null(encoder.DeletedContentLeft);
        }
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ASegmentThatCantBeRebuiltIsReported()
    {
        using var recording = new Recording();
        for (var i = 0; i < 6; i++)
            recording.Add(i);
        recording.FinishRecording();
        var file = recording.FileOf(recording.Captures[0].Frame.SegmentId);
        recording.Delete((20, 30));
        File.WriteAllBytes(file, new byte[100]); // damaged

        using var encoder = new BatchEncoder(recording.Folder.Path);
        Assert.True(encoder.WaitUntilIdle(Timeout));
        Assert.NotNull(encoder.DeletedContentLeft);
        Assert.Contains(Path.GetFileName(file), encoder.DeletedContentLeft);
    }

    /// <summary>
    /// Keeps a file from being deleted until disposed. On Windows: another program has it open without letting it be
    /// deleted. Elsewhere an open file can always be deleted; what stops it is a folder that can't be changed.
    /// </summary>
    private static IDisposable HoldAgainstDeletion(string file)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var folder = Path.GetDirectoryName(file)!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() =>
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(folder, mode);
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static bool FilesContain(string dataFolder, string text)
    {
        var needle = System.Text.Encoding.UTF8.GetBytes(text);
        foreach (var path in Directory.GetFiles(dataFolder, WorklapseDatabase.FileName + "*"))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(needle) >= 0)
                return true;
        }
        return false;
    }

    private static List<string> AppNames(WorklapseDatabase db)
    {
        using var command = db.Command("SELECT exe_name FROM apps ORDER BY exe_name;");
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static void AssertShows(DecodedFrame frame, byte[] image)
    {
        try
        {
            var psnr = Psnr(frame.I420, image);
            Assert.True(psnr > MinimumPsnr, $"PSNR {psnr:F1} dB");
        }
        finally
        {
            frame.Release();
        }
    }

    private static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        var mse = sum / a.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>A gradient with a moving bar and some fine detail, different for each index.</summary>
    private static byte[] Image(int index)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = (y * Width + x) * 4;
            var bar = Math.Abs(x - index * 20 % Width) < 8;
            pixels[p] = (byte)(bar ? 30 : x * 255 / Width);
            pixels[p + 1] = (byte)(bar ? 30 : y * 255 / Height);
            pixels[p + 2] = (byte)((x / 8 + y / 8) % 2 == 0 ? 220 : 160);
            pixels[p + 3] = 255;
        }
        var frame = new byte[I420.Size(Width, Height)];
        I420.FromBgraRows(pixels, Width * 4, Width, Height, 0, Height, frame);
        return frame;
    }

    /// <summary>A picture unlike any <see cref="Image"/>: what must not stay on disk once deleted.</summary>
    private static byte[] Secret()
    {
        var frame = Image(0);
        for (var i = 0; i < frame.Length; i++)
            frame[i] = (byte)(255 - frame[i]);
        return frame;
    }

    /// <summary>The recorder on a test folder, with a fake screen and clock, ticked by hand.</summary>
    private sealed class RecorderRun : IAsyncDisposable
    {
        private readonly Clock _clock = new();
        private readonly FakeScreen _screen = new();

        public Recorder Recorder { get; private set; } = null!;

        /// <summary>The recorder's current sink (it makes a new one after an error).</summary>
        public FrameSink? Sink { get; private set; }

        public static RecorderRun Start(string dataFolder, bool paused = false)
        {
            var run = new RecorderRun();
            var settings = new SettingsStore(Path.Combine(dataFolder, "settings.json"));
            settings.Save(new WorklapseSettings { DataFolder = dataFolder, CaptureMode = CaptureMode.AllMonitors, IdleMinutes = 120 });
            var platform = new PlatformServices(run._screen, new FakeForeground(), new FakeIdle(), new FakeSession());
            run.Recorder = new Recorder(settings, platform, store => run.Sink = new FrameSink(store, () => (FrameFormat.Video, 30)), run._clock);
            if (paused)
            {
                run.Recorder.IsPaused = true;
                run._clock.Now = Nine;
                run.Recorder.Tick();
            }
            return run;
        }

        /// <summary>A tick at 09:00 + <paramref name="seconds"/> with the screen showing <paramref name="image"/>.</summary>
        public void Capture(int seconds, byte[] image)
        {
            _clock.Now = Nine.AddSeconds(seconds);
            _screen.Next = image;
            Recorder.Tick();
        }

        public ValueTask DisposeAsync() => Recorder.DisposeAsync();

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = Nine;
            public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
        }

        private sealed class FakeScreen : IScreenSource
        {
            private ulong _captures; // every capture counts as changed
            public byte[] Next { get; set; } = Image(0);
            public IReadOnlyList<MonitorInfo> GetMonitors() => [Monitor];

            // A copy: the recorder may change the buffer in place.
            public ScreenCapture? Capture(MonitorInfo monitor) =>
                new() { Monitor = monitor, Width = Width, Height = Height, Frame = Next.ToArray(), Hash = ++_captures };

            public void Dispose() { }
        }

        private sealed class FakeForeground : IForegroundWindowProbe
        {
            public ForegroundWindow? GetForegroundWindow() => new("work.exe", null, "Work", new PixelRect(0, 0, Width, Height));
        }

        private sealed class FakeIdle : IIdleDetector
        {
            public TimeSpan GetIdleTime() => TimeSpan.Zero;
        }

        private sealed class FakeSession : ISessionStateMonitor
        {
            public bool IsLocked => false;
            public void Dispose() { }
        }
    }

    /// <summary>One monitor recorded into a test folder, a capture every 10 s from 09:00.</summary>
    private sealed class Recording : IDisposable
    {
        public Recording()
        {
            Db = WorklapseDatabase.Open(Folder.Path);
            Store = new RecordingStore(Db);
            MonitorId = Store.GetOrAddMonitor(Monitor);
            Sink = new FrameSink(Store, () => (FrameFormat.Video, 30));
        }

        public TempFolder Folder { get; } = new();
        public WorklapseDatabase Db { get; }
        public RecordingStore Store { get; }
        public FrameSink Sink { get; }
        public long MonitorId { get; }

        /// <summary>The captures not deleted, with the frame each was recorded with and the image it shows.</summary>
        public List<(long CaptureId, int Seconds, FrameRef Frame, byte[] Image)> Captures { get; } = [];

        /// <summary>A capture with a new picture (<see cref="Image"/> unless given), at 10 s × <paramref name="index"/>.</summary>
        public void Add(int index, byte[]? picture = null)
        {
            var image = picture ?? Image(index);
            var (captureId, frame) = TestRecording.Append(Store, Sink, Monitor, MonitorId, image, Nine.AddSeconds(10 * index));
            Captures.Add((captureId, 10 * index, frame, image));
            _last = (frame, image);
        }

        /// <summary>A capture where the monitor didn't change: it shows the last frame recorded, like the recorder does.</summary>
        public void AddUnchanged(int seconds)
        {
            var captureId = TestRecording.AppendUnchanged(Store, MonitorId, _last.Frame, Nine.AddSeconds(seconds));
            Captures.Add((captureId, seconds, _last.Frame, _last.Image));
        }

        private (FrameRef Frame, byte[] Image) _last;

        /// <summary>Closes and encodes the open segment, then stops the sink's encoder.</summary>
        public void FinishRecording()
        {
            Sink.CloseAll();
            Assert.True(Sink.Encoder.WaitUntilIdle(Timeout));
            Sink.Dispose();
        }

        /// <summary>Marks and deletes the captures in each range of seconds after 09:00, as Day review does.</summary>
        public void Delete(params (int From, int To)[] ranges)
        {
            using var history = new HistoryStore(WorklapseDatabase.Open(Folder.Path));
            foreach (var (from, to) in ranges)
            {
                history.SetPendingDelete(Nine.AddSeconds(from), Nine.AddSeconds(to), pending: true);
                Captures.RemoveAll(c => c.Seconds >= from && c.Seconds < to);
            }
            history.DeleteMarked(DateOnly.FromDateTime(Nine.LocalDateTime));
        }

        /// <summary>Runs a batch encoder on the folder until it has nothing left to do, as on the next start.</summary>
        public void Reclaim(Action<string>? afterStep = null)
        {
            Exception? failed = null;
            using var encoder = new BatchEncoder(Folder.Path, afterStep);
            encoder.ReclaimFailed += (_, e) => failed = e.Error;
            encoder.EncodingFailed += (_, e) => failed = e.Error;
            Assert.True(encoder.WaitUntilIdle(Timeout));
            Assert.Null(failed);
        }

        public List<byte[]> EveryFrameOnDisk() => FramesOnDisk(Folder.Path);

        /// <summary>
        /// Every frame in every video file in a data folder, decoded straight from the files: what anyone with
        /// the folder could see, whatever the database says.
        /// </summary>
        public static List<byte[]> FramesOnDisk(string dataFolder)
        {
            var frames = new List<byte[]>();
            foreach (var path in Directory.GetFiles(Path.Combine(dataFolder, "video"), "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith(".staging"))
                {
                    using var staging = new StagingReader(path);
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    for (var offset = (long)StagingFile.HeaderSize; offset < file.Length;)
                    {
                        var header = new byte[StagingFile.RecordHeaderSize];
                        file.Position = offset;
                        file.ReadExactly(header);
                        var length = StagingFile.RecordHeaderSize + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                        staging.Apply(offset, length);
                        frames.Add(staging.Frame.ToArray());
                        offset += length;
                    }
                }
                else
                {
                    using var file = File.OpenRead(path);
                    using var decoder = new Av1Decoder(threads: 1);
                    foreach (var (offset, length, _) in IvfReader.EnumerateFrames(file).ToList())
                    {
                        decoder.Decode(IvfReader.ReadFrame(file, offset, length));
                        var frame = new byte[I420.Size(decoder.Width, decoder.Height)];
                        decoder.CopyI420(frame);
                        frames.Add(frame);
                    }
                }
            }
            return frames;
        }

        public FrameRef CurrentFrame(long captureId) => CurrentFrame(Db, captureId);

        public static FrameRef CurrentFrame(WorklapseDatabase db, long captureId)
        {
            using var command = db.Command("SELECT segment_id, frame_index FROM frames WHERE capture_id = $id;");
            command.Parameters.AddWithValue("$id", captureId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return new FrameRef(reader.GetInt64(0), reader.GetInt32(1));
        }

        public string FileOf(long segmentId)
        {
            using var command = Db.Command("SELECT path FROM segments WHERE id = $id;");
            command.Parameters.AddWithValue("$id", segmentId);
            return Path.Combine(Folder.Path, (string)command.ExecuteScalar()!);
        }

        public SegmentState State(long segmentId) => (SegmentState)Count("SELECT state FROM segments WHERE id = " + segmentId);

        public List<long> SegmentIds() => Column<long>("SELECT id FROM segments ORDER BY id;");

        public List<int> FrameIndexes(long segmentId) =>
            Column<int>($"SELECT frame_index FROM segment_frames WHERE segment_id = {segmentId} ORDER BY frame_index;");

        public List<int> KeyFrames(long segmentId) =>
            Column<int>($"SELECT frame_index FROM segment_frames WHERE segment_id = {segmentId} AND is_key = 1 ORDER BY frame_index;");

        public long Count(string sql)
        {
            using var command = Db.Command(sql);
            return (long)command.ExecuteScalar()!;
        }

        /// <summary>Every capture left shows its picture, read through a new provider.</summary>
        public void AssertFramesShowWhatWasCaptured()
        {
            using var provider = new FrameProvider(Folder.Path);
            foreach (var capture in Captures)
                AssertShows(provider.Get(CurrentFrame(capture.CaptureId)), capture.Image);
        }

        /// <summary>No files left behind or still to delete, and nothing queued.</summary>
        public void AssertTidy()
        {
            Assert.Equal(0, Count("SELECT count(*) FROM dead_segment_files"));
            Assert.Equal(0, Count("SELECT count(*) FROM reclaim_queue"));
            var expected = new List<string>();
            using (var command = Db.Command("SELECT path, state FROM segments;"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var path = Path.GetFullPath(Path.Combine(Folder.Path, reader.GetString(0)));
                    expected.Add((SegmentState)reader.GetInt32(1) == SegmentState.Staged ? StagingFile.PathFor(path) : path);
                }
            }
            var video = Path.Combine(Folder.Path, "video");
            var actual = Directory.Exists(video) ? Directory.GetFiles(video, "*", SearchOption.AllDirectories) : [];
            Assert.Equal(expected.Order(), actual.Select(Path.GetFullPath).Order());
        }

        public void Dispose()
        {
            Sink.Dispose();
            Db.Dispose();
            Folder.Dispose();
        }

        private List<T> Column<T>(string sql)
        {
            using var command = Db.Command(sql);
            using var reader = command.ExecuteReader();
            var values = new List<T>();
            while (reader.Read())
                values.Add(reader.GetFieldValue<T>(0));
            return values;
        }
    }
}
