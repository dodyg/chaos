namespace Chaos.Application.Contracts;

public sealed record RecurrenceSeriesRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string ContainerId { get; init; }
    public required string RuleJson { get; init; }
    public required string TemplateJson { get; init; }
    public required long Revision { get; init; }
    public required long ActionableCount { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    /// <summary>PLAN3 9.10: <c>{"members":[ids],"start":n}</c>, shared spaces only; null when the series does not rotate.</summary>
    public string? RotationJson { get; init; }
}

public enum OccurrenceState { Active, Completed, Skipped }

public sealed record RecurrenceOccurrenceRecord : AggregateRecord
{
    public required string SeriesId { get; init; }
    public required string NominalKey { get; init; }
    public required string ScheduleKey { get; init; }
    public string? TaskId { get; init; }
    public required string EffectiveLocal { get; init; }
    public string? EditSnapshotJson { get; init; }
    public required OccurrenceState State { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public required long SeriesRevision { get; init; }
    /// <summary>Position in the series' rotation, fixed when the occurrence is created; null when it does not rotate.</summary>
    public int? RotationIndex { get; init; }
}
