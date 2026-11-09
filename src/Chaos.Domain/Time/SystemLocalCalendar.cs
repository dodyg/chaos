namespace Chaos.Domain.Time;

/// <summary>Default calendar over an injectable clock (BCL only; tests inject FakeTimeProvider-style TimeProviders).</summary>
public sealed class SystemLocalCalendar(TimeProvider clock, TimeZoneInfo zone) : ILocalCalendar
{
    public TimeZoneInfo Zone { get; } = zone ?? throw new ArgumentNullException(nameof(zone));

    public DateOnly Today()
    {
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);
    }

    public IReadOnlyList<DateOnly> WeekDates(DateOnly date, DayOfWeek firstWeekday)
    {
        var daysFromWeekStart = ((int)date.DayOfWeek - (int)firstWeekday + 7) % 7;
        var start = date.AddDays(-daysFromWeekStart);

        var week = new DateOnly[7];
        for (var i = 0; i < 7; i++)
        {
            week[i] = start.AddDays(i);
        }

        return week;
    }

    public DateTimeOffset ToUtcInstant(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, Zone);
        return new DateTimeOffset(utc);
    }
}
