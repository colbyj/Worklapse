using Worklapse.Core.Imaging;
using Worklapse.Video;

namespace Worklapse.Tests;

public class IvfPlaybackTests
{
    private const int Width = 160, Height = 96;

    // Recorded in UTC+01:00: 09:00:00 local is 08:00:00 UTC.
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void AFolderOpensAsOneTrackPerMonitorInTimeOrderInTheRecordingsTimeZone()
    {
        using var folder = new TempFolder();
        var day = Path.Combine(folder.Path, "video", "2026-11-20");
        Write(Path.Combine(day, "091500-m1.ivf"), Start.AddMinutes(15), 3);
        Write(Path.Combine(day, "090000-m1.ivf"), Start, 4);
        Write(Path.Combine(day, "090000-m2.ivf"), Start, 2);
        File.WriteAllBytes(Path.Combine(day, "notes.ivf"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(day, "091500-m1.ivf.staging"), [1, 2, 3]);

        var recording = IvfLibrary.Open(Path.Combine(folder.Path, "video"));

        Assert.Equal(3, recording.FileCount);
        Assert.Single(recording.Skipped);
        Assert.Equal(["Monitor 1", "Monitor 2"], recording.Tracks.Select(t => t.Name));
        var frames = recording.Tracks[0].Frames;
        Assert.Equal(7, frames.Count);
        Assert.Equal(Start, frames[0].Time);
        Assert.Equal(TimeSpan.FromHours(1), frames[0].Time.Offset);
        Assert.Equal(Start.AddMinutes(15), frames[4].Time);
        Assert.True(frames.Zip(frames.Skip(1)).All(p => p.First.Time < p.Second.Time));
    }

    [Fact]
    public void MonitorsPlayedTogetherEachShowTheirLatestFrameAtEveryMoment()
    {
        using var folder = new TempFolder();
        var day = Path.Combine(folder.Path, "2026-11-20");
        // Monitor 1 changes at 0, 10, 20 and 30 s; monitor 2 starts later, at 10 and 20 s (shared moments), then 25 s.
        Write(Path.Combine(day, "090000-m1.ivf"), Start, 4);
        Write(Path.Combine(day, "090010-m2.ivf"), Start.AddSeconds(10), 2);
        Write(Path.Combine(day, "090025-m2.ivf"), Start.AddSeconds(25), 1);

        var playlist = new IvfPlaylist(IvfLibrary.Open(day).Tracks);

        Assert.Equal([0, 10, 20, 25, 30], playlist.Times.Select(t => (int)(t - Start).TotalSeconds));
        string Shown(int moment) => string.Join(" ", playlist.FramesAt(moment)
            .Select(f => f is null ? "-" : $"{(int)(f.Time - Start).TotalSeconds}"));
        Assert.Equal("0 -", Shown(0));
        Assert.Equal("10 10", Shown(1));
        Assert.Equal("20 20", Shown(2));
        Assert.Equal("20 25", Shown(3));
        Assert.Equal("30 25", Shown(4));
        Assert.True(playlist.ChangesAt(0, 0));
        Assert.False(playlist.ChangesAt(1, 0));
        Assert.False(playlist.ChangesAt(0, 3));
        Assert.True(playlist.ChangesAt(1, 3));
        Assert.False(playlist.ChangesAt(1, 4));
    }

    [Fact]
    public void ASingleFileOpensAndAnyFrameDecodesAsInOrder()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "2026-11-20", "090000-m3-r1.ivf");
        Write(path, Start, 6);

        var track = Assert.Single(IvfLibrary.Open(path).Tracks);
        Assert.Equal("Monitor 3", track.Name);
        var expected = DecodeInOrder(path);

        using var source = new IvfFrameSource();
        // Forward, back (from the key frame again, or cached), across a reopen and out of order.
        foreach (var i in new[] { 0, 1, 5, 4, 3, 2, 0, 5, 1 })
        {
            var frame = source.Get(track.Frames[i]);
            try
            {
                Assert.Equal(expected[i], frame.I420.ToArray());
            }
            finally
            {
                frame.Release();
            }
        }
        source.CloseIdle(TimeSpan.Zero);
        var again = source.Get(track.Frames[3]);
        Assert.Equal(expected[3], again.I420.ToArray());
        again.Release();
    }

    [Fact]
    public void WithoutUsableNamesTheTimeZoneIsThisComputers()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "clip.ivf");
        Write(path, Start, 1);

        var track = Assert.Single(IvfLibrary.Open(path).Tracks);
        Assert.Equal("clip", track.Name);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), track.Frames[0].Time.ToUnixTimeMilliseconds());
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow), track.Frames[0].Time.Offset);
    }

    /// <summary>A segment as Worklapse writes it: one key frame, then a frame every ten seconds, stamped in Unix ms.</summary>
    private static void Write(string path, DateTimeOffset start, int frames)
    {
        using var encoder = new Av1Encoder(Width, Height, quality: 40);
        using var writer = new IvfWriter(path, Width, Height);
        for (var i = 0; i < frames; i++)
        {
            var pts = start.AddSeconds(10 * i).ToUnixTimeMilliseconds();
            writer.WriteFrame(pts, encoder.Encode(Image(i), pts).Data.Span);
        }
    }

    private static List<byte[]> DecodeInOrder(string path)
    {
        using var stream = File.OpenRead(path);
        using var decoder = new Av1Decoder(threads: 1);
        var frames = new List<byte[]>();
        foreach (var (offset, length, _) in IvfReader.EnumerateFrames(stream).ToList())
        {
            decoder.Decode(IvfReader.ReadFrame(stream, offset, length));
            var i420 = new byte[I420.Size(Width, Height)];
            decoder.CopyI420(i420);
            frames.Add(i420);
        }
        return frames;
    }

    private static byte[] Image(int index)
    {
        var frame = new byte[I420.Size(Width, Height)];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            frame[y * Width + x] = (byte)(Math.Abs(x - index * 17 % Width) < 6 ? 20 : (x + y * 2) % 256);
        frame.AsSpan(Width * Height).Fill(128);
        return frame;
    }
}
