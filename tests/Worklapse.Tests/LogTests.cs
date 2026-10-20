using Worklapse.Core.Diagnostics;

namespace Worklapse.Tests;

/// <summary>The log is one static file: these run on their own, so other tests' errors don't land in it.</summary>
[CollectionDefinition(nameof(LogTests), DisableParallelization = true)]
public sealed class LogCollection;

[Collection(nameof(LogTests))]
public class LogTests
{
    [Fact]
    public void NothingIsWrittenUntilTheLogIsStarted()
    {
        using var folder = new TempFolder();
        Log.Error("before the start");
        Log.Start("1.2.3", folder.Path);
        try
        {
            Log.Error("after the start", new InvalidOperationException("the details"));
        }
        finally
        {
            Log.Stop();
        }
        Log.Error("after the stop");

        var text = File.ReadAllText(Path.Combine(folder.Path, "worklapse.log"));
        Assert.Contains("Worklapse 1.2.3 started", text);
        Assert.Contains("ERROR after the start", text);
        Assert.Contains("InvalidOperationException: the details", text);
        Assert.DoesNotContain("before the start", text);
        Assert.DoesNotContain("after the stop", text);
    }

    [Fact]
    public void ACrashIsReportedOnceAtTheNextStart()
    {
        using var folder = new TempFolder();
        Log.Start("1.0.0", folder.Path);
        try
        {
            Assert.Null(Log.TakeCrashReport());
            var before = DateTimeOffset.Now.AddSeconds(-1);
            Log.Crash(new InvalidOperationException("the encoder fell over"));

            // The next run.
            Log.Start("1.0.0", folder.Path);
            var report = Log.TakeCrashReport();
            Assert.NotNull(report);
            Assert.Equal("the encoder fell over", report.Message);
            Assert.InRange(report.At, before, DateTimeOffset.Now.AddSeconds(1));
            Assert.Null(Log.TakeCrashReport());
            Assert.Contains("FATAL Worklapse stopped", File.ReadAllText(Path.Combine(folder.Path, "worklapse.log")));
        }
        finally
        {
            Log.Stop();
        }
    }

    [Fact]
    public void AFullLogStartsOverKeepingThePreviousOne()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "worklapse.log");
        File.WriteAllText(path, new string('x', (int)Log.MaxBytes + 1));
        Log.Start("1.0.0", folder.Path);
        try
        {
            Log.Info("a new entry");
        }
        finally
        {
            Log.Stop();
        }

        Assert.Equal(Log.MaxBytes + 1, new FileInfo(Path.Combine(folder.Path, "worklapse.old.log")).Length);
        var text = File.ReadAllText(path);
        Assert.Contains("a new entry", text);
        Assert.DoesNotContain("xxx", text);
    }
}
