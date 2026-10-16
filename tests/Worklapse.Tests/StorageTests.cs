using Worklapse.Core.Platform;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

public class StorageTests
{
    [Fact]
    public void OpeningCreatesTheSchemaAndReopeningKeepsData()
    {
        using var folder = new TempFolder();
        long captureId;
        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            var store = new RecordingStore(db);
            var app = store.GetOrAddApp("rider64.exe", @"C:\Program Files\Rider\rider64.exe");
            captureId = store.InsertCapture(DateTimeOffset.Now, CaptureKind.Active, app, "MainWindow.axaml", new PixelRect(1, 2, 3, 4));
        }

        using (var db = WorklapseDatabase.Open(folder.Path))
        {
            using var command = db.Command("SELECT window_title FROM captures WHERE id = $id;");
            command.Parameters.AddWithValue("$id", captureId);
            Assert.Equal("MainWindow.axaml", command.ExecuteScalar());
        }
    }

    [Fact]
    public void AppsAreDeduplicatedIncludingThoseWithoutAPath()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var a = new RecordingStore(db).GetOrAddApp("pid 4", null);
        // A fresh store has no cache, so this goes to the database.
        var b = new RecordingStore(db).GetOrAddApp("pid 4", null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void MonitorsAreDeduplicatedByGeometry()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var monitor = new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 3440, 1440), true);
        var a = new RecordingStore(db).GetOrAddMonitor(monitor);
        var b = new RecordingStore(db).GetOrAddMonitor(monitor);
        var c = new RecordingStore(db).GetOrAddMonitor(monitor with { Bounds = new PixelRect(0, 0, 2560, 1440) });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
