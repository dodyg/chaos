namespace Chaos.Domain.Reminders;

/// <summary>Units of a follow-up interval (stored as integers; PLAN §11).</summary>
public enum DurationUnit
{
    Minutes = 0,
    Hours = 1,
    Days = 2,
    Weeks = 3,
}

/// <summary>
/// When a follow-up with an outside person is due (PLAN §11): at a fixed instant, N units
/// after waiting started, N units before the task's due anchor, or N days without qualifying
/// task activity.
/// </summary>
public enum FollowUpKind
{
    Fixed = 0,
    AfterWaiting = 1,
    BeforeDue = 2,
    AfterInactivity = 3,
}

/// <summary>Snooze presets offered by a notification; <see cref="Until"/> is a chosen instant.</summary>
public enum SnoozeChoice
{
    TenMinutes = 0,
    OneHour = 1,
    Tomorrow = 2,
    Until = 3,
}

/// <summary>
/// Pure reminder timing (PLAN §11): anchors, quiet hours, follow-up deadlines and snoozes.
/// No clock access; every function takes the instant it reasons about.
/// </summary>
public static class ReminderTiming
{
    /// <summary>Insistent reminders repeat at most this often (PLAN §11: a minimum of five minutes).</summary>
    public const int MinimumInsistentMinutes = 5;
    public const int MaximumInsistentMinutes = 1440;
    /// <summary>Offsets are bounded to one year either side of the anchor.</summary>
    public const int MaximumOffsetMinutes = 525_600;
    public const int MaximumFollowUpAmount = 999;

    /// <summary>
    /// Resolves a local wall time in <paramref name="zone"/> to its UTC instant. A time skipped
    /// by a DST gap moves forward by the gap; an ambiguous time takes the earlier instant (PLAN §6).
    /// </summary>
    public static DateTimeOffset ResolveLocal(TimeZoneInfo zone, DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // The offset in force before the transition places the instant after the gap.
            return new DateTimeOffset(local, zone.GetUtcOffset(local.AddHours(-12))).ToUniversalTime();
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// The instant of a planned or due anchor. A timed anchor uses its own zone; a date-only
    /// anchor uses the profile zone and the default reminder time, never midnight UTC.
    /// </summary>
    public static DateTimeOffset AnchorInstant(DateOnly date, TimeOnly? time, TimeZoneInfo zone, TimeOnly defaultReminderTime) =>
        ResolveLocal(zone, date, time ?? defaultReminderTime);

    /// <summary>A relative rule's target: the anchor moved by <paramref name="offsetMinutes"/> (negative is before, as quick add stores "1h before" as -60).</summary>
    public static DateTimeOffset OffsetTarget(DateTimeOffset anchor, int offsetMinutes) => anchor.AddMinutes(offsetMinutes);

    /// <summary>
    /// Adds an interval. Minutes and hours are elapsed time; days and weeks move the local
    /// calendar date and keep the wall time, so a DST change does not shift the hour.
    /// </summary>
    public static DateTimeOffset Add(DateTimeOffset instant, int amount, DurationUnit unit, TimeZoneInfo zone)
    {
        switch (unit)
        {
            case DurationUnit.Minutes:
                return instant.AddMinutes(amount);
            case DurationUnit.Hours:
                return instant.AddHours(amount);
            case DurationUnit.Days:
            case DurationUnit.Weeks:
                var local = TimeZoneInfo.ConvertTime(instant, zone);
                var days = unit == DurationUnit.Weeks ? amount * 7 : amount;
                var date = DateOnly.FromDateTime(local.DateTime).AddDays(days);
                return ResolveLocal(zone, date, TimeOnly.FromDateTime(local.DateTime));
            default:
                throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown duration unit.");
        }
    }

    /// <summary>Whether a local time of day falls inside quiet hours [start, end); crossing midnight is allowed.</summary>
    public static bool IsQuiet(TimeOnly time, TimeOnly start, TimeOnly end) =>
        start < end ? time >= start && time < end : time >= start || time < end;

    /// <summary>
    /// Moves an ordinary delivery out of quiet hours to the next allowed instant (their end).
    /// Unchanged when quiet hours are off, incomplete or empty (start equals end).
    /// </summary>
    public static DateTimeOffset AfterQuietHours(DateTimeOffset instant, TimeZoneInfo zone, bool enabled, TimeOnly? start, TimeOnly? end)
    {
        if (!enabled || start is not { } from || end is not { } to || from == to)
        {
            return instant;
        }

        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (!IsQuiet(time, from, to))
        {
            return instant;
        }

        var date = DateOnly.FromDateTime(local.DateTime);
        // Crossing midnight: before midnight the quiet period ends tomorrow.
        var endDate = from > to && time >= from ? date.AddDays(1) : date;
        var resolved = ResolveLocal(zone, endDate, to);
        return resolved > instant ? resolved : instant;
    }

    /// <summary>
    /// A follow-up's due instant, or null when it cannot be computed (suspended): BeforeDue
    /// without a due anchor, AfterWaiting without a waiting start. Inactivity counts from the
    /// later of the last qualifying activity and the start of waiting.
    /// </summary>
    public static DateTimeOffset? FollowUpDue(
        FollowUpKind kind,
        DateTimeOffset? fixedAt,
        int? amount,
        DurationUnit? unit,
        DateTimeOffset? waitingStartedAt,
        DateTimeOffset? dueAnchor,
        DateTimeOffset? lastActivityAt,
        TimeZoneInfo zone)
    {
        switch (kind)
        {
            case FollowUpKind.Fixed:
                return fixedAt;
            case FollowUpKind.AfterWaiting:
                return waitingStartedAt is { } started && amount is { } after && unit is { } afterUnit ? Add(started, after, afterUnit, zone) : null;
            case FollowUpKind.BeforeDue:
                return dueAnchor is { } due && amount is { } before && unit is { } beforeUnit ? Add(due, -before, beforeUnit, zone) : null;
            case FollowUpKind.AfterInactivity:
                var from = Later(lastActivityAt, waitingStartedAt);
                return from is { } start && amount is { } days ? Add(start, days, DurationUnit.Days, zone) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// When a snoozed delivery comes back: 10 minutes or an hour from now, tomorrow at the
    /// default reminder time, or a chosen later instant. Snoozing never edits a due date.
    /// </summary>
    public static DateTimeOffset SnoozeUntil(SnoozeChoice choice, DateTimeOffset now, TimeZoneInfo zone, TimeOnly defaultReminderTime, DateTimeOffset? until)
    {
        switch (choice)
        {
            case SnoozeChoice.TenMinutes:
                return now.AddMinutes(10);
            case SnoozeChoice.OneHour:
                return now.AddHours(1);
            case SnoozeChoice.Tomorrow:
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
                return ResolveLocal(zone, today.AddDays(1), defaultReminderTime);
            case SnoozeChoice.Until:
                if (until is not { } chosen || chosen <= now)
                {
                    throw new Validation.DomainValidationException("Choose a snooze time later than now.");
                }

                return chosen;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown snooze choice.");
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
