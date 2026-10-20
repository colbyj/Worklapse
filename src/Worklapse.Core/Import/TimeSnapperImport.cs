using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Core.Import;

/// <param name="Read">TimeSnapper captures looked at so far.</param>
public readonly record struct TimeSnapperProgress(int Read, int Total, int Imported);

/// <param name="Imported">Captures added, with or without a picture.</param>
/// <param name="WithoutPicture">Of those, the ones with no picture to show: TimeSnapper couldn't name the program, or the file isn't a JPEG.</param>
/// <param name="AlreadyImported">Imported before (and kept or deleted since).</param>
/// <param name="RecordedByWorklapse">Left out because Worklapse recorded that moment itself.</param>
/// <param name="PictureDeleted">Left out because their picture was deleted (in TimeSnapper's Day Browser, or since).</param>
/// <param name="Unreadable">Left out for now because their picture couldn't be opened; importing again tries them again.</param>
public sealed record TimeSnapperResult(int Imported, int WithoutPicture, int AlreadyImported, int RecordedByWorklapse, int PictureDeleted,
    int Unreadable)
{
    public int Skipped => AlreadyImported + RecordedByWorklapse + PictureDeleted + Unreadable;
}

/// <summary>
/// Imports TimeSnapper's history: each capture becomes a Worklapse capture (active; TimeSnapper records nothing while
/// idle) with its app, site and window title, and its picture, a JPEG of the active window, stays where TimeSnapper
/// put it: it becomes an image segment with that absolute path, on a monitor of its own (<see cref="MonitorName"/>).
/// Deleting such a capture in Worklapse deletes its picture too, like Worklapse's own images.
/// <list type="bullet">
/// <item>Captures whose picture is gone are left out (deleted in TimeSnapper's Day Browser, which keeps the row),
/// except for programs TimeSnapper couldn't name, which come in without one, as do captures whose file isn't a
/// JPEG (TimeSnapper's first captures were PNGs of the whole desktop). One whose file can't be opened now is left
/// for the next import.</item>
/// <item>Worklapse decides for the time it recorded itself, active or idle: a capture falling in the time one of
/// Worklapse's own counts for is left out. TimeSnapper fills only what Worklapse didn't record (not running, paused,
/// an excluded app).</item>
/// <item>Importing again adds only what is new (<c>timesnapper_imports</c>).</item>
/// <item>Programs and sites TimeSnapper counts as productive are marked so, if Worklapse has them; nothing is unmarked.</item>
/// <item>Sites whose name isn't a host (TimeSnapper sometimes took a page title for one) are dropped: those captures
/// have their browser only.</item>
/// </list>
/// Writes in short transactions, so the recorder can go on recording meanwhile.
/// </summary>
public static class TimeSnapperImport
{
    /// <summary>The device name of the monitor TimeSnapper's pictures are shown on: each is a window, at its own size.</summary>
    public const string MonitorName = "TimeSnapper";

    /// <summary>TimeSnapper's program name for windows it couldn't tell the program of.</summary>
    public const string UnknownProgram = "(Unknown)";

    /// <summary>TimeSnapper's default JPEG quality, what its pictures were most likely saved at.</summary>
    private const int PictureQuality = 15;

    /// <summary>Captures written per transaction.</summary>
    private const int BatchSize = 500;

    /// <summary>Progress is reported every this many TimeSnapper captures.</summary>
    private const int ProgressInterval = 1000;

    /// <summary>Only the start of a JPEG is read, for its size: the frame header comes before the picture data.</summary>
    private const int HeaderReadSize = 4096;

    /// <param name="maxCaptureLength">How long a capture counts for at most (<see cref="Settings.WorklapseSettings.MaxCaptureLength"/>).</param>
    /// <exception cref="OperationCanceledException">Cancelled; what was written so far stays, and importing again goes on from there.</exception>
    public static TimeSnapperResult Run(WorklapseDatabase db, ITimeSnapperData data, TimeSpan maxCaptureLength,
        IProgress<TimeSnapperProgress>? progress = null, CancellationToken cancel = default)
    {
        var store = new RecordingStore(db);
        var imported = ImportedActivities(db);
        var own = OwnCaptureTimes(db);
        var ownLength = (long)maxCaptureLength.TotalMilliseconds;
        var (monitorId, layoutId) = AddMonitor(store);

        int added = 0, withoutPicture = 0, already = 0, recorded = 0, deleted = 0, unreadable = 0, read = 0;
        var batch = new List<(TimeSnapperActivity Activity, DateTimeOffset Time, Picture? Picture)>(BatchSize);
        foreach (var activity in data.Activities)
        {
            cancel.ThrowIfCancellationRequested();
            if (++read % ProgressInterval == 0)
                progress?.Report(new TimeSnapperProgress(read, data.ActivityCount, added + batch.Count));
            if (imported.Contains(activity.Id))
            {
                already++;
                continue;
            }
            var time = new DateTimeOffset(activity.Time, TimeZoneInfo.Local.GetUtcOffset(activity.Time));
            if (IsRecorded(own, time, ownLength))
            {
                recorded++;
                continue;
            }

            var (state, picture) = ReadPicture(activity.FileName);
            if (state == FileState.Unreadable)
            {
                unreadable++;
                continue;
            }
            if (state == FileState.Missing && !IsUnknown(activity.Program))
            {
                deleted++;
                continue;
            }
            if (picture is null)
                withoutPicture++;
            batch.Add((activity, time, picture));

            if (batch.Count == BatchSize)
            {
                added += Write(store, data, batch, monitorId, layoutId);
                batch.Clear();
            }
        }
        added += Write(store, data, batch, monitorId, layoutId);
        MarkProductive(db, data);
        progress?.Report(new TimeSnapperProgress(read, data.ActivityCount, added));
        return new TimeSnapperResult(added, withoutPicture, already, recorded, deleted, unreadable);
    }

