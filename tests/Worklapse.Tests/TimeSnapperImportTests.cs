using Worklapse.Core.History;
using Worklapse.Core.Imaging;
using Worklapse.Core.Import;
using Worklapse.Core.Platform;
using Worklapse.Core.Storage;
using Worklapse.Video;

namespace Worklapse.Tests;

/// <summary>Importing TimeSnapper's history: what comes in, what's left out, and its pictures, left where they are.</summary>
public class TimeSnapperImportTests
{
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly DateTime Nine = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Local);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    private static readonly TimeSnapperProgram Firefox = new(1, "FIREFOX.EXE", @"C:\PROGRAM FILES\MOZILLA FIREFOX\FIREFOX.EXE", IsSite: false);
    private static readonly TimeSnapperProgram Mail = new(2, "MAIL.GOOGLE.COM", "MAIL.GOOGLE.COM", IsSite: true);
    private static readonly TimeSnapperProgram TitleAsSite = new(3, "GITHUB - GITHUB.COM", "GITHUB - GITHUB.COM", IsSite: true);
    private static readonly TimeSnapperProgram Rider = new(4, "RIDER64.EXE", @"C:\TOOLS\RIDER\BIN\RIDER64.EXE", IsSite: false);
    private static readonly TimeSnapperProgram Unknown = new(5, "(UNKNOWN)", null, IsSite: false);

    [Fact]
    public void CapturesComeInWithTheirPicturesLeftWhereTheyAre()
    {
        using var data = new TempFolder();
        using var snapshots = new TempFolder();
        var first = WriteJpeg(snapshots, "09.00.00.000.jpg", 101, 57, seed: 1); // odd sizes, as windows are
        var second = WriteJpeg(snapshots, "09.00.10.000.jpg", 64, 48, seed: 2);
        var source = new FakeData(
            Activity(1, 0, first, "firefox.exe", "Inbox - Gmail - Mozilla Firefox", Firefox, Mail),
            Activity(2, 10, second, "rider64.exe", "Worklapse – Program.cs", Rider));

        var result = Import(data, source);

        Assert.Equal(new TimeSnapperResult(2, 0, 0, 0, 0, 0), result);
        using var history = new HistoryStore(WorklapseDatabase.Open(data.Path));
        var day = history.LoadDay(Day);
        Assert.Equal([new DateTimeOffset(Nine), new DateTimeOffset(Nine.AddSeconds(10))], day.Captures.Select(c => c.Time));
        Assert.All(day.Captures, c => Assert.True(c.IsActive));
        Assert.Equal(["firefox.exe", "rider64.exe"], day.Captures.Select(c => c.AppName));
        Assert.Equal(["mail.google.com", null], day.Captures.Select(c => c.Site));
        Assert.Equal(["Inbox - Gmail - Mozilla Firefox", "Worklapse – Program.cs"], day.Captures.Select(c => c.WindowTitle));

        // Shown on a monitor of their own, read from where TimeSnapper put them, at their own sizes.
        var monitor = Assert.Single(day.Monitors);
        Assert.True(monitor.IsTimeSnapper);
        Assert.All(day.Captures, c => Assert.Equal([monitor.Id], c.MonitorIds));
        using var provider = new FrameProvider(data.Path);
        AssertShows(provider, day.Captures[0], 101, 57, seed: 1);
        AssertShows(provider, day.Captures[1], 64, 48, seed: 2);
        Assert.True(File.Exists(first) && File.Exists(second));
        Assert.Empty(Directory.GetFileSystemEntries(data.Path).Where(p => Directory.Exists(p)));
    }

    [Fact]
    public void CapturesWhosePictureWasDeletedAreLeftOutExceptForUnknownPrograms()
    {
        using var data = new TempFolder();
        using var snapshots = new TempFolder();
        var png = Path.Combine(snapshots.Path, "09.00.30.000.png");
        File.WriteAllBytes(png, [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        var source = new FakeData(
            Activity(1, 0, null, "firefox.exe", "deleted in TimeSnapper", Firefox),
            Activity(2, 10, Path.Combine(snapshots.Path, "gone.jpg"), "firefox.exe", "deleted since", Firefox),
            Activity(3, 20, null, TimeSnapperImport.UnknownProgram, "Task Manager", Unknown),
            Activity(4, 30, png, "firefox.exe", "TimeSnapper's first captures were PNGs", Firefox));

        var result = Import(data, source);

        Assert.Equal(new TimeSnapperResult(Imported: 2, WithoutPicture: 2, 0, 0, PictureDeleted: 2, 0), result);
        using var history = new HistoryStore(WorklapseDatabase.Open(data.Path));
        var day = history.LoadDay(Day);
        Assert.Equal([new DateTimeOffset(Nine.AddSeconds(20)), new DateTimeOffset(Nine.AddSeconds(30))], day.Captures.Select(c => c.Time));
        Assert.Equal([null, "firefox.exe"], day.Captures.Select(c => c.AppName));
        Assert.Equal("Task Manager", day.Captures[0].WindowTitle);
        Assert.All(day.Captures, c => Assert.Empty(c.Frames));
    }

    [Fact]
    public void WorklapseDecidesForTheTimeItRecordedActiveOrIdleAndTimeSnapperFillsTheRest()
    {
        using var data = new TempFolder();
        using var snapshots = new TempFolder();
        // Worklapse at a 30 s interval (a capture counts for up to 45 s): active, active, idle, idle, then paused
        // until 09:05:00.
        using (var db = WorklapseDatabase.Open(data.Path))
        {
            var store = new RecordingStore(db);
            foreach (var (seconds, kind) in new[] { (0, CaptureKind.Active), (30, CaptureKind.Active), (60, CaptureKind.Idle), (90, CaptureKind.Idle), (300, CaptureKind.Active) })
                store.InsertCapture(new DateTimeOffset(Nine.AddSeconds(seconds)), kind, null, "Worklapse's own", null);
        }
        // TimeSnapper every 10 s, from 08:59:50 to 09:05:10.
        var source = new FakeData([.. Enumerable.Range(-1, 33).Select(i =>
            Activity(i + 2, 10 * i, WriteJpeg(snapshots, $"{i}.jpg", 32, 32, seed: i), "firefox.exe", $"TimeSnapper {10 * i}", Firefox))]);

        var result = Import(data, source, maxCaptureLength: TimeSpan.FromSeconds(45));

        // Left out from 09:00:00 to 09:02:15 (the last idle capture counts for 45 s) and from 09:05:00.
        Assert.Equal(new TimeSnapperResult(17, 0, 0, RecordedByWorklapse: 16, 0, 0), result);
        int[] imported = [-10, .. Enumerable.Range(14, 16).Select(i => 10 * i)];
        Assert.Equal([.. new[] { 0, 30, 60, 90, 300 }.Concat(imported).Order()], Seconds(data));
        using var history = new HistoryStore(WorklapseDatabase.Open(data.Path));
        var idle = history.LoadDay(Day).Captures.Where(c => !c.IsActive).Select(c => (int)(c.Time - new DateTimeOffset(Nine)).TotalSeconds);
        Assert.Equal([60, 90], idle);
    }

    [Fact]
    public void CapturesImportedBeforeAreLeftOutEvenOnceDeleted()
    {
        using var data = new TempFolder();
        using var snapshots = new TempFolder();
        var source = new FakeData([.. new[] { 0, 10, 20 }.Select((seconds, i) =>
            Activity(i + 1, seconds, WriteJpeg(snapshots, $"{i}.jpg", 32, 32, seed: i), "firefox.exe", $"TimeSnapper {seconds}", Firefox))]);
        Assert.Equal(new TimeSnapperResult(3, 0, 0, 0, 0, 0), Import(data, source));

        // Again: nothing new. A capture imported and then deleted doesn't come back either.
        Assert.Equal(new TimeSnapperResult(0, 0, AlreadyImported: 3, 0, 0, 0), Import(data, source.Again()));
        using (var history = new HistoryStore(WorklapseDatabase.Open(data.Path)))
        {
            history.SetPendingDelete(new DateTimeOffset(Nine.AddSeconds(20)), new DateTimeOffset(Nine.AddSeconds(21)), pending: true);
            Assert.Equal(1, history.DeleteMarked(Day).Captures);
        }
        Assert.Equal(new TimeSnapperResult(0, 0, AlreadyImported: 3, 0, 0, 0), Import(data, source.Again()));
        Assert.Equal([0, 10], Seconds(data));
    }

    [Fact]
    public void DeletingAnImportedCaptureDeletesItsPicture()
    {
        using var data = new TempFolder();
        using var snapshots = new TempFolder();
        var kept = WriteJpeg(snapshots, "09.00.00.000.jpg", 40, 30, seed: 1);
        var secret = WriteJpeg(snapshots, "09.00.10.000.jpg", 40, 30, seed: 2);
        Import(data, new FakeData(
            Activity(1, 0, kept, "firefox.exe", "kept", Firefox),
            Activity(2, 10, secret, "firefox.exe", "secret", Firefox)));

        using (var history = new HistoryStore(WorklapseDatabase.Open(data.Path)))
        {
            history.SetPendingDelete(new DateTimeOffset(Nine.AddSeconds(10)), new DateTimeOffset(Nine.AddSeconds(11)), pending: true);
            history.DeleteMarked(Day);
        }
        using (var encoder = new BatchEncoder(data.Path))
        {
            encoder.RequestReclaim();
            Assert.True(encoder.WaitUntilIdle(Timeout));
        }

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(secret));
        using var db = WorklapseDatabase.Open(data.Path);
        Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM segments"));
        Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM dead_segment_files"));
        Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM timesnapper_imports WHERE capture_id IS NULL"));
    }

    [Fact]
    public void WhatTimeSnapperCountsProductiveIsMarkedOnTaskAndNothingIsUnmarked()
    {
        using var data = new TempFolder();
        using var snapshots = new TempFolder();
        using (var db = WorklapseDatabase.Open(data.Path))
        {
            var store = new RecordingStore(db);
            var code = store.GetOrAddApp("Code.exe", null);
            store.InsertCapture(new DateTimeOffset(Nine.AddHours(-1)), CaptureKind.Active, code, "Worklapse's own", null);
            using var history = new HistoryStore(WorklapseDatabase.Open(data.Path));
            history.SetOnTask(new ProgramKey(ProgramKind.App, "Code.exe"), true);
        }
        var source = new FakeData(
            Activity(1, 0, WriteJpeg(snapshots, "1.jpg", 32, 32, 1), "firefox.exe", "Inbox", Firefox, Mail),
            Activity(2, 10, WriteJpeg(snapshots, "2.jpg", 32, 32, 2), "rider64.exe", "Program.cs", Rider),
            Activity(3, 20, WriteJpeg(snapshots, "3.jpg", 32, 32, 3), "firefox.exe", "GitHub", Firefox, TitleAsSite))
        {
            Classified = new HashSet<int> { Mail.Id, Rider.Id, TitleAsSite.Id },
        };

        Import(data, source);

        using var store2 = new HistoryStore(WorklapseDatabase.Open(data.Path));
        var programs = store2.GetPrograms(TimeSpan.FromSeconds(10)).ToDictionary(p => p.Program, p => p.OnTask);
        Assert.Equal(new Dictionary<ProgramKey, bool>
        {
            [new(ProgramKind.App, "Code.exe")] = true,
            [new(ProgramKind.App, "firefox.exe")] = false,
            [new(ProgramKind.App, "rider64.exe")] = true,
            [new(ProgramKind.Site, "mail.google.com")] = true,
        }, programs);
    }

    [Theory]
    [InlineData("MAIL.GOOGLE.COM", "mail.google.com")]
    [InlineData("DEV.PRESAYO.COM:7078", "dev.presayo.com")]
    [InlineData("LOCALHOST:5080", "localhost")]
    [InlineData("172.16.1.10:8006", "172.16.1.10")]
    [InlineData("GITHUB - GITHUB.COM", null)]
    [InlineData("CALENDAR.GOOGLE.COM - VISIT", null)]
    [InlineData("C:/USERS", null)]
    [InlineData("/C:", null)]
    [InlineData("SETTINGS", null)]
    [InlineData("1720", null)]
    [InlineData("172.1:7077", null)]
    [InlineData("675B373D-C514-49FA-926A-E3EE9F3AB2A9", null)]
    public void SitesAreHostNamesAsWorklapseRecordsThem(string timeSnapper, string? expected) =>
        Assert.Equal(expected, TimeSnapperImport.SiteName(timeSnapper));

    [Fact]
    public void AJpegsSizeComesFromItsFrameHeader()
    {
        using var snapshots = new TempFolder();
        var path = WriteJpeg(snapshots, "a.jpg", 333, 77, seed: 3);
        using (var file = File.OpenRead(path))
            Assert.Equal((333, 77), TimeSnapperImport.JpegSize(file));
        using var notJpeg = new MemoryStream([0x89, (byte)'P', (byte)'N', (byte)'G']);
        Assert.Null(TimeSnapperImport.JpegSize(notJpeg));
        using var cut = new MemoryStream(File.ReadAllBytes(path)[..20]);
        Assert.Null(TimeSnapperImport.JpegSize(cut));
    }

    /// <param name="maxCaptureLength">Worklapse's: 15 s at the default 10 s interval.</param>
    private static TimeSnapperResult Import(TempFolder data, ITimeSnapperData source, TimeSpan? maxCaptureLength = null)
    {
        using var db = WorklapseDatabase.Open(data.Path);
        return TimeSnapperImport.Run(db, source, maxCaptureLength ?? TimeSpan.FromSeconds(15));
    }

    /// <summary>When the day's captures were taken, in seconds after nine.</summary>
    private static List<int> Seconds(TempFolder data)
    {
        using var history = new HistoryStore(WorklapseDatabase.Open(data.Path));
        return history.LoadDay(Day).Captures.Select(c => (int)(c.Time - new DateTimeOffset(Nine)).TotalSeconds).ToList();
    }

    private static object? Scalar(WorklapseDatabase db, string sql)
    {
        using var command = db.Command(sql);
        return command.ExecuteScalar();
    }

    private static TimeSnapperActivity Activity(int id, int seconds, string? file, string program, string caption,
        TimeSnapperProgram programRow, TimeSnapperProgram? site = null) =>
        new(id, Nine.AddSeconds(seconds), file, program, caption, programRow.Id, site?.Id);

    /// <summary>A test picture as I420, different for each seed.</summary>
    private static byte[] Picture(int width, int height, int seed)
    {
        var frame = new byte[I420.Size(width, height)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                frame[y * width + x] = (byte)(40 + (x * 3 + y * 2 + seed * 50) % 170);
        frame.AsSpan(width * height).Fill(128);
        return frame;
    }

    private static string WriteJpeg(TempFolder folder, string name, int width, int height, int seed)
    {
        var path = Path.Combine(folder.Path, name);
        using var image = ImageCodec.Encode(FrameFormat.Jpeg, Picture(width, height, seed), width, height, quality: 90);
        File.WriteAllBytes(path, image.Data.ToArray());
        return path;
    }

    private static void AssertShows(FrameProvider provider, CaptureRecord capture, int width, int height, int seed)
    {
        var frame = provider.Get(Assert.Single(capture.Frames).Frame);
        try
        {
            Assert.Equal((width, height), (frame.Width, frame.Height));
            var expected = Picture(width, height, seed);
            var luma = frame.I420[..(width * height)];
            var worst = 0;
            for (var i = 0; i < luma.Length; i++)
                worst = Math.Max(worst, Math.Abs(luma[i] - expected[i]));
            Assert.True(worst < 24, $"off by up to {worst}");
        }
        finally
        {
            frame.Release();
        }
    }

    private sealed class FakeData(params TimeSnapperActivity[] activities) : ITimeSnapperData
    {
        public IReadOnlyDictionary<int, TimeSnapperProgram> Programs { get; } =
            new[] { Firefox, Mail, TitleAsSite, Rider, Unknown }.ToDictionary(p => p.Id);

        public IReadOnlySet<int> Classified { get; init; } = new HashSet<int>();
        public int ActivityCount => activities.Length;
        public IEnumerable<TimeSnapperActivity> Activities => activities;

        public FakeData Again() => new(activities) { Classified = Classified };
    }
}
