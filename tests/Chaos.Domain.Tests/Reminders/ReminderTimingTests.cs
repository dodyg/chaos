using Chaos.Domain.Reminders;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Tests.Reminders;

public sealed class ReminderTimingTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Test]
    public async Task AnchorInstant_DateOnly_UsesTheDefaultTimeInTheProfileZone()
    {
        var anchor = ReminderTiming.AnchorInstant(new DateOnly(2026, 11, 12), null, NewYork, new TimeOnly(9, 0));
        await Assert.That(anchor).IsEqualTo(Utc(11, 12, 14));
    }

    [Test]
    public async Task ResolveLocal_TimeInSpringGap_MovesForwardByTheGap()
    {
        // 2026-03-08 02:30 does not exist in New York; it resolves to 03:30 EDT.
        var resolved = ReminderTiming.ResolveLocal(NewYork, new DateOnly(2026, 3, 8), new TimeOnly(2, 30));
        await Assert.That(resolved).IsEqualTo(Utc(3, 8, 7, 30));
    }

    [Test]
    public async Task ResolveLocal_AmbiguousAutumnTime_TakesTheEarlierInstant()
    {
        // 2026-11-01 01:30 happens twice in New York; the first (EDT, UTC-4) is chosen.
        var resolved = ReminderTiming.ResolveLocal(NewYork, new DateOnly(2026, 11, 1), new TimeOnly(1, 30));
        await Assert.That(resolved).IsEqualTo(Utc(11, 1, 5, 30));
    }

    [Test]
    public async Task OffsetTarget_NegativeIsBefore()
    {
        await Assert.That(ReminderTiming.OffsetTarget(Utc(11, 12, 14), -60)).IsEqualTo(Utc(11, 12, 13));
        await Assert.That(ReminderTiming.OffsetTarget(Utc(11, 12, 14), 30)).IsEqualTo(Utc(11, 12, 14, 30));
    }

    [Test]
    public async Task Add_DaysAcrossDstChange_KeepsTheWallTime_HoursKeepElapsedTime()
    {
        var saturdayMorning = Utc(10, 31, 13); // 09:00 EDT
        await Assert.That(ReminderTiming.Add(saturdayMorning, 2, DurationUnit.Days, NewYork)).IsEqualTo(Utc(11, 2, 14)); // 09:00 EST
        await Assert.That(ReminderTiming.Add(saturdayMorning, 48, DurationUnit.Hours, NewYork)).IsEqualTo(Utc(11, 2, 13));
        await Assert.That(ReminderTiming.Add(saturdayMorning, 1, DurationUnit.Weeks, NewYork)).IsEqualTo(Utc(11, 7, 14));
    }

    [Test]
    [Arguments(23, 30, true)]
    [Arguments(2, 0, true)]
    [Arguments(7, 0, false)]
    [Arguments(21, 59, false)]
    [Arguments(22, 0, true)]
    public async Task IsQuiet_CrossingMidnight_IsHalfOpen(int hour, int minute, bool quiet)
    {
        await Assert.That(ReminderTiming.IsQuiet(new TimeOnly(hour, minute), new TimeOnly(22, 0), new TimeOnly(7, 0))).IsEqualTo(quiet);
    }

    [Test]
    public async Task AfterQuietHours_BeforeMidnight_MovesToTomorrowsEnd_AfterMidnight_ToTodaysEnd()
    {
        var evening = ReminderTiming.AfterQuietHours(Utc(11, 10, 23, 30), TimeZoneInfo.Utc, true, new TimeOnly(22, 0), new TimeOnly(7, 0));
        var night = ReminderTiming.AfterQuietHours(Utc(11, 11, 3), TimeZoneInfo.Utc, true, new TimeOnly(22, 0), new TimeOnly(7, 0));
        var day = ReminderTiming.AfterQuietHours(Utc(11, 11, 12), TimeZoneInfo.Utc, true, new TimeOnly(22, 0), new TimeOnly(7, 0));

        await Assert.That(evening).IsEqualTo(Utc(11, 11, 7));
        await Assert.That(night).IsEqualTo(Utc(11, 11, 7));
        await Assert.That(day).IsEqualTo(Utc(11, 11, 12));
    }

    [Test]
    public async Task AfterQuietHours_DisabledOrEmpty_LeavesTheInstant()
    {
        await Assert.That(ReminderTiming.AfterQuietHours(Utc(11, 10, 23), TimeZoneInfo.Utc, false, new TimeOnly(22, 0), new TimeOnly(7, 0))).IsEqualTo(Utc(11, 10, 23));
        await Assert.That(ReminderTiming.AfterQuietHours(Utc(11, 10, 23), TimeZoneInfo.Utc, true, new TimeOnly(22, 0), new TimeOnly(22, 0))).IsEqualTo(Utc(11, 10, 23));
    }

    [Test]
    public async Task FollowUpDue_Inactivity_CountsFromTheLaterOfActivityAndWaitingStart()
    {
        var started = Utc(11, 10, 9);
        var due = ReminderTiming.FollowUpDue(FollowUpKind.AfterInactivity, null, 3, DurationUnit.Days, started, null, Utc(11, 9, 9), TimeZoneInfo.Utc);
        var afterActivity = ReminderTiming.FollowUpDue(FollowUpKind.AfterInactivity, null, 3, DurationUnit.Days, started, null, Utc(11, 12, 9), TimeZoneInfo.Utc);

        await Assert.That(due).IsEqualTo(Utc(11, 13, 9));
        await Assert.That(afterActivity).IsEqualTo(Utc(11, 15, 9));
    }

    [Test]
    public async Task FollowUpDue_BeforeDueWithoutDue_IsSuspended()
    {
        await Assert.That(ReminderTiming.FollowUpDue(FollowUpKind.BeforeDue, null, 1, DurationUnit.Days, Utc(11, 10, 9), null, null, TimeZoneInfo.Utc)).IsNull();
    }

    [Test]
    public async Task SnoozeUntil_Tomorrow_UsesTheDefaultTimeOnTheNextLocalDate()
    {
        var lateEvening = Utc(11, 11, 3); // 22:00 EST on 10 Nov
        await Assert.That(ReminderTiming.SnoozeUntil(SnoozeChoice.Tomorrow, lateEvening, NewYork, new TimeOnly(9, 0), null)).IsEqualTo(Utc(11, 11, 14));
        await Assert.That(ReminderTiming.SnoozeUntil(SnoozeChoice.TenMinutes, lateEvening, NewYork, new TimeOnly(9, 0), null)).IsEqualTo(Utc(11, 11, 3, 10));
    }

    [Test]
    public async Task SnoozeUntil_ChosenTimeInThePast_IsRejected()
    {
        DomainValidationException? error = null;
        try { ReminderTiming.SnoozeUntil(SnoozeChoice.Until, Utc(11, 10, 9), TimeZoneInfo.Utc, new TimeOnly(9, 0), Utc(11, 10, 8)); }
        catch (DomainValidationException caught) { error = caught; }
        await Assert.That(error).IsNotNull();
    }
}