    /// <summary>
    /// A site's name as Worklapse records it (see <see cref="SiteDomain"/>), or null when TimeSnapper's isn't a host
    /// name: a page title, a file path, a word ("SETTINGS"), a number (which <see cref="Uri"/> would read as an IPv4
    /// address: "1720" is 0.0.6.184), so the name must start with the host it gives.
    /// </summary>
    public static string? SiteName(string name)
    {
        var host = SiteDomain.FromAddressBar(name);
        if (host is null || !(host.Contains('.') || host == "localhost"))
            return null;
        return name.StartsWith(host, StringComparison.OrdinalIgnoreCase) || name.StartsWith("www." + host, StringComparison.OrdinalIgnoreCase)
            ? host : null;
    }

    private sealed record Picture(string Path, int Width, int Height, int Length);

    private static int Write(RecordingStore store, ITimeSnapperData data, List<(TimeSnapperActivity Activity, DateTimeOffset Time, Picture? Picture)> batch,
        long monitorId, long layoutId)
    {
        if (batch.Count == 0)
            return 0;

        // Ids are looked up again each batch: a delete in Day review meanwhile can remove an app or site.
        var apps = new Dictionary<(string, string?), long>();
        var sites = new Dictionary<string, long>();
        var db = store.Database;
        using var transaction = store.BeginTransaction();
        foreach (var (activity, time, picture) in batch)
        {
            long? appId = null;
            if (!IsUnknown(activity.Program))
            {
                var path = activity.ProgramId is { } programId && data.Programs.TryGetValue(programId, out var program) && !program.IsSite
                    ? program.Path : null;
                if (!apps.TryGetValue((activity.Program!, path), out var id))
                    apps[(activity.Program!, path)] = id = store.GetOrAddApp(activity.Program!, path);
                appId = id;
            }

            long? siteId = null;
            if (activity.SiteId is { } siteProgramId && data.Programs.TryGetValue(siteProgramId, out var site) && site.IsSite
                && SiteName(site.Name) is { } domain)
            {
                if (!sites.TryGetValue(domain, out var id))
                    sites[domain] = id = store.GetOrAddSite(domain);
                siteId = id;
            }

            var title = string.IsNullOrEmpty(activity.Caption) ? null : activity.Caption;
            var captureId = store.InsertCapture(time, CaptureKind.Active, appId, title, window: null, siteId, layoutId);
            if (picture is not null)
                store.InsertFrame(captureId, monitorId, new FrameRef(GetOrAddSegment(store, picture, monitorId, time), 0));

            using var record = db.Command("INSERT INTO timesnapper_imports (activity_id, capture_id) VALUES ($activity, $capture);");
            record.Parameters.AddWithValue("$activity", activity.Id);
            record.Parameters.AddWithValue("$capture", captureId);
            record.ExecuteNonQuery();
        }
        transaction.Commit();
        return batch.Count;
    }

    /// <summary>
    /// The picture's segment: one frame, the file itself. A file is never in two segments, since deleting one deletes
    /// the file.
    /// </summary>
    private static long GetOrAddSegment(RecordingStore store, Picture picture, long monitorId, DateTimeOffset time)
    {
        using (var find = store.Database.Command("SELECT id FROM segments WHERE path = $path;"))
        {
            find.Parameters.AddWithValue("$path", picture.Path);
            if (find.ExecuteScalar() is long existing)
                return existing;
        }
        var segmentId = store.InsertSegment(monitorId, picture.Path, picture.Width, picture.Height, PictureQuality, time,
            SegmentState.Encoded, FrameFormat.Jpeg);
        store.InsertSegmentFrame(segmentId, 0, 0, picture.Length, isKey: true);
        return segmentId;
    }

