namespace Worklapse.Core.History;

/// <summary>
/// Where days begin: at midnight, or later when the settings move the start (<see cref="Settings.WorklapseSettings.DayStart"/>)
/// so the early hours of a late night count toward the day before. A day is named by the date it begins on.
/// </summary>
public static class LocalDay
{
    /// <summary>The day a local time falls in.</summary>
    public static DateOnly Of(DateTime local, TimeOnly dayStart) => DateOnly.FromDateTime(local - dayStart.ToTimeSpan());

    public static DateOnly Today(TimeOnly dayStart) => Of(DateTime.Now, dayStart);

    /// <summary>
    /// The start and end of a day. Captures store their own offset, but the day itself is bounded as this computer's
    /// time zone sees it now (a day recorded in another time zone can straddle two).
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly day, TimeOnly dayStart) =>
        (At(day.ToDateTime(dayStart)), At(day.AddDays(1).ToDateTime(dayStart)));

    /// <summary>The midnight a day's date begins with, which its timelines measure from.</summary>
    public static DateTimeOffset Midnight(DateOnly day) => At(day.ToDateTime(TimeOnly.MinValue));

    private static DateTimeOffset At(DateTime local) => new(local, TimeZoneInfo.Local.GetUtcOffset(local));
}
