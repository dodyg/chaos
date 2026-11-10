using Chaos.Domain.Validation;

namespace Chaos.Domain.Recurrence;

public enum RecurrenceMode { FixedSchedule, AfterCompletion }
public enum RecurrenceFrequency { Day, Week, Month, Year }
public enum RecurrenceEndKind { Never, Count, UntilDate }

/// <summary>A versioned local-calendar rule. A null time denotes an all-day occurrence.</summary>
public sealed record RecurrenceRule(
    RecurrenceMode Mode,
    RecurrenceFrequency Frequency,
    int Interval,
    DateOnly AnchorDate,
    TimeOnly? AnchorTime,
    string ZoneId,
    IReadOnlyList<DayOfWeek> Weekdays,
    int? MonthDay,
    int? Ordinal,
    DayOfWeek? OrdinalWeekday,
    int? MonthOfYear,
    RecurrenceEndKind EndKind,
    int? Count,
    DateOnly? UntilDate,
    int SchemaVersion = 1)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(Mode) || !Enum.IsDefined(Frequency) || !Enum.IsDefined(EndKind))
            throw new DomainValidationException("This recurrence rule is not supported.");
        if (Interval is < 1 or > 999)
            throw new DomainValidationException("The recurrence interval must be between 1 and 999.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(ZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new DomainValidationException("Choose a valid recurrence time zone."); }
        if (Weekdays is null || Weekdays.Any(day => !Enum.IsDefined(day)) || Weekdays.Distinct().Count() != Weekdays.Count)
            throw new DomainValidationException("Choose distinct valid weekdays.");
        if (MonthDay is < 1 or > 31 || MonthOfYear is < 1 or > 12 || Ordinal is < -1 or 0 or > 5 ||
            (Ordinal is null) != (OrdinalWeekday is null) || (OrdinalWeekday is { } day && !Enum.IsDefined(day)))
            throw new DomainValidationException("Choose a valid calendar day or ordinal weekday.");
        if (MonthDay is not null && Ordinal is not null)
            throw new DomainValidationException("Choose either a calendar day or an ordinal weekday.");
        if (Mode == RecurrenceMode.AfterCompletion && (Weekdays.Count != 0 || MonthDay is not null || Ordinal is not null || MonthOfYear is not null))
            throw new DomainValidationException("Completion-relative rules cannot select calendar days.");
        if (Mode == RecurrenceMode.FixedSchedule &&
            ((Frequency != RecurrenceFrequency.Week && Weekdays.Count != 0) ||
             (Frequency is RecurrenceFrequency.Day or RecurrenceFrequency.Week && (MonthDay is not null || Ordinal is not null)) ||
             (Frequency != RecurrenceFrequency.Year && MonthOfYear is not null)))
            throw new DomainValidationException("Those calendar selectors do not apply to this frequency.");
        if ((EndKind == RecurrenceEndKind.Count && (Count is null or < 1 || UntilDate is not null)) ||
            (EndKind == RecurrenceEndKind.UntilDate && (UntilDate is null || UntilDate < AnchorDate || Count is not null)) ||
            (EndKind == RecurrenceEndKind.Never && (Count is not null || UntilDate is not null)))
            throw new DomainValidationException("Choose one valid recurrence end condition.");
    }
}
