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

/// <summary>Week plan lifecycle (PLAN §10). Only a Confirmed plan seeds My Day.</summary>
public enum WeekPlanState
{
    /// <summary>Never confirmed (or revived after a week-start change); only a draft exists.</summary>
    Draft = 0,
    Confirmed = 1,
    /// <summary>Replaced by plans on new week boundaries after the first weekday changed.</summary>
    Superseded = 2,
}

/// <summary>A goal's outcome, assessed by the person (PLAN §5 WeekGoals).</summary>
public enum WeekGoalOutcome
{
    Pending = 0,
    Achieved = 1,
    LetGo = 2,
}

/// <summary>How a task came to be in a week.</summary>
public enum WeekAssignmentOrigin
{
    Planned = 0,
    /// <summary>Carried over from the previous week's unfinished work.</summary>
    Carried = 1,
}

/// <summary>What the person decided for one unfinished task of the previous week (ritual step 2).</summary>
public enum LeftoverDecision
{
    /// <summary>Carry it into this week (it joins the week, on a day or in the pool).</summary>
    Carry = 0,
    /// <summary>Not this week; optionally with an explicitly chosen new planned date.</summary>
    Later = 1,
    /// <summary>Drop it from the personal plan. The task itself is untouched.</summary>
    Remove = 2,
    /// <summary>Explicitly cancel the task.</summary>
    Cancel = 3,
}

/// <summary>
/// One profile's plan for seven local dates (PLAN §5 WeekPlans). The draft payload is kept
/// apart from the confirmed goal/assignment rows.
/// </summary>
public sealed record WeekPlanRecord
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required DateOnly WeekStartDate { get; init; }
    public required WeekPlanState State { get; init; }
    public long ConfirmedRevision { get; init; }
    public DateTimeOffset? ConfirmedAtUtc { get; init; }
    public string? DraftPayload { get; init; }
    public long DraftRevision { get; init; }
    public long? DraftBaseRevision { get; init; }
    public required long Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record WeekGoalRecord
{
    public required string Id { get; init; }
    public required string WeekPlanId { get; init; }
    public required string Title { get; init; }
    public required long Position { get; init; }
    public string? ProjectId { get; init; }
    public WeekGoalOutcome Outcome { get; init; }
    public IReadOnlyList<string> TaskIds { get; init; } = [];
    public required long Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>A task's place in a confirmed week: one day, or the pool when <see cref="LocalDate"/> is null.</summary>
public sealed record WeekAssignmentRecord
{
    public required string Id { get; init; }
    public required string WeekPlanId { get; init; }
    public required string TaskId { get; init; }
    public DateOnly? LocalDate { get; init; }
    public required long Position { get; init; }
    public WeekAssignmentOrigin Origin { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>Stored availability: a weekday default (<see cref="Weekday"/>) or a one-date override (<see cref="Date"/>).</summary>
public sealed record AvailabilityRecord(DayOfWeek? Weekday, DateOnly? Date, int TotalMinutes, int? WorkMinutes);

// ---- Draft payload (stored as versioned JSON in WeekPlans.DraftPayload) ----

public sealed record WeekDraftGoal(string Id, string Title, string? ProjectId, IReadOnlyList<string> TaskIds, WeekGoalOutcome Outcome);

/// <summary>A task in the draft week; a null day is the unassigned pool. List order is the order within each day.</summary>
public sealed record WeekDraftAssignment(string TaskId, DateOnly? Day);

public sealed record WeekDraftLeftover(string TaskId, LeftoverDecision Decision, DateOnly? LaterDate);

public sealed record WeekDraftOverride(DateOnly Date, int TotalMinutes, int? WorkMinutes);

/// <summary>The reviewed week before confirmation; <see cref="Step"/> remembers where the ritual was left (1–5).</summary>
public sealed record WeekDraft(
    IReadOnlyList<WeekDraftGoal> Goals,
    IReadOnlyList<WeekDraftAssignment> Assignments,
    IReadOnlyList<WeekDraftLeftover> Leftovers,
    IReadOnlyList<WeekDraftOverride> Overrides,
    int Step)
{
    public static readonly WeekDraft Empty = new([], [], [], [], 1);
}
