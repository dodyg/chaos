using Chaos.Domain.Tasks;

namespace Chaos.Domain.Planning;

/// <summary>How much time one local date offers: total minutes and an optional work allowance (PLAN §10).</summary>
public sealed record DayAvailability(int TotalMinutes, int? WorkMinutes)
{
    public const int MaxMinutes = 24 * 60;

    /// <summary>The initial default: 8 hours on weekdays and nothing at weekends, without a separate work allowance.</summary>
    public static DayAvailability DefaultFor(DayOfWeek weekday) =>
        weekday is DayOfWeek.Saturday or DayOfWeek.Sunday ? new(0, null) : new(8 * 60, null);

    /// <returns>Null when valid, otherwise a message naming the problem.</returns>
    public string? Problem() =>
        TotalMinutes is < 0 or > MaxMinutes ? "Available time must be between 0 and 24 hours."
        : WorkMinutes is < 0 or > MaxMinutes ? "Work time must be between 0 and 24 hours."
        : WorkMinutes > TotalMinutes ? "Work time cannot be more than the total available time."
        : null;
}

/// <summary>One task as capacity sees it: its remaining estimate (null = unknown) and category.</summary>
public sealed record CapacityItem(int? EstimateMinutes, CapacityCategory Category, bool IsOpen);

/// <summary>
/// One day's load against its availability. Missing estimates are counted separately and never
/// imply zero effort; the warnings are advice only and nothing is rescheduled.
/// </summary>
public sealed record DayLoad(
    int PlannedMinutes,
    int WorkMinutes,
    int UnknownEstimates,
    int OpenCount,
    bool OverTotal,
    bool OverWork);

/// <summary>Pure capacity arithmetic for the weekly planner (PLAN §10 "Weekly ritual and capacity").</summary>
public static class Capacity
{
    /// <summary>
    /// Sums the remaining estimates of open items; work time additionally counts Work items.
    /// Over-total warns when the planned time exceeds the day's total, including zero
    /// availability with anything planned; over-work warns only when a work allowance is set.
    /// </summary>
    public static DayLoad Measure(DayAvailability availability, IEnumerable<CapacityItem> items)
    {
        int planned = 0, work = 0, unknown = 0, open = 0;
        foreach (var item in items)
        {
            if (!item.IsOpen)
            {
                continue;
            }

            open++;
            if (item.EstimateMinutes is not { } estimate)
            {
                unknown++;
                continue;
            }

            planned += estimate;
            if (item.Category == CapacityCategory.Work)
            {
                work += estimate;
            }
        }

        var overTotal = planned > availability.TotalMinutes || (availability.TotalMinutes == 0 && open > 0);
        var overWork = availability.WorkMinutes is { } allowance && (work > allowance || (allowance == 0 && work > 0));
        return new DayLoad(planned, work, unknown, open, overTotal, overWork);
    }
}
