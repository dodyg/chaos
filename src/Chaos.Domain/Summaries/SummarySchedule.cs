using Chaos.Domain.Planning;
using Chaos.Domain.Reminders;

namespace Chaos.Domain.Summaries;

/// <summary>Which plain summary (PLAN §12): the day ahead, how the day went, or how the week went.</summary>
public enum SummaryKind
{
    Morning = 0,
    Daily = 1,
    Weekly = 2,
}

/// <summary>
/// When summaries are due (PLAN §12, work package 11). Pure calendar arithmetic over local
/// dates in the profile zone: a daily or morning period is one local date, a weekly period is
/// exactly seven local dates from the week start (never 168 UTC hours). Scheduled instants use
/// the same DST rules as reminders: a time in a gap moves forward, an ambiguous time takes
/// the earlier instant.
/// </summary>
public static class SummarySchedule
{
    public static readonly TimeOnly DefaultMorningTime = new(8, 0);
    public static readonly TimeOnly DefaultEveningTime = new(18, 0);

    /// <summary>Missed daily summaries are caught up for at most this many earlier dates.</summary>
    public const int CatchUpDays = 7;

    /// <summary>The weekday a new profile gets its weekly summary on: the last day of its week.</summary>
    public static DayOfWeek DefaultWeeklyDay(DayOfWeek firstWeekday) => (DayOfWeek)(((int)firstWeekday + 6) % 7);

    /// <summary>
    /// The date a week's summary is due: the first date on or after the week's last day that
    /// falls on the chosen weekday. Choosing the last day summarizes the week as it closes;
    /// choosing a later weekday summarizes the finished week a few days into the next one.
    /// </summary>
    public static DateOnly WeeklyDueDate(DateOnly weekStart, DayOfWeek weeklyDay)
    {
        var end = WeekCalendar.EndOf(weekStart);
        var ahead = ((int)weeklyDay - (int)end.DayOfWeek + 7) % 7;
        return end.AddDays(ahead);
    }

    /// <summary>The instant a local date and wall time occur in the zone.</summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone) => ReminderTiming.ResolveLocal(zone, date, time);

    /// <summary>
    /// The dates whose evening summary should exist by <paramref name="now"/>: today once its
    /// evening time has passed, and up to <see cref="CatchUpDays"/> earlier dates, never before
    /// <paramref name="earliest"/> (the day the profile was created). Newest first.
    /// </summary>
    public static IReadOnlyList<DateOnly> DailyDatesDue(DateOnly today, DateTimeOffset now, TimeOnly eveningTime, TimeZoneInfo zone, DateOnly earliest)
    {
        var dates = new List<DateOnly>();
        if (now >= At(today, eveningTime, zone) && today >= earliest)
        {
            dates.Add(today);
        }

        for (var back = 1; back <= CatchUpDays; back++)
        {
            var date = today.AddDays(-back);
            if (date < earliest)
            {
                break;
            }

            dates.Add(date);
        }

        return dates;
    }
}
