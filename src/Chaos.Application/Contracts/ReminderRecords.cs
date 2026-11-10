namespace Chaos.Application.Contracts;

public enum ReminderAnchor { Absolute, Planned, Due }

/// <summary>A durable rule; package 10 supplies scheduling and delivery.</summary>
public sealed record ReminderRuleRecord : AggregateRecord
{
    public required string ProfileId { get; init; }
    public required string TaskId { get; init; }
    public required ReminderAnchor Anchor { get; init; }
    public int? OffsetMinutes { get; init; }
    public DateTimeOffset? AbsoluteAtUtc { get; init; }
    public int? InsistentMinutes { get; init; }
    public bool Enabled { get; init; }
}

/// <summary>Relative rule retained by a template; absolute reminders cannot be reused.</summary>
public sealed record TemplateReminderOffset(ReminderAnchor Anchor, int OffsetMinutes, int? InsistentMinutes);

/// <summary>Waiting on a person outside the app (PLAN §11): free text, nothing is ever sent.</summary>
public sealed record WaitingDetailRecord : AggregateRecord
{
    public required string TaskId { get; init; }
    public required string ProfileId { get; init; }
    public required string Person { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
}

/// <summary>
/// A follow-up rule for a waiting task. <see cref="DueAtUtc"/> is derived after committed
/// events (null when suspended) and is not part of the versioned state.
/// </summary>
public sealed record FollowUpRecord : AggregateRecord
{
    public required string TaskId { get; init; }
    public required string ProfileId { get; init; }
    public required Chaos.Domain.Reminders.FollowUpKind Kind { get; init; }
    public DateTimeOffset? FixedAtUtc { get; init; }
    public int? Amount { get; init; }
    public Chaos.Domain.Reminders.DurationUnit? Unit { get; init; }
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
    public DateTimeOffset? ResolvedAtUtc { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? DueAtUtc { get; init; }
}

public enum NotificationSourceKind { ReminderRule = 0, FollowUp = 1 }

public enum NotificationJobState { Pending = 0, Delivered = 1, Acknowledged = 2, Snoozed = 3, Cancelled = 4 }

public enum NotificationReason { Initial = 0, Snoozed = 1, Repeat = 2 }

/// <summary>How a delivered notification reached the person.</summary>
public enum NotificationPresentation { NotYet = 0, System = 1, InApp = 2, Suppressed = 3 }

public enum NotificationOsState { None = 0, Scheduled = 1, Failed = 2 }

/// <summary>
/// One desired or delivered notification (PLAN §11 ReminderDeliveries + NotificationJobs):
/// the persisted projection the reconciler schedules with the OS and the notification
/// center's durable history.
/// </summary>
public sealed record NotificationJobRecord
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required string TaskId { get; init; }
    public required NotificationSourceKind SourceKind { get; init; }
    public required string SourceId { get; init; }
    public required long Generation { get; init; }
    public required long Sequence { get; init; }
    public required NotificationReason Reason { get; init; }
    /// <summary>The original target; snoozes and repeats keep it.</summary>
    public required DateTimeOffset TargetAtUtc { get; init; }
    /// <summary>Snooze or repeat instant; delivery is never before it.</summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }
    /// <summary>When delivery is due after quiet hours.</summary>
    public required DateTimeOffset DeliverAtUtc { get; init; }
    public required NotificationJobState State { get; init; }
    public DateTimeOffset? DeliveredAtUtc { get; init; }
    public NotificationPresentation Presentation { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
    public string? ActionTaken { get; init; }
    public NotificationOsState OsState { get; init; }
    public string? OsNotificationId { get; init; }
    public int OsAttempts { get; init; }
    public DateTimeOffset? OsLastAttemptUtc { get; init; }
    public string? OsError { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
