namespace Chaos.Domain.Time;

/// <summary>
/// Local calendar over the profile's zone (PLAN §4 core ports): UTC now, today,
/// week boundaries (exactly seven local dates, first weekday is a profile setting),
/// and local-to-instant resolution with DST rules (PLAN §6).
/// </summary>
public interface ILocalCalendar
{
    /// <summary>Profile time zone (validated canonical ID at profile creation/update).</summary>
    TimeZoneInfo Zone { get; }

    /// <summary>The profile's current local date.</summary>
    DateOnly Today();

    /// <summary>
    /// The seven local dates of the week containing <paramref name="date"/>,
    /// starting on <paramref name="firstWeekday"/>.
    /// </summary>
    IReadOnlyList<DateOnly> WeekDates(DateOnly date, DayOfWeek firstWeekday);

    /// <summary>
    /// Resolves a local wall time to its UTC instant. An invalid (skipped) DST local
    /// time resolves forward by the transition gap; an ambiguous local time resolves
    /// to the earlier instant (PLAN §6).
    /// </summary>
    DateTimeOffset ToUtcInstant(DateOnly date, TimeOnly time);
}
