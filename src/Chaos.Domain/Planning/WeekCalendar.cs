namespace Chaos.Domain.Planning;

/// <summary>
/// Local-week arithmetic (PLAN §6, §10). A week is exactly seven local dates starting on the
/// profile's first weekday; it is never a UTC duration of 168 hours.
/// </summary>
public static class WeekCalendar
{
    public const int DaysInWeek = 7;

    /// <summary>The first date of the week containing <paramref name="date"/> for the given first weekday.</summary>
    public static DateOnly StartOf(DateOnly date, DayOfWeek firstWeekday)
    {
        var back = ((int)date.DayOfWeek - (int)firstWeekday + DaysInWeek) % DaysInWeek;
        return date.AddDays(-back);
    }

    public static DateOnly EndOf(DateOnly weekStart) => weekStart.AddDays(DaysInWeek - 1);

    public static bool Contains(DateOnly weekStart, DateOnly date) => date >= weekStart && date <= EndOf(weekStart);

    /// <summary>The seven dates of a week in order.</summary>
    public static IReadOnlyList<DateOnly> Dates(DateOnly weekStart) =>
        [.. Enumerable.Range(0, DaysInWeek).Select(weekStart.AddDays)];

    /// <summary>True when the two seven-day ranges share at least one date.</summary>
    public static bool Overlaps(DateOnly firstStart, DateOnly secondStart) =>
        firstStart <= EndOf(secondStart) && secondStart <= EndOf(firstStart);
}
