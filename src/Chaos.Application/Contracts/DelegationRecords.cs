using Chaos.Sync;

namespace Chaos.Application.Contracts;

/// <summary>
/// One delegation of a task to a member (PLAN3 9.11): who asked whom, the brief, and where it
/// stands. The brief is the delegator's and never changes; the state moves only along
/// <see cref="DelegationTransitions"/>. A retired row is a tombstone.
/// </summary>
public sealed record TaskDelegationRecord : AggregateRecord
{
    public required string TaskId { get; init; }
    public required string FromMemberId { get; init; }
    public required string ToMemberId { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>What "done" looks like, in the delegator's words.</summary>
    public string DoneDefinition { get; init; } = string.Empty;
    /// <summary>The date the delegator asked for; the task's own due date is unchanged.</summary>
    public DateOnly? DueDate { get; init; }
    public bool ReviewRequired { get; init; }
    public required DelegationState State { get; init; }
    public required DateTimeOffset StateChangedAtUtc { get; init; }
    /// <summary>The recipient's reason for declining or their question.</summary>
    public string? Response { get; init; }
}

/// <summary>A nudge the delegator sent the recipient about a delegation: a record, never edited.</summary>
public sealed record NudgeRecord
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required string DelegationId { get; init; }
    public required string FromMemberId { get; init; }
    public required string ToMemberId { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset SentAtUtc { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
