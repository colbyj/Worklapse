using System.Globalization;
using Worklapse.Core.History;
using Worklapse.Core.Projects;

namespace Worklapse.Tests;

public class PeriodTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void WeeksRunMondayToSundayAndStepAcrossYears()
    {
        var week = Period.Containing(PeriodKind.Week, new DateOnly(2026, 1, 1)); // a Thursday
        Assert.Equal(new DateOnly(2025, 12, 29), week.Start);
        Assert.Equal(new DateOnly(2026, 1, 5), week.End);
        Assert.Equal(1, Period.WeekNumber(week.Start)); // ISO: the week with the year's first Thursday
        Assert.Equal(new DateOnly(2025, 12, 22), week.Previous.Start);
        Assert.Equal(new DateOnly(2026, 1, 5), week.Next.Start);

        var month = Period.Containing(PeriodKind.Month, new DateOnly(2026, 2, 14));
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)), (month.Start, month.End));
        Assert.Equal(new DateOnly(2026, 1, 1), month.Previous.Start);
        Assert.Equal(new DateOnly(2027, 1, 1), Period.Containing(PeriodKind.Month, new DateOnly(2026, 12, 5)).Next.Start);
    }

    [Fact]
    public void TextsNameTheRange()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("12 – 18 October 2026", Period.Containing(PeriodKind.Week, new DateOnly(2026, 10, 14)).Text);
            Assert.Equal("28 Sep – 4 Oct 2026", Period.Containing(PeriodKind.Week, new DateOnly(2026, 10, 1)).Text);
            Assert.Equal("29 Dec 2025 – 4 Jan 2026", Period.Containing(PeriodKind.Week, new DateOnly(2026, 1, 1)).Text);
            Assert.Equal("Week 1", Period.Containing(PeriodKind.Week, new DateOnly(2026, 1, 1)).Title);
            Assert.Equal("October 2026", Period.Containing(PeriodKind.Month, new DateOnly(2026, 10, 14)).Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void SwitchingBetweenWeekAndMonthKeepsToTheSameTime()
    {
        // A week straddling two months becomes the month its Thursday is in, and back.
        var straddling = Period.Containing(PeriodKind.Week, new DateOnly(2030, 9, 30)); // Mon 30 Sep – Sun 6 Oct 2030
        Assert.Equal(new DateOnly(2030, 10, 1), straddling.As(PeriodKind.Month, Today).Start);
        Assert.Equal(straddling, straddling.As(PeriodKind.Month, Today).As(PeriodKind.Week, Today));

        // A month starting on a Sunday becomes the week of its first Thursday, not the week ending on the 1st.
        var march = Period.Containing(PeriodKind.Month, new DateOnly(2026, 3, 1)); // 1 March 2026 is a Sunday
        Assert.Equal(new DateOnly(2026, 3, 2), march.As(PeriodKind.Week, Today).Start);
        Assert.Equal(march, march.As(PeriodKind.Week, Today).As(PeriodKind.Month, Today));

        // The period holding today becomes the one holding today.
        Assert.Equal(Period.Containing(PeriodKind.Week, Today), Period.Containing(PeriodKind.Month, Today).As(PeriodKind.Week, Today));
        Assert.Equal(Period.Containing(PeriodKind.Month, Today), Period.Containing(PeriodKind.Week, Today).As(PeriodKind.Month, Today));
    }

    [Fact]
    public void ColumnsAreAWeeksDaysOrTheWeeksAMonthsDaysFallIn()
    {
        var week = Period.Containing(PeriodKind.Week, new DateOnly(2026, 10, 14));
        Assert.Equal(7, week.ColumnCount);
        Assert.Equal(2, week.ColumnOf(new DateOnly(2026, 10, 14)));
        Assert.Equal(new DateOnly(2026, 10, 18), week.ColumnStart(6));

        var october = Period.Containing(PeriodKind.Month, new DateOnly(2026, 10, 14)); // Thu 1 – Sat 31
        Assert.Equal((new DateOnly(2026, 9, 28), new DateOnly(2026, 11, 2)), (october.GridStart, october.GridEnd));
        Assert.Equal(5, october.ColumnCount);
        Assert.Equal(0, october.ColumnOf(new DateOnly(2026, 10, 4)));
        Assert.Equal(1, october.ColumnOf(new DateOnly(2026, 10, 5)));
        Assert.Equal(4, october.ColumnOf(new DateOnly(2026, 10, 31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => october.ColumnOf(new DateOnly(2026, 9, 30)));

        Assert.Equal(4, Period.Containing(PeriodKind.Month, new DateOnly(2027, 2, 1)).ColumnCount);  // Mon 1 – Sun 28
        Assert.Equal(6, Period.Containing(PeriodKind.Month, new DateOnly(2026, 8, 1)).ColumnCount);  // Sat 1 – Mon 31
    }

    [Fact]
    public void TotalsAreSummedPerColumnAndLoggedTimeAddsALine()
    {
        var october = Period.Containing(PeriodKind.Month, new DateOnly(2026, 10, 1));
        DaySummary Day(int day, long project, int assignedMinutes, int productive = 0, int other = 0, int recorded = 0)
        {
            var date = new DateOnly(2026, 10, day);
            var time = new ProjectTime([],
                new Dictionary<long, ProjectTotals> { [project] = new(TimeSpan.FromMinutes(assignedMinutes), TimeSpan.Zero, TimeSpan.Zero) },
                new UnassignedTotals(TimeSpan.FromMinutes(productive), TimeSpan.FromMinutes(other), TimeSpan.Zero),
                TimeSpan.FromMinutes(recorded));
            return new DaySummary(date, null, time, [], []);
        }

        var totals = PeriodTotals.Build(october,
            [Day(2, 1, 60, productive: 5, recorded: 70), Day(4, 1, 30, other: 10), Day(5, 2, 45, recorded: 50), Day(31, 1, 15)],
            [new LoggedTime(new DateOnly(2026, 10, 2), 1, TimeSpan.FromMinutes(90)),
             new LoggedTime(new DateOnly(2026, 10, 20), 3, TimeSpan.FromMinutes(20)),     // logged only
             new LoggedTime(new DateOnly(2026, 11, 2), 1, TimeSpan.FromMinutes(600))]);  // outside the month

        TimeSpan[] Minutes(params int[] values) => values.Select(v => TimeSpan.FromMinutes(v)).ToArray();
        Assert.Equal(Minutes(90, 0, 0, 0, 15), totals.Assigned[1]);
        Assert.Equal(Minutes(0, 45, 0, 0, 0), totals.Assigned[2]);
        Assert.Equal(Minutes(0, 0, 0, 0, 0), totals.Assigned[3]);
        Assert.Equal(TimeSpan.FromMinutes(90), totals.Logged[1]);
        Assert.Equal(TimeSpan.FromMinutes(20), totals.Logged[3]);
        Assert.False(totals.Logged.ContainsKey(2));
        Assert.Equal(Minutes(5, 0, 0, 0, 0), totals.Productive);
        Assert.Equal(Minutes(10, 0, 0, 0, 0), totals.Other);
        Assert.Equal(Minutes(70, 50, 0, 0, 0), totals.Recorded);
        Assert.Equal(Minutes(90, 45, 0, 0, 15), totals.AssignedPerColumn());
    }
}
