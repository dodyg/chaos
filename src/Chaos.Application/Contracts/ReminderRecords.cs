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
