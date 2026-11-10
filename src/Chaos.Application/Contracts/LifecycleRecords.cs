using Chaos.Domain.Tasks;

namespace Chaos.Application.Contracts;

/// <summary>A subtask row (PLAN §5 Subtasks): no independent space/container/recurrence columns.</summary>
public sealed record SubtaskRecord : AggregateRecord
{
    public required string TaskId { get; init; }
    public required string Title { get; init; }
    public string Notes { get; init; } = string.Empty;
    public required TaskItemStatus Status { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? CancelledAtUtc { get; init; }
    public required long Position { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTerminal => TaskItemStatusRules.IsTerminal(Status);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCompleted => TaskItemStatusRules.IsCompleted(Status);
}

/// <summary>A section of one container (PLAN §5 Sections); tasks reference (Id, ContainerId).</summary>
public sealed record SectionRecord : AggregateRecord
{
    public required string ContainerId { get; init; }
    public required string Title { get; init; }
    public required long Position { get; init; }
}

/// <summary>
/// A reusable task template row (PLAN §5 TaskTemplates). The payload is a versioned JSON
/// envelope (see <see cref="Chaos.Application.Abstractions.StoredJsonFormats"/>) around a
/// <see cref="TemplatePayload"/>; dates are relative day offsets, never absolute timestamps.
/// </summary>
public sealed record TemplateRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string Name { get; init; }
    public required string NormalizedName { get; init; }
    public required string PayloadJson { get; init; }
}

/// <summary>
/// Task fields carried by a template (PLAN §6 template rules). Planned/due are stored as
/// nonnegative day offsets from the instantiation day, plus optional fixed wall time and
/// zone, so an instantiated task gets fresh dates. Subtasks carry structure only.
/// </summary>
public sealed record TemplatePayload
{
    public required string Title { get; init; }
    public string Notes { get; init; } = string.Empty;
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public int? EstimateMinutes { get; init; }
    public CapacityCategory CapacityCategory { get; init; } = CapacityCategory.Personal;
    public int? PlannedDayOffset { get; init; }
    public TimeOnly? PlannedTime { get; init; }
    public string? PlannedZoneId { get; init; }
    public int? DueDayOffset { get; init; }
    public TimeOnly? DueTime { get; init; }
    public string? DueZoneId { get; init; }
    public IReadOnlyList<TemplateSubtask> Subtasks { get; init; } = [];
}

/// <summary>Subtask structure inside a template payload (title/notes/order; always open when instantiated).</summary>
public sealed record TemplateSubtask
{
    public required string Title { get; init; }
    public string Notes { get; init; } = string.Empty;
    public required long Position { get; init; }
}
