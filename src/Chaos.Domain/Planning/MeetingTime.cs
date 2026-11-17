namespace Chaos.Domain.Planning;

/// <summary>
/// One meeting from another calendar, as planning sees it (PLAN2 §10.4). Timed meetings carry UTC
/// instants; all-day meetings carry local dates, the end exclusive as in ICS and Google. Only busy
/// meetings take time off a day.
/// </summary>
public sealed record MeetingInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool AllDay, DateOnly? StartDate, DateOnly? EndDate, bool Busy)
{
    public static MeetingInterval Timed(DateTimeOffset start, DateTimeOffset end, bool busy = true) => new(start, end, false, null, null, busy);

    public static MeetingInterval AllDayRange(DateOnly start, DateOnly endExclusive, bool busy) =>
        new(default, default, true, start, endExclusive, busy);
}

/// <summary>Pure meeting arithmetic: busy minutes of a local day and the time still free in it.</summary>
public static class MeetingTime
{
    /// <summary>
    /// The union of busy intervals clipped to the local <paramref name="day"/>, in whole minutes.
    /// Overlaps count once, a meeting across midnight counts on both days for its share, a busy
    /// all-day meeting fills the day, and a daylight-saving day is as long as it really is.
    /// </summary>
    public static int BusyMinutes(DateOnly day, IEnumerable<MeetingInterval> intervals, TimeZoneInfo zone)
    {
        var (start, end) = DayBounds(day, zone);
        return BusyBetween(start, end, day, intervals);
    }

    /// <summary>
    /// What is left of the day for tasks: the available minutes minus busy time, and, when the day is
    /// today, no more than the time between now and the end of the day minus the meetings still to come.
    /// Never below zero.
    /// </summary>
    public static int FreeMinutes(DateOnly day, DateTimeOffset now, int availableMinutes, IEnumerable<MeetingInterval> intervals, TimeZoneInfo zone)
    {
        var list = intervals as IReadOnlyCollection<MeetingInterval> ?? [.. intervals];
        var (start, end) = DayBounds(day, zone);
        var free = Math.Max(0, availableMinutes - BusyBetween(start, end, day, list));
        if (now > start && now < end)
        {
            var left = (int)Math.Floor((end - now).TotalMinutes) - BusyBetween(now, end, day, list);
            free = Math.Min(free, Math.Max(0, left));
        }
        else if (now >= end)
        {
            free = 0;
        }

        return free;
    }

    /// <summary>The local day's start and end as UTC instants (the end is the next local midnight).</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) DayBounds(DateOnly day, TimeZoneInfo zone) =>
        (StartOfDay(day, zone), StartOfDay(day.AddDays(1), zone));

    private static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        // A day can start inside a skipped hour; step forward to the first valid local time.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static int BusyBetween(DateTimeOffset windowStart, DateTimeOffset windowEnd, DateOnly day, IEnumerable<MeetingInterval> intervals)
    {
        var spans = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var item in intervals)
        {
            if (!item.Busy)
            {
                continue;
            }

            if (item.AllDay)
            {
                if (item.StartDate is { } first && item.EndDate is { } last && first <= day && day < last)
                {
                    spans.Add((windowStart, windowEnd));
                }

                continue;
            }

            var from = item.StartUtc > windowStart ? item.StartUtc : windowStart;
            var to = item.EndUtc < windowEnd ? item.EndUtc : windowEnd;
            if (to > from)
            {
                spans.Add((from, to));
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        TimeSpan total = TimeSpan.Zero;
        DateTimeOffset? runStart = null, runEnd = null;
        foreach (var (start, end) in spans)
        {
            if (runEnd is null || start > runEnd)
            {
                if (runStart is not null)
                {
                    total += runEnd!.Value - runStart.Value;
                }

                runStart = start;
                runEnd = end;
            }
            else if (end > runEnd)
            {
                runEnd = end;
            }
        }

        if (runStart is not null)
        {
            total += runEnd!.Value - runStart.Value;
        }

        return (int)Math.Round(total.TotalMinutes, MidpointRounding.AwayFromZero);
    }
}
