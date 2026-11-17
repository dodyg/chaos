using Chaos.Domain.Tasks;

namespace Chaos.Application.Contracts;

/// <summary>
/// A saved custom view (PLAN2 §11.2, migration 026). <see cref="DefinitionJson"/> is the
/// version-1 condition tree and <see cref="LayoutJson"/> the chosen layout; both are validated
/// before they are stored. The row is a synced aggregate; alert baselines are device-local.
/// </summary>
public sealed record SavedViewRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public string? Color { get; init; }
    public required bool Pinned { get; init; }
    public required int Position { get; init; }
    public required string DefinitionJson { get; init; }
    public required string LayoutJson { get; init; }
    public required bool AlertsEnabled { get; init; }
}

/// <summary>One task's alert baseline entry on this device: when it joined the view and when it left.</summary>
public sealed record SavedViewMatchRecord(string ViewId, string TaskId, DateTimeOffset FirstSeenUtc, DateTimeOffset? LeftAtUtc);

public enum ViewAlertState { Pending = 0, Delivered = 1, Acknowledged = 2, Cancelled = 4 }

/// <summary>
/// One grouped alert for a view (PLAN2 §11.2, migration 027): the tasks that newly matched since the
/// last evaluation, counted once per view. Device-local; delivered after quiet hours.
/// </summary>
public sealed record ViewAlertRecord
{
    public required string Id { get; init; }
    public required string ViewId { get; init; }
    public required int TaskCount { get; init; }
    public required IReadOnlyList<string> TaskIds { get; init; }
    public required string FirstTaskTitle { get; init; }
    public required DateTimeOffset DeliverAtUtc { get; init; }
    public required ViewAlertState State { get; init; }
    public DateTimeOffset? DeliveredAtUtc { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
    public NotificationPresentation Presentation { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>The sort a custom view applies; the field names are fixed by <c>ViewLayout</c>.</summary>
public sealed record ViewSort(string Field, bool Descending);

/// <summary>
/// A condition tree with every clock- and zone-dependent value resolved by the application, so the
/// store only translates it to parameterized SQL (PLAN2 §11.2). Operators are normalized:
/// in, notIn (ids, statuses), is (single number, category, source, flag), atLeast, atMost,
/// hasAnyOf, hasAllOf, hasNone, range, isEmpty, isNotEmpty, lessThan, moreThan, contains,
/// overdue and never (a condition nothing can satisfy).
/// </summary>
public abstract record ResolvedViewNode;

public sealed record ResolvedViewGroup(bool All, IReadOnlyList<ResolvedViewNode> Items) : ResolvedViewNode;

public sealed record ResolvedViewCondition(string Field, string Operator) : ResolvedViewNode
{
    /// <summary>Ids (container, section, tag) or the single source name.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary>Status, priority or category as their stored integers; a flag as 0/1.</summary>
    public IReadOnlyList<long> Numbers { get; init; } = [];

    public string? Text { get; init; }
    public bool Descendants { get; init; }

    /// <summary>Range bounds as stored text: local dates for date fields, UTC instants for instant fields. Lower is inclusive, upper exclusive; null is open.</summary>
    public string? Lower { get; init; }

    public string? Upper { get; init; }

    /// <summary>The overdue predicate's per-zone "now" and local today, resolved exactly as the built-in Overdue view does.</summary>
    public DateOnly? OverdueBefore { get; init; }

    public IReadOnlyList<ZoneCutoff> Cutoffs { get; init; } = [];
}

/// <summary>What a custom view's quick add can pre-fill (PLAN2 §11.2 "Add task in context").</summary>
public sealed record ViewContextDefaults
{
    public string? ContainerId { get; init; }
    public TaskItemStatus? Status { get; init; }
    public TaskPriority? Priority { get; init; }
    public CapacityCategory? Category { get; init; }
    public IReadOnlyList<string> TagIds { get; init; } = [];
    public DateOnly? PlannedDate { get; init; }
    public DateOnly? DueDate { get; init; }
}