    private static bool IsUnknown(string? program) => string.IsNullOrEmpty(program) || program == UnknownProgram;

    private static HashSet<int> ImportedActivities(WorklapseDatabase db)
    {
        using var command = db.Command("SELECT activity_id FROM timesnapper_imports;");
        using var reader = command.ExecuteReader();
        var ids = new HashSet<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    /// <summary>When Worklapse's own captures were taken (UTC ms), ascending.</summary>
    private static long[] OwnCaptureTimes(WorklapseDatabase db)
    {
        using var command = db.Command("""
            SELECT ts_ms FROM captures c
            WHERE NOT EXISTS (SELECT 1 FROM timesnapper_imports t WHERE t.capture_id = c.id)
            ORDER BY ts_ms;
            """);
        using var reader = command.ExecuteReader();
        var times = new List<long>();
        while (reader.Read())
            times.Add(reader.GetInt64(0));
        return [.. times];
    }

    /// <summary>
    /// Whether <paramref name="time"/> falls in the time one of Worklapse's own captures counts for, as Day review
    /// counts it: until the next one, at most <paramref name="maxCaptureLength"/> ms (<see cref="Settings.WorklapseSettings.MaxCaptureLength"/>).
    /// Idle captures count too, so idle time stays idle.
    /// </summary>
    private static bool IsRecorded(long[] own, DateTimeOffset time, long maxCaptureLength)
    {
        var ms = time.ToUnixTimeMilliseconds();
        // The last of Worklapse's captures at or before the time.
        int low = 0, high = own.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (own[middle] <= ms)
                low = middle + 1;
            else
                high = middle;
        }
        if (low == 0)
            return false;
        var start = own[low - 1];
        var end = low < own.Length ? Math.Min(own[low], start + maxCaptureLength) : start + maxCaptureLength;
        return ms < end;
    }

    /// <summary>The monitor TimeSnapper's pictures are shown on, alone in its layout.</summary>
    private static (long Monitor, long Layout) AddMonitor(RecordingStore store)
    {
        // Its size is only used for captures without a picture: each picture is shown at its own size.
        var monitorId = store.GetOrAddMonitor(new MonitorInfo(MonitorName, new PixelRect(0, 0, 1920, 1080), IsPrimary: false));
        return (monitorId, store.GetOrAddLayout([monitorId]));
    }

    private enum FileState { Missing, Unreadable, Read }

    /// <summary>A capture's picture file, and the picture if it is a JPEG.</summary>
    private static (FileState, Picture?) ReadPicture(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return (FileState.Missing, null);
        try
        {
            using var file = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, HeaderReadSize);
            return (FileState.Read, JpegSize(file) is var (width, height) && file.Length <= int.MaxValue
                ? new Picture(fileName, width, height, (int)file.Length)
                : null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (FileState.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (FileState.Unreadable, null); // there, but can't be opened now (in use, no access)
        }
    }

    /// <summary>A JPEG's size, from its frame header; null if the stream isn't a JPEG.</summary>
    internal static (int Width, int Height)? JpegSize(Stream stream)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            return null;
        while (true)
        {
            var b = stream.ReadByte();
            if (b != 0xFF)
                return null;
            int marker;
            do
                marker = stream.ReadByte();
            while (marker == 0xFF); // fill bytes
            if (marker < 0)
                return null;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
                continue; // no length
            if (marker is 0xD9 or 0xDA)
                return null; // end of image or picture data before any frame header

            var length = (stream.ReadByte() << 8) | stream.ReadByte();
            if (length < 2)
                return null;
            // Start of frame: any but DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                stream.ReadByte(); // sample precision
                var height = (stream.ReadByte() << 8) | stream.ReadByte();
                var width = (stream.ReadByte() << 8) | stream.ReadByte();
                return width > 0 && height > 0 ? (width, height) : null;
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }
    }

    /// <summary>Marks what TimeSnapper counts as productive, by exe name or site, where Worklapse has it.</summary>
    private static void MarkProductive(WorklapseDatabase db, ITimeSnapperData data)
    {
        using var transaction = db.BeginTransaction();
        foreach (var id in data.Classified)
        {
            if (!data.Programs.TryGetValue(id, out var program))
                continue;
            if (program.IsSite)
            {
                if (SiteName(program.Name) is not { } domain)
                    continue;
                using var command = db.Command("UPDATE sites SET productive = 1 WHERE domain = $name;");
                command.Parameters.AddWithValue("$name", domain);
                command.ExecuteNonQuery();
            }
            else
            {
                using var command = db.Command("UPDATE apps SET productive = 1 WHERE exe_name = $name;");
                command.Parameters.AddWithValue("$name", program.Name);
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }
}
