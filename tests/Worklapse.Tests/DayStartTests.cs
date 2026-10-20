using System.Globalization;
using Worklapse.Core.History;
using Worklapse.Core.Platform;
using Worklapse.Core.Projects;
using Worklapse.Core.Settings;
using Worklapse.Core.Storage;

namespace Worklapse.Tests;

public class DayStartTests
{
    private static readonly DateOnly Day = new(2026, 10, 15);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(15);

    private static DateTimeOffset Local(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Theory]
    [InlineData("00:00", "2026-10-16 00:00", "2026-10-16")]
    [InlineData("04:00", "2026-10-16 03:59", "2026-10-15")]
    [InlineData("04:00", "2026-10-16 04:00", "2026-10-16")]
    [InlineData("22:00", "2026-10-15 21:59", "2026-10-14")]
    [InlineData("22:00", "2026-10-15 22:00", "2026-10-15")]
    public void ADayIsNamedByTheDateItBeginsOn(string dayStart, string time, string day) =>
        Assert.Equal(DateOnly.Parse(day, CultureInfo.InvariantCulture),
            LocalDay.Of(DateTime.Parse(time, CultureInfo.InvariantCulture), TimeOnly.Parse(dayStart, CultureInfo.InvariantCulture)));

    [Fact]
    public void LateNightsCountTowardTheDayBefore()
    {
        using var folder = new TempFolder();
        using var db = WorklapseDatabase.Open(folder.Path);
        var recording = new RecordingStore(db);
        var app = recording.GetOrAddApp("Code.exe", null);
        var next = Day.AddDays(1);
        DateTimeOffset[] times = [Local(Day, 23), Local(next, 1), Local(next, 3, 59), Local(next, 4), Local(next, 9)];
        foreach (var time in times)
            recording.InsertCapture(time, CaptureKind.Active, app, "t", null);

        var midnight = new HistoryStore(db);
        Assert.Equal([Day, next], midnight.GetDays());
        Assert.Equal(times[..1], midnight.LoadDay(Day).Captures.Select(c => c.Time));

        var four = new HistoryStore(db, new TimeOnly(4, 0));
        Assert.Equal((Local(Day, 4), Local(next, 4)), four.DayRange(Day));
        Assert.Equal([Day, next], four.GetDays());
        Assert.Equal(times[..3], four.LoadDay(Day).Captures.Select(c => c.Time));
        Assert.Equal(times[3..], four.LoadDay(next).Captures.Select(c => c.Time));

        // The views showing many days split them the same way.
        var summaries = DaySummaries.Build(four, new ProjectStore(db), Day, next.AddDays(1), Max);
        Assert.Equal((times[0], times[2] + Max), summaries[0].Extent);
        Assert.Equal((times[3], times[4] + Max), summaries[1].Extent);

        // A day starting in the evening holds the next morning.
        var evening = new HistoryStore(db, new TimeOnly(22, 0));
        Assert.Equal([Day], evening.GetDays());
        Assert.Equal(times, evening.LoadDay(Day).Captures.Select(c => c.Time));
    }

    [Fact]
    public void TheDayStartIsKeptToTheMinute()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(path, """{"DayStart": "04:30:15"}""");
        Assert.Equal(new TimeOnly(4, 30), new SettingsStore(path).Current.DayStart);

        new SettingsStore(path).Save(new WorklapseSettings { DayStart = new TimeOnly(23, 59) });
        Assert.Equal(new TimeOnly(23, 59), new SettingsStore(path).Current.DayStart);
        Assert.Equal(default, new WorklapseSettings().DayStart);
    }
}
