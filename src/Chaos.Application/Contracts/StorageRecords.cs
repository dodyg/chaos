using System.Text.Json.Serialization;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Contracts;

/// <summary>Common audit columns for mutable aggregates (PLAN §5).</summary>
public abstract record AggregateRecord
{
    public required string Id { get; init; }
    public required long Version { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }
}

public sealed record ProfileRecord : AggregateRecord
{
    public required string DisplayName { get; init; }
    public required string ZoneId { get; init; }
    public required string Locale { get; init; }
    public required string DateFormat { get; init; } // DMY | MDY | YMD
    public required DayOfWeek FirstWeekday { get; init; }
    public TimeOnly? PlanningTime { get; init; }
    /// <summary>Evening daily summary time; null = not scheduled (PLAN §12).</summary>
    public TimeOnly? SummaryTime { get; init; }
    /// <summary>Morning summary time; null = not scheduled.</summary>
    public TimeOnly? MorningTime { get; init; }
    /// <summary>Weekday the weekly summary is due; null = not scheduled.</summary>
    public DayOfWeek? WeeklySummaryDay { get; init; }
    /// <summary>Whether plain summaries (and, later, the AI) may list the day's meetings; off by default (PLAN2 §10.4).</summary>
    public bool IncludeMeetingsInSummaries { get; init; }
    public required TimeOnly DefaultReminderTime { get; init; }
    public bool QuietHoursEnabled { get; init; }
    public TimeOnly? QuietStart { get; init; }
    public TimeOnly? QuietEnd { get; init; }
    /// <summary>Global capture shortcut, a normalized accelerator; null = off (PLAN §13).</summary>
    public string? CaptureShortcut { get; init; } = "Ctrl+Alt+N";
    /// <summary>Global Personal-pad shortcut; null = off.</summary>
    public string? PadShortcut { get; init; } = "Ctrl+Alt+P";
    /// <summary>Closing the window leaves Chaos running in the notification area.</summary>
    public bool KeepRunningInTray { get; init; } = true;
    // Q16 time blocking (group timeblock, client schema 13): null spaces means every space.
    public bool TimeBlockEnabled { get; init; }
    public string? TimeBlockAccountId { get; init; }
    public bool TimeBlockShowTitles { get; init; } = true;
    public string? TimeBlockRunnerDeviceId { get; init; }
    public string? TimeBlockCalendarId { get; init; }
    public string? TimeBlockSpacesJson { get; init; }
}

/// <summary>
/// The one Google event the runner keeps for a task (PLAN3 §11). <see cref="AggregateRecord.Id"/> is the task id;
/// <see cref="EventId"/> is derived from it, so two devices can never make two events for one task. The hash covers
/// the title, start, end and visibility that were pushed, so a pass knows whether Google still matches the task.
/// </summary>
public sealed record TimeBlockEventRecord : AggregateRecord
{
    public required string EventId { get; init; }
    public string? ETag { get; init; }
    public string? PushedHash { get; init; }
    public DateTimeOffset? LastSyncedAtUtc { get; init; }
    public DateTimeOffset? RemoteMovedAtUtc { get; init; }
}

/// <summary>This device's position in the app calendar's change feed (device-local: the runner reads it).</summary>
public sealed record TimeBlockState(string? CalendarId, string? SyncToken, string? PageToken, DateTimeOffset? LastPassUtc, string? LastError, bool NeedsSignIn);

public sealed record SpaceRecord : AggregateRecord
{
    public required string OwnerProfileId { get; init; }
    public required SpaceKind Kind { get; init; }
    public required string Name { get; init; }
    public string? DefaultDeadlineReminderJson { get; init; }
    public bool TagsOwnerOnly { get; init; }
    /// <summary>Agent settings of the space's <c>settings</c> group (PLAN3 12.3, 12.4, E8): whether agents may be added, how long a claim lasts, and whether an agent may close a task.</summary>
    public bool AgentsAllowed { get; init; }
    public int ClaimMinutes { get; init; } = Chaos.Sync.AgentLimits.DefaultClaimMinutes;
    public bool AllowAgentsToClose { get; init; }
}

public sealed record SpaceStateRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string Name { get; init; }
    public required string NormalizedName { get; init; }
    public required TaskItemStatus BaseStatus { get; init; }
    public required long Position { get; init; }
}

public sealed record ContainerRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required ContainerKind Kind { get; init; }
    public required string Name { get; init; }
    public bool IsInbox { get; init; }
    public required long Position { get; init; }
    public DateOnly? DeadlineDate { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
    /// <summary>Category new tasks captured into this container take; null means no default.</summary>
    public CapacityCategory? DefaultCategory { get; init; }
}

/// <summary>A task row (PLAN §5 Tasks). Planned/due keep local date/time/zone as task data; My Day membership etc. are profile-owned elsewhere.</summary>
public sealed record TaskRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string ContainerId { get; init; }
    public string? SectionId { get; init; }
    public required string Title { get; init; }
    public string Notes { get; init; } = string.Empty;
    public required TaskItemStatus Status { get; init; }
    public string? CustomStateId { get; init; }
    public TaskAssignMode AssignMode { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public int? EstimateMinutes { get; init; }
    public CapacityCategory CapacityCategory { get; init; } = CapacityCategory.Personal;
    public DateOnly? PlannedDate { get; init; }
    public TimeOnly? PlannedTime { get; init; }
    public string? PlannedZoneId { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public string? DueZoneId { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? CancelledAtUtc { get; init; }
    public required long Position { get; init; }
    public string? RecurrenceOccurrenceKey { get; init; }

    // Q17 agents (PLAN3 12.4): an exclusive, expiring hold on the task by a person or an agent, and whether a person
    // made it Ready for agent, optionally for one local agent. The claim is an actor ID (person:… or agent:…).
    public string? ClaimedById { get; init; }
    public DateTimeOffset? ClaimedAtUtc { get; init; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; init; }
    public bool AgentReady { get; init; }
    public string? AgentAssigneeId { get; init; }

    // Computed flags are derived from Status; excluding them keeps stored snapshots canonical.
    [JsonIgnore]
    public bool IsTerminal => TaskItemStatusRules.IsTerminal(Status);
    [JsonIgnore]
    public bool IsCompleted => TaskItemStatusRules.IsCompleted(Status);
}

public sealed record TaskAssigneeRecord(string TaskId, string MemberId, DateTimeOffset AddedAtUtc,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset? DeletedAtUtc);

public sealed record TaskHistoryRecord
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required string OperationId { get; init; }
    public required DateTimeOffset EffectiveAtUtc { get; init; }
    public required string Actor { get; init; }
    public required string EventKind { get; init; }
    public required IReadOnlyList<string> ChangedFields { get; init; }
    public string? SnapshotBefore { get; init; }
    public string? SnapshotAfter { get; init; }
    public required long AggregateVersion { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed record OperationReceiptRecord
{
    public required string ProfileId { get; init; }
    public required string OperationId { get; init; }
    public required string CommandKind { get; init; }
    public required string PayloadHash { get; init; }
    public required string ResultJson { get; init; }
    public required DateTimeOffset CommittedAtUtc { get; init; }
}

public enum BackupValidationStatus { Valid = 0, Unvalidated = 1, Invalid = 2 }

public enum BackupReason { PreMigration = 0, PreImport = 1, PreRestore = 2, Manual = 3, Scheduled = 4 }

public sealed record BackupCatalogRecord
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required long SchemaVersion { get; init; }
    public required string AppVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string ChecksumSha256 { get; init; }
    public required BackupValidationStatus ValidationStatus { get; init; }
    public required BackupReason Reason { get; init; }
}
