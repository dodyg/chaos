using Chaos.Domain.Validation;

namespace Chaos.Domain.Recurrence;

/// <summary>Pure local-calendar calculations; materialization and actionable counts belong to the caller.</summary>
public static class RecurrenceCalculator
{
    /// <summary>
    /// Select the next fixed slot after the previous nominal key, suppressing missed slots.
    /// All-day slots remain eligible throughout their local date; timed slots must not have passed.
    /// The caller supplies a bounded search horizon and enforces the actionable count.
    /// </summary>
    public static DateTime? NextFixed(RecurrenceRule rule, DateTime previousNominal, DateTime localNow, DateOnly horizon)
    {
        var today = DateOnly.FromDateTime(localNow);
        if (horizon < today) throw new DomainValidationException("The search horizon must include today.");
        var previousDate = DateOnly.FromDateTime(previousNominal);
        var start = previousDate > today ? previousDate : today;
        if (start > horizon) return null;
        foreach (var slot in Expand(rule, start, horizon, 2))
        {
            if (slot <= previousNominal) continue;
            if (rule.AnchorTime is not null && slot < localNow) continue;
            return slot;
        }
        return null;
    }

    /// <summary>Returns fixed schedule slots in an inclusive, explicitly bounded range.</summary>
    public static IReadOnlyList<DateTime> Expand(RecurrenceRule rule, DateOnly from, DateOnly through, int cap)
    {
        rule.Validate();
        if (rule.Mode != RecurrenceMode.FixedSchedule || from > through || cap is < 1 or > 10000)
            throw new DomainValidationException("Provide a fixed rule, a valid date range and a cap between 1 and 10000.");
        var first = from > rule.AnchorDate ? from : rule.AnchorDate;
        var last = rule.UntilDate is { } until && until < through ? until : through;
        var result = new List<DateTime>();
        if (first > last) return result;
        for (var day = first; ; day = day.AddDays(1))
        {
            if (Matches(rule, day))
            {
                result.Add(day.ToDateTime(rule.AnchorTime ?? TimeOnly.MinValue));
                if (result.Count == cap) break;
            }
            if (day == last) break;
        }
        return result;
    }

    /// <summary>Advance from the local completion or skip time, clamping month and year arithmetic.</summary>
    public static DateTime? AfterCompletion(RecurrenceRule rule, DateTime localCompletion)
    {
        rule.Validate();
        if (rule.Mode != RecurrenceMode.AfterCompletion)
            throw new DomainValidationException("This rule is not completion-relative.");
        DateTime next;
        try
        {
            next = rule.Frequency switch
            {
                RecurrenceFrequency.Day => localCompletion.AddDays(rule.Interval),
                RecurrenceFrequency.Week => localCompletion.AddDays(7 * rule.Interval),
                RecurrenceFrequency.Month => localCompletion.AddMonths(rule.Interval),
                RecurrenceFrequency.Year => localCompletion.AddYears(rule.Interval),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (ArgumentOutOfRangeException) { return null; }
        return rule.UntilDate is { } until && DateOnly.FromDateTime(next) > until ? null : next;
    }

    private static bool Matches(RecurrenceRule rule, DateOnly day)
    {
        var monthDelta = (day.Year - rule.AnchorDate.Year) * 12 + day.Month - rule.AnchorDate.Month;
        return rule.Frequency switch
        {
            RecurrenceFrequency.Day => (day.DayNumber - rule.AnchorDate.DayNumber) % rule.Interval == 0,
            RecurrenceFrequency.Week => WeekMatches(rule, day),
            RecurrenceFrequency.Month => monthDelta % rule.Interval == 0 && MonthMatches(rule, day),
            RecurrenceFrequency.Year => (day.Year - rule.AnchorDate.Year) % rule.Interval == 0 &&
                day.Month == (rule.MonthOfYear ?? rule.AnchorDate.Month) && MonthMatches(rule, day),
            _ => false,
        };
    }

    private static bool WeekMatches(RecurrenceRule rule, DateOnly day)
    {
        // Monday is the stable boundary for interval weeks, independent of UI week settings.
        var anchorMonday = rule.AnchorDate.DayNumber - (((int)rule.AnchorDate.DayOfWeek + 6) % 7);
        var monday = day.DayNumber - (((int)day.DayOfWeek + 6) % 7);
        return ((monday - anchorMonday) / 7) % rule.Interval == 0 &&
            (rule.Weekdays.Count == 0 ? day.DayOfWeek == rule.AnchorDate.DayOfWeek : rule.Weekdays.Contains(day.DayOfWeek));
    }

    private static bool MonthMatches(RecurrenceRule rule, DateOnly day)
    {
        if (rule.Ordinal is { } ordinal && rule.OrdinalWeekday is { } weekday)
            return day.DayOfWeek == weekday && (ordinal == -1
                ? day.Day + 7 > DateTime.DaysInMonth(day.Year, day.Month)
                : (day.Day - 1) / 7 + 1 == ordinal);
        return day.Day == (rule.MonthDay ?? rule.AnchorDate.Day);
    }
}
