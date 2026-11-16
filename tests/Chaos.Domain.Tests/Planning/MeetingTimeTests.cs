using Chaos.Domain.Planning;

namespace Chaos.Domain.Tests.Planning;

/// <summary>Busy-minute and free-time arithmetic (PLAN2 §10.4): overlaps, all-day, midnight and daylight saving.</summary>
public sealed class MeetingTimeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Day = new(2026, 11, 16);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);

    [Test]
    public async Task OverlappingMeetings_AreCountedOnce()
    {
        MeetingInterval[] meetings =
        [
            MeetingInterval.Timed(At(16, 9), At(16, 10, 30)),
            MeetingInterval.Timed(At(16, 10), At(16, 11)),
            MeetingInterval.Timed(At(16, 10, 15), At(16, 10, 45)),
            MeetingInterval.Timed(At(16, 14), At(16, 15)),
        ];

        await Assert.That(MeetingTime.BusyMinutes(Day, meetings, Utc)).IsEqualTo(120 + 60);
    }

    [Test]
    public async Task FreeMeetings_AndNoMeetings_TakeNoTime()
    {
        await Assert.That(MeetingTime.BusyMinutes(Day, [MeetingInterval.Timed(At(16, 9), At(16, 10), busy: false)], Utc)).IsEqualTo(0);
        await Assert.That(MeetingTime.BusyMinutes(Day, [], Utc)).IsEqualTo(0);
    }

    [Test]
    public async Task AMeetingAcrossMidnight_CountsItsShareOnEachDay()
    {
        MeetingInterval[] meetings = [MeetingInterval.Timed(At(16, 23), At(17, 1, 30))];

        await Assert.That(MeetingTime.BusyMinutes(Day, meetings, Utc)).IsEqualTo(60);
        await Assert.That(MeetingTime.BusyMinutes(Day.AddDays(1), meetings, Utc)).IsEqualTo(90);
        await Assert.That(MeetingTime.BusyMinutes(Day.AddDays(2), meetings, Utc)).IsEqualTo(0);
    }

    [Test]
    public async Task ABusyAllDayMeeting_FillsEachDayItCovers_AndAFreeOneNone()
    {
        var multi = MeetingInterval.AllDayRange(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 18), busy: true);
        var free = MeetingInterval.AllDayRange(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17), busy: false);

        await Assert.That(MeetingTime.BusyMinutes(Day, [multi], Utc)).IsEqualTo(24 * 60);
        await Assert.That(MeetingTime.BusyMinutes(Day.AddDays(1), [multi], Utc)).IsEqualTo(24 * 60);
        await Assert.That(MeetingTime.BusyMinutes(Day.AddDays(2), [multi], Utc)).IsEqualTo(0);
        await Assert.That(MeetingTime.BusyMinutes(Day, [free], Utc)).IsEqualTo(0);
    }

    [Test]
    public async Task OnADaylightSavingDay_TheDayIsAsLongAsItReallyIs()
    {
        // Europe/London ends daylight saving on 2026-10-25: the local day has 25 hours.
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var day = new DateOnly(2026, 10, 25);
        var (start, end) = MeetingTime.DayBounds(day, london);
        await Assert.That((end - start).TotalMinutes).IsEqualTo(25 * 60);

        // A busy all-day meeting fills all 25 hours; a timed one across the repeated hour counts real elapsed time.
        await Assert.That(MeetingTime.BusyMinutes(day, [MeetingInterval.AllDayRange(day, day.AddDays(1), busy: true)], london)).IsEqualTo(25 * 60);
        var across = MeetingInterval.Timed(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero));
        await Assert.That(MeetingTime.BusyMinutes(day, [across], london)).IsEqualTo(120);
    }

    [Test]
    public async Task OnTheSpringForwardDay_TheDayHasTwentyThreeHours()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var day = new DateOnly(2026, 3, 8);
        var (start, end) = MeetingTime.DayBounds(day, newYork);
        await Assert.That((end - start).TotalMinutes).IsEqualTo(23 * 60);
    }

    [Test]
    public async Task Zone_DecidesWhichLocalDayAMeetingBelongsTo()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        // 16:00 UTC on the 16th is 01:00 on the 17th in Tokyo.
        MeetingInterval[] meetings = [MeetingInterval.Timed(At(16, 16), At(16, 17))];

        await Assert.That(MeetingTime.BusyMinutes(Day, meetings, tokyo)).IsEqualTo(0);
        await Assert.That(MeetingTime.BusyMinutes(Day.AddDays(1), meetings, tokyo)).IsEqualTo(60);
    }

    [Test]
    public async Task FreeMinutes_AreAvailableMinusBusy_AndNeverNegative()
    {
        MeetingInterval[] meetings = [MeetingInterval.Timed(At(16, 9), At(16, 11))];
        var tomorrow = At(15, 12);

        await Assert.That(MeetingTime.FreeMinutes(Day, tomorrow, 480, meetings, Utc)).IsEqualTo(480 - 120);
        await Assert.That(MeetingTime.FreeMinutes(Day, tomorrow, 90, meetings, Utc)).IsEqualTo(0);
    }

    [Test]
    public async Task FreeMinutes_Today_StopAtTheEndOfTheDay_AndSkipMeetingsAlreadyOver()
    {
        MeetingInterval[] meetings = [MeetingInterval.Timed(At(16, 9), At(16, 11)), MeetingInterval.Timed(At(16, 20), At(16, 21))];

        // 15:00: eight available hours minus both meetings is 300 minutes, but only nine hours remain, less the 20:00 meeting.
        await Assert.That(MeetingTime.FreeMinutes(Day, At(16, 15), 480, meetings, Utc)).IsEqualTo(300);
        // 21:30: 150 minutes of the day are left and no meeting follows.
        await Assert.That(MeetingTime.FreeMinutes(Day, At(16, 21, 30), 480, meetings, Utc)).IsEqualTo(150);
        // After the day, nothing is free.
        await Assert.That(MeetingTime.FreeMinutes(Day, At(17, 1), 480, meetings, Utc)).IsEqualTo(0);
    }

    [Test]
    public async Task AfterMeetings_ReducesTotalAndWork_ButNeverBelowZero()
    {
        await Assert.That(new DayAvailability(480, 360).AfterMeetings(120)).IsEqualTo(new DayAvailability(360, 360));
        await Assert.That(new DayAvailability(480, 360).AfterMeetings(240)).IsEqualTo(new DayAvailability(240, 240));
        await Assert.That(new DayAvailability(480, null).AfterMeetings(600)).IsEqualTo(new DayAvailability(0, null));
        await Assert.That(new DayAvailability(480, 360).AfterMeetings(0)).IsEqualTo(new DayAvailability(480, 360));
    }
}
