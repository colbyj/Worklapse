using System.Globalization;

namespace Worklapse.Core.Projects;

public enum PeriodKind
{
    Week,
    Month,
}

/// <summary>A week (Monday to Sunday) or a calendar month, as the Overview and the Timesheet step through them.</summary>
/// <param name="Start">The Monday, or the first of the month.</param>
public readonly record struct Period(PeriodKind Kind, DateOnly Start)
{
    /// <summary>The week or month containing <paramref name="day"/>.</summary>
    public static Period Containing(PeriodKind kind, DateOnly day) =>
        new(kind, kind == PeriodKind.Week ? WeekStart(day) : new DateOnly(day.Year, day.Month, 1));

    /// <summary>The day after the last.</summary>
    public DateOnly End => Kind == PeriodKind.Week ? Start.AddDays(7) : Start.AddMonths(1);

    public Period Previous => new(Kind, Kind == PeriodKind.Week ? Start.AddDays(-7) : Start.AddMonths(-1));

    public Period Next => new(Kind, Kind == PeriodKind.Week ? Start.AddDays(7) : Start.AddMonths(1));

    public bool Contains(DateOnly day) => day >= Start && day < End;

    /// <summary>
    /// The same time as another kind: the one holding today when this period does, else a week becomes the month its
    /// Thursday is in, and a month the week holding its first Thursday (ISO's rule, so switching back and forth
    /// returns to where it started).
    /// </summary>
    public Period As(PeriodKind kind, DateOnly today) => kind == Kind ? this
        : Contains(today) ? Containing(kind, today)
        : kind == PeriodKind.Month ? Containing(PeriodKind.Month, Start.AddDays(3))
        : Containing(PeriodKind.Week, Start.AddDays((4 - (int)Start.DayOfWeek + 7) % 7));

    /// <summary>"12 – 18 October 2026", "28 Sep – 4 Oct 2026", or "October 2026".</summary>
    public string Text
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            if (Kind == PeriodKind.Month)
                return Start.ToString("MMMM yyyy", culture);
            var last = End.AddDays(-1);
            return Start.Month == last.Month ? $"{Start.Day} – {last.ToString("d MMMM yyyy", culture)}"
                : Start.Year == last.Year ? $"{Start.ToString("d MMM", culture)} – {last.ToString("d MMM yyyy", culture)}"
                : $"{Start.ToString("d MMM yyyy", culture)} – {last.ToString("d MMM yyyy", culture)}";
        }
    }

    /// <summary>"Week 42" (ISO week) or "October".</summary>
    public string Title => Kind == PeriodKind.Month
        ? Start.ToString("MMMM", CultureInfo.CurrentCulture)
        : $"Week {WeekNumber(Start)}";

    /// <summary>"This Week" or "This Month", for the button that goes there.</summary>
    public string TodayText => Kind == PeriodKind.Week ? "This Week" : "This Month";

    /// <summary>The first day of the whole weeks covering the period (a month's calendar starts on this Monday).</summary>
    public DateOnly GridStart => WeekStart(Start);

    /// <summary>The day after the whole weeks covering the period.</summary>
    public DateOnly GridEnd => WeekStart(End.AddDays(-1)).AddDays(7);

    /// <summary>The columns of the period's totals: a week's days, or the weeks a month's days fall in.</summary>
    public int ColumnCount => Kind == PeriodKind.Week ? 7 : (GridEnd.DayNumber - GridStart.DayNumber) / 7;

    /// <summary>The column a day of the period is counted in.</summary>
    public int ColumnOf(DateOnly day) => Contains(day)
        ? (day.DayNumber - GridStart.DayNumber) / (Kind == PeriodKind.Week ? 1 : 7)
        : throw new ArgumentOutOfRangeException(nameof(day), day, "The day isn't in the period.");

    /// <summary>A column's first day: the day itself, or the Monday of the week (which can be in the month before).</summary>
    public DateOnly ColumnStart(int column) => GridStart.AddDays(Kind == PeriodKind.Week ? column : column * 7);

    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static int WeekNumber(DateOnly day) => ISOWeek.GetWeekOfYear(day.ToDateTime(TimeOnly.MinValue));
}
