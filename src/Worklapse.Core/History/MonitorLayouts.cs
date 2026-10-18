namespace Worklapse.Core.History;

/// <summary>
/// Works out which monitors were attached at captures recorded before the recorder saved that with each capture
/// (schema 6). Plugging monitors in or out makes Windows move and rename them, which records them as new
/// monitors; drawn all at once they cover each other. Only the frames tell:
/// <list type="bullet">
/// <item>a capture with frames of several monitors (all or selected monitors) had exactly those;</item>
/// <item>one with a single frame (active application, primary monitor) had that monitor, plus the day's other
/// monitors, nearest frame in time first, leaving out any that can't have been attached alongside one already
/// in: overlapping it, with the same device name, or a second primary;</item>
/// <item>one without frames (idle) takes the layout of the nearest capture with frames, earlier first.</item>
/// </list>
/// A monitor unplugged and not replaced by another in its place can stay in the guess.
/// </summary>
public static class MonitorLayouts
{
    /// <returns>For each capture, the ids of the monitors attached, ascending; empty for a day without frames.</returns>
    public static IReadOnlyList<long>[] Guess(IReadOnlyList<CaptureRecord> captures, IReadOnlyDictionary<long, MonitorRecord> monitors)
    {
        // When each monitor has a frame, in time order.
        var seen = new Dictionary<long, List<long>>();
        foreach (var capture in captures)
            foreach (var frame in capture.Frames)
            {
                if (!seen.TryGetValue(frame.MonitorId, out var times))
                    seen[frame.MonitorId] = times = [];
                times.Add(capture.Time.ToUnixTimeMilliseconds());
            }

        var interned = new Dictionary<string, IReadOnlyList<long>>();
        var layouts = new IReadOnlyList<long>?[captures.Count];
        for (var i = 0; i < captures.Count; i++)
        {
            var own = captures[i].Frames.Select(f => f.MonitorId).Distinct().ToList();
            if (own.Count == 0)
                continue;

            var layout = own.Count > 1 ? own : Around(own[0], captures[i].Time.ToUnixTimeMilliseconds());
            layout.Sort();
            var key = string.Join(",", layout);
            if (!interned.TryGetValue(key, out var shared))
                interned[key] = shared = layout;
            layouts[i] = shared;
        }

        // Captures without frames: the nearest earlier layout, or the first one after them.
        IReadOnlyList<long>? previous = null;
        for (var i = 0; i < captures.Count; i++)
            layouts[i] = previous = layouts[i] ?? previous;
        IReadOnlyList<long> next = [];
        for (var i = captures.Count - 1; i >= 0; i--)
            layouts[i] = next = layouts[i] ?? next;
        return layouts!;

        List<long> Around(long monitorId, long time)
        {
            var layout = new List<long> { monitorId };
            var candidates = seen.Keys
                .Where(id => id != monitorId)
                .OrderBy(id => Distance(seen[id], time))
                .ThenBy(id => id);
            foreach (var candidate in candidates)
                if (layout.All(id => CanCoexist(monitors[id], monitors[candidate])))
                    layout.Add(candidate);
            return layout;
        }
    }

    private static bool CanCoexist(MonitorRecord a, MonitorRecord b) =>
        a.Bounds.Intersect(b.Bounds).IsEmpty
        && !string.Equals(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase)
        && !(a.IsPrimary && b.IsPrimary);

    /// <summary>Milliseconds from <paramref name="time"/> to the nearest of <paramref name="times"/> (ascending).</summary>
    private static long Distance(List<long> times, long time)
    {
        var index = times.BinarySearch(time);
        if (index >= 0)
            return 0;
        index = ~index;
        var distance = long.MaxValue;
        if (index < times.Count)
            distance = times[index] - time;
        if (index > 0)
            distance = Math.Min(distance, time - times[index - 1]);
        return distance;
    }
}
