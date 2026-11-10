using Chaos.Domain.Planning;
using Chaos.Domain.Tasks;

namespace Chaos.Domain.Tests.Planning;

public sealed class CapacityTests
{
    [Test]
    [Arguments(DayOfWeek.Sunday, "2026-11-08")]
    [Arguments(DayOfWeek.Monday, "2026-11-09")]
    [Arguments(DayOfWeek.Saturday, "2026-11-07")]
    [Arguments(DayOfWeek.Tuesday, "2026-11-10")]
    public async Task StartOf_UsesTheConfiguredFirstWeekday(DayOfWeek first, string expected)
    {
        var start = WeekCalendar.StartOf(new DateOnly(2026, 11, 10), first);
        await Assert.That(start).IsEqualTo(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(WeekCalendar.EndOf(start)).IsEqualTo(start.AddDays(6));
    }

    [Test]
    public async Task Dates_AcrossYearBoundary_AreSevenConsecutiveLocalDates()
    {
        var dates = WeekCalendar.Dates(WeekCalendar.StartOf(new DateOnly(2027, 1, 1), DayOfWeek.Monday));
        await Assert.That(dates.Count).IsEqualTo(7);
        await Assert.That(dates[0]).IsEqualTo(new DateOnly(2026, 12, 28));
        await Assert.That(dates[6]).IsEqualTo(new DateOnly(2027, 1, 3));
        await Assert.That(WeekCalendar.Overlaps(new DateOnly(2026, 12, 27), new DateOnly(2026, 12, 28))).IsTrue();
        await Assert.That(WeekCalendar.Overlaps(new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 28))).IsFalse();
    }

    [Test]
    public async Task Measure_MissingEstimates_AreCountedSeparatelyNeverAsZero()
    {
        var load = Capacity.Measure(new DayAvailability(60, null),
            [new CapacityItem(30, CapacityCategory.Personal, true), new CapacityItem(null, CapacityCategory.Personal, true), new CapacityItem(500, CapacityCategory.Personal, false)]);
        await Assert.That(load.PlannedMinutes).IsEqualTo(30);
        await Assert.That(load.UnknownEstimates).IsEqualTo(1);
        await Assert.That(load.OpenCount).IsEqualTo(2);
        await Assert.That(load.OverTotal).IsFalse();
    }

    [Test]
    public async Task Measure_ZeroAvailability_WarnsEvenForUnknownEstimates()
    {
        var load = Capacity.Measure(new DayAvailability(0, null), [new CapacityItem(null, CapacityCategory.Personal, true)]);
        await Assert.That(load.OverTotal).IsTrue();
        await Assert.That(Capacity.Measure(new DayAvailability(0, null), []).OverTotal).IsFalse();
    }

    [Test]
    public async Task Measure_WorkAllowance_CountsOnlyWorkItems()
    {
        var items = new[] { new CapacityItem(90, CapacityCategory.Work, true), new CapacityItem(120, CapacityCategory.Personal, true) };
        var load = Capacity.Measure(new DayAvailability(480, 60), items);
        await Assert.That(load.WorkMinutes).IsEqualTo(90);
        await Assert.That(load.PlannedMinutes).IsEqualTo(210);
        await Assert.That(load.OverWork).IsTrue();
        await Assert.That(load.OverTotal).IsFalse();
        await Assert.That(Capacity.Measure(new DayAvailability(480, null), items).OverWork).IsFalse();
    }

    [Test]
    public async Task Problem_RejectsWorkAboveTotalAndOutOfRangeValues()
    {
        await Assert.That(new DayAvailability(60, 90).Problem()).IsNotNull();
        await Assert.That(new DayAvailability(-1, null).Problem()).IsNotNull();
        await Assert.That(new DayAvailability(1441, null).Problem()).IsNotNull();
        await Assert.That(new DayAvailability(480, 480).Problem()).IsNull();
        await Assert.That(DayAvailability.DefaultFor(DayOfWeek.Saturday).TotalMinutes).IsEqualTo(0);
        await Assert.That(DayAvailability.DefaultFor(DayOfWeek.Wednesday).TotalMinutes).IsEqualTo(480);
    }
}
