namespace Chaos.Application.Contracts;

/// <summary>How a task came to be on a day plan (PLAN §10).</summary>
public enum DayPlanItemOrigin
{
    Manual = 0,
    /// <summary>Seeded once from a confirmed weekly plan (package 9).</summary>
    Week = 1,
    /// <summary>Carried over explicitly from the previous day.</summary>
    CarryOver = 2,
}

/// <summary>
/// One profile's plan for one local date (PLAN §5 DayPlans). Created once per date; never
/// deleted, so earlier days stay reviewable.
/// </summary>
public sealed record DayPlanRecord
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public string? SeededWeekPlanId { get; init; }
    public long? SeededWeekRevision { get; init; }
    public required DateTimeOffset InitializedAtUtc { get; init; }
    public required long Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// A task's membership in one day plan (PLAN §5 DayPlanItems). Removing sets
/// <see cref="RemovedAtUtc"/> and keeps the row, so a removed task is never silently put back.
/// </summary>
public sealed record DayPlanItemRecord
{
    public required string Id { get; init; }
    public required string DayPlanId { get; init; }
    public required string TaskId { get; init; }
    public required long Position { get; init; }
    public required DayPlanItemOrigin Origin { get; init; }
    public required DateTimeOffset AddedAtUtc { get; init; }
    public DateTimeOffset? RemovedAtUtc { get; init; }
    public required long Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
