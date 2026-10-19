namespace Worklapse.App.ViewModels;

internal static class TimeSpanSums
{
    /// <summary>The total of a time per item (LINQ's Sum has no overload for <see cref="TimeSpan"/>).</summary>
    public static TimeSpan SumTime<T>(this IEnumerable<T> items, Func<T, TimeSpan> selector) =>
        items.Aggregate(TimeSpan.Zero, (sum, item) => sum + selector(item));
}
