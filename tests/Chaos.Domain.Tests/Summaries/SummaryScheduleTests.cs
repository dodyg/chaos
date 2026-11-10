using Chaos.Domain.Summaries;

namespace Chaos.Domain.Tests.Summaries;

public sealed class SummaryScheduleTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Test]
    [Arguments(DayOfWeek.Sunday, DayOfWeek.Saturday)]
    [Arguments(DayOfWeek.Monday, DayOfWeek.Sunday)]
    [Arguments(DayOfWeek.Saturday, DayOfWeek.Friday)]
    public async Task DefaultWeeklyDay_IsTheLastDayOfTheConfiguredWeek(DayOfWeek first, DayOfWeek expected)
    {
        await Assert.That(SummarySchedule.DefaultWeeklyDay(first)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(DayOfWeek.Sunday, "2026-11-15")]    // the week's last day: summarized as it closes
    [Arguments(DayOfWeek.Monday, "2026-11-16")]    // the next morning's weekday: the finished week
    [Arguments(DayOfWeek.Wednesday, "2026-11-18")] // a few days into the next week
    public async Task WeeklyDueDate_IsTheFirstChosenWeekdayOnOrAfterTheWeeksLastDay(DayOfWeek weeklyDay, string expected)
    {
        var due = SummarySchedule.WeeklyDueDate(new DateOnly(2026, 11, 9), weeklyDay); // Monday-start week
        await Assert.That(due).IsEqualTo(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task At_TimeInTheSpringForwardGap_MovesForward()
    {
        var at = SummarySchedule.At(new DateOnly(2027, 3, 14), new TimeOnly(2, 30), NewYork);
        await Assert.That(at).IsEqualTo(new DateTimeOffset(2027, 3, 14, 7, 30, 0, TimeSpan.Zero)); // 03:30 EDT
    }

    [Test]
    public async Task At_AmbiguousFallBackTime_TakesTheEarlierInstant()
    {
        var at = SummarySchedule.At(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), NewYork);
        await Assert.That(at).IsEqualTo(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero)); // 01:30 EDT
    }

    [Test]
    public async Task DailyDatesDue_BeforeTheEveningTime_LeavesTodayOutButCatchesUpEarlierDays()
    {
        var today = new DateOnly(2026, 11, 10);
        var dates = SummarySchedule.DailyDatesDue(today, new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), new TimeOnly(18, 0), TimeZoneInfo.Utc, today.AddDays(-3));

        await Assert.That(dates.SequenceEqual([today.AddDays(-1), today.AddDays(-2), today.AddDays(-3)])).IsTrue();
    }

    [Test]
    public async Task DailyDatesDue_AfterTheEveningTime_IncludesTodayAndAtMostSevenEarlierDates()
    {
        var today = new DateOnly(2026, 11, 10);
        var dates = SummarySchedule.DailyDatesDue(today, new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero), new TimeOnly(18, 0), TimeZoneInfo.Utc, new DateOnly(2020, 1, 1));

        await Assert.That(dates[0]).IsEqualTo(today);
        await Assert.That(dates.Count).IsEqualTo(1 + SummarySchedule.CatchUpDays);
        await Assert.That(dates[^1]).IsEqualTo(today.AddDays(-SummarySchedule.CatchUpDays));
    }
}
