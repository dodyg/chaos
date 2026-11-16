using Chaos.Domain.Tasks;

namespace Chaos.Application.Contracts;

/// <summary>What a board's settings row is attached to (PLAN2 P2; views arrive in P3).</summary>
public enum BoardTargetKind
{
    Container = 1,
    View = 2,
}

/// <summary>How the columns of a board are chosen (PLAN2 §11.1).</summary>
public enum BoardGrouping
{
    Status = 0,
    Section = 1,
    Priority = 2,
    TagGroup = 3,
}

/// <summary>How the swimlanes of a board are chosen (PLAN2 §11.1).</summary>
public enum BoardSwimlane
{
    None = 0,
    Priority = 1,
    TagGroup = 2,
    Category = 3,
    Container = 4,
}

/// <summary>
/// One target's board settings (PLAN2 §11.1, migration 025). The row is created the first
/// time the person changes anything about the board; a target with no row uses the defaults.
/// The ID is deterministic (boardsetting:{targetKind}:{targetId}) so two devices that
/// configure the same target offline converge on one row.
/// </summary>
public sealed record BoardSettingsRecord : AggregateRecord
{
    public required BoardTargetKind TargetKind { get; init; }
    public required string TargetId { get; init; }
    public required BoardGrouping GroupBy { get; init; }
    public required BoardSwimlane SwimlaneBy { get; init; }
    /// <summary>The parent tag whose direct children are the columns; required when GroupBy is TagGroup.</summary>
    public string? ColumnTagId { get; init; }
    /// <summary>The parent tag whose direct children are the lanes; required when SwimlaneBy is TagGroup.</summary>
    public string? SwimlaneTagId { get; init; }
    /// <summary>JSON object mapping a column key to its WIP limit (a warning only).</summary>
    public required string WipLimitsJson { get; init; }
    /// <summary>JSON array of hidden column keys.</summary>
    public required string HiddenColumnsJson { get; init; }
}

/// <summary>One card's data beyond the task row: subtask progress and the GitHub issue mark.</summary>
public sealed record BoardCardRow(TaskRecord Task, int SubtasksTotal, int SubtasksDone, bool HasIssueLink);

/// <summary>Subtask progress and the GitHub issue mark of one task.</summary>
public sealed record BoardCardExtras(int SubtasksTotal, int SubtasksDone, bool HasIssueLink);

/// <summary>One active task-tag link of a container's tasks.</summary>
public sealed record BoardCardTag(string TaskId, string TagId);
