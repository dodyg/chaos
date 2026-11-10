using Chaos.Application.Abstractions;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tasks;

/// <summary>
/// Field-level validation for task lifecycle commands (PLAN §6): limits stated in errors,
/// a time requires a date and a zone, zones are validated canonical IDs, and enums are
/// checked against their defined values. Everything user-entered is preserved otherwise.
/// </summary>
internal static class TaskFieldValidation
{
    public static string Title(string? raw, string fieldPath)
    {
        try
        {
            return TaskTitle.Normalize(raw);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation(fieldPath, error.Message);
        }
    }

    public static string Notes(string? raw, string fieldPath)
    {
        try
        {
            return TaskNotes.Normalize(raw);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation(fieldPath, error.Message);
        }
    }

    public static int? Estimate(int? raw, string fieldPath)
    {
        try
        {
            return EstimateMinutes.Normalize(raw);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation(fieldPath, error.Message);
        }
    }

    public static string? Zone(string? raw, string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(raw.Trim());
            return raw.Trim();
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw AppException.Validation(fieldPath, "That time zone is not recognized on this computer. Pick a zone from the list.");
        }
    }

    public static TaskPriority Priority(TaskPriority value, string fieldPath)
    {
        if (!Enum.IsDefined(value))
        {
            throw AppException.Validation(fieldPath, "Choose one of the five priorities: None, Low, Normal, High or Urgent.");
        }

        return value;
    }

    public static CapacityCategory Category(CapacityCategory value, string fieldPath)
    {
        if (!Enum.IsDefined(value))
        {
            throw AppException.Validation(fieldPath, "Choose either the personal or the work capacity category.");
        }

        return value;
    }

    public static TaskItemStatus Status(TaskItemStatus value, string fieldPath)
    {
        if (!Enum.IsDefined(value))
        {
            throw AppException.Validation(fieldPath, "That status does not exist. Use one of the six task statuses.");
        }

        return value;
    }

    /// <summary>A time requires a date and a zone (PLAN §5 connection rules); a zone is validated.</summary>
    public static (DateOnly? Date, TimeOnly? Time, string? ZoneId) Anchor(
        DateOnly? date, TimeOnly? time, string? zoneId, string dateField, string timeField, string zoneField)
    {
        var normalizedZone = Zone(zoneId, zoneField);

        if (time is not null && date is null)
        {
            throw AppException.Validation(timeField, "A time needs a date. Pick the date this time belongs to.");
        }

        if (time is not null && normalizedZone is null)
        {
            throw AppException.Validation(zoneField, "A time needs a time zone. Keep the zone that came with the time.");
        }

        return (date, time, normalizedZone);
    }
}
