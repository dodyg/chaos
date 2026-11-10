using Chaos.Domain.Recurrence;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Tests.Recurrence;

public sealed class RecurrenceCalculatorTests
{
    private static RecurrenceRule Rule(DateOnly anchor, RecurrenceFrequency frequency) =>
        new(RecurrenceMode.FixedSchedule, frequency, 1, anchor, null, "UTC", [], null, null, null, null,
            RecurrenceEndKind.Never, null, null);

    [Test]
    public async Task Expand_MonthEnd_SkipsMissingDates()
    {
        var dates = RecurrenceCalculator.Expand(Rule(new(2024, 1, 31), RecurrenceFrequency.Month), new(2024, 1, 1), new(2024, 5, 31), 10);
        await Assert.That(dates.Select(DateOnly.FromDateTime).ToArray()).IsEquivalentTo(new[] { new DateOnly(2024, 1, 31), new(2024, 3, 31), new(2024, 5, 31) });
    }

    [Test]
    public async Task Expand_LeapDay_SkipsNonLeapYears()
    {
        var dates = RecurrenceCalculator.Expand(Rule(new(2024, 2, 29), RecurrenceFrequency.Year), new(2024, 1, 1), new(2032, 12, 31), 10);
        await Assert.That(dates.Select(DateOnly.FromDateTime).ToArray()).IsEquivalentTo(new[] { new DateOnly(2024, 2, 29), new(2028, 2, 29), new(2032, 2, 29) });
    }

    [Test]
    public async Task Expand_LastFriday_UsesLastMatchingWeekday()
    {
        var rule = Rule(new(2024, 1, 1), RecurrenceFrequency.Month) with { Ordinal = -1, OrdinalWeekday = DayOfWeek.Friday };
        var dates = RecurrenceCalculator.Expand(rule, new(2024, 2, 1), new(2024, 3, 31), 10);
        await Assert.That(dates.Select(DateOnly.FromDateTime).ToArray()).IsEquivalentTo(new[] { new DateOnly(2024, 2, 23), new(2024, 3, 29) });
    }

    [Test]
    public async Task Expand_AlternateWeeksAndUntil_IncludesOnlySelectedSlots()
    {
        var rule = Rule(new(2024, 1, 1), RecurrenceFrequency.Week) with { Interval = 2, Weekdays = [DayOfWeek.Monday, DayOfWeek.Friday], EndKind = RecurrenceEndKind.UntilDate, UntilDate = new(2024, 1, 19) };
        var dates = RecurrenceCalculator.Expand(rule, new(2024, 1, 1), new(2024, 2, 1), 10);
        await Assert.That(dates.Select(DateOnly.FromDateTime).ToArray()).IsEquivalentTo(new[] { new DateOnly(2024, 1, 1), new(2024, 1, 5), new(2024, 1, 15), new(2024, 1, 19) });
    }

    [Test]
    public async Task AfterCompletion_MonthEnd_ClampsFromCompletion()
    {
        var rule = Rule(new(2024, 1, 1), RecurrenceFrequency.Month) with { Mode = RecurrenceMode.AfterCompletion };
        var next = RecurrenceCalculator.AfterCompletion(rule, new DateTime(2024, 1, 31, 14, 30, 0));
        await Assert.That(next).IsEqualTo(new DateTime(2024, 2, 29, 14, 30, 0));
    }

    [Test]
    public async Task Validate_CompletionRuleWithSelectors_RejectsContradiction()
    {
        var rule = Rule(new(2024, 1, 1), RecurrenceFrequency.Week) with { Mode = RecurrenceMode.AfterCompletion, Weekdays = [DayOfWeek.Monday] };
        await Assert.That(() => rule.Validate()).Throws<DomainValidationException>();
    }

    [Test]
    public async Task Expand_Cap_StopsAtRequestedNumber()
    {
        var dates = RecurrenceCalculator.Expand(Rule(new(2024, 1, 1), RecurrenceFrequency.Day), new(2024, 1, 1), new(2024, 12, 31), 2);
        await Assert.That(dates.Count).IsEqualTo(2);
        await Assert.That(dates[1]).IsEqualTo(new DateTime(2024, 1, 2));
    }

    [Test]
    public async Task NextFixed_LateCompletion_SuppressesMissedSlots()
    {
        var rule = Rule(new(2024, 1, 1), RecurrenceFrequency.Day) with { AnchorTime = new(9, 0) };
        var next = RecurrenceCalculator.NextFixed(rule, new(2024, 1, 1, 9, 0, 0), new(2024, 1, 10, 10, 0, 0), new(2024, 1, 20));
        await Assert.That(next).IsEqualTo(new DateTime(2024, 1, 11, 9, 0, 0));
    }

    [Test]
    public async Task NextFixed_AllDayToday_RemainsEligible()
    {
        var rule = Rule(new(2024, 1, 1), RecurrenceFrequency.Day);
        var next = RecurrenceCalculator.NextFixed(rule, new(2024, 1, 1), new(2024, 1, 10, 23, 59, 0), new(2024, 1, 20));
        await Assert.That(next).IsEqualTo(new DateTime(2024, 1, 10));
    }
}
