using Chaos.Domain.Tasks;

namespace Chaos.Application.Transfer;

/// <summary>Import sources Chaos reads deterministically (PLAN §14). Values match ImportBatches.SourceKind.</summary>
public enum ImportSourceKind
{
    ChaosJson = 0,
    Org = 1,
    Csv = 2,
}

/// <summary>A local date with an optional time and zone, as read from a source.</summary>
public sealed record ImportDate(DateOnly Date, TimeOnly? Time, string? ZoneId);

public sealed record ImportSubtask(string Title, string Notes, TaskItemStatus Status, string? PreservedId = null);

public sealed record ImportDiaryEntry(DateTimeOffset EntryAtUtc, string Markdown);

/// <summary>
/// One top-level task read from a source, before anything is written. <see cref="SourceKey"/>
/// identifies the item within its format (a stable ID when the source has one, otherwise the
/// row or heading) and feeds the import fingerprint that prevents duplicates on re-import.
/// </summary>
public sealed record ImportTask
{
    public required int Line { get; init; }
    public required string SourceKey { get; init; }
    /// <summary>True when <see cref="SourceKey"/> is a stable ID from the source rather than content.</summary>
    public bool HasStableKey { get; init; }
    /// <summary>The task ID to keep (Chaos JSON merge); null gives the task a new ID.</summary>
    public string? PreservedId { get; init; }
    public required string Title { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string? ListName { get; init; }
    public string? PreservedListId { get; init; }
    public TaskItemStatus Status { get; init; } = TaskItemStatus.NotStarted;
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public int? EstimateMinutes { get; init; }
    public CapacityCategory Category { get; init; } = CapacityCategory.Personal;
    public ImportDate? Planned { get; init; }
    public ImportDate? Due { get; init; }
    public DateTimeOffset? ClosedAtUtc { get; init; }
    public IReadOnlyList<string> TagPaths { get; init; } = [];
    public IReadOnlyList<ImportSubtask> Subtasks { get; init; } = [];
    public IReadOnlyList<ImportDiaryEntry> Diary { get; init; } = [];
}

/// <summary>A problem found while reading or validating a source. Line 0 means the whole file.</summary>
public sealed record ImportIssue(int Line, string Field, string Message);

/// <summary>Everything a source contained, parsed and validated, with nothing written yet.</summary>
public sealed record ImportDraft(
    ImportSourceKind Kind,
    string SourceName,
    string FileFingerprint,
    IReadOnlyList<ImportTask> Tasks,
    IReadOnlyList<ImportIssue> Errors,
    IReadOnlyList<ImportIssue> Warnings);

/// <summary>How one source list will be placed: an existing list or a new one.</summary>
public sealed record ImportListPlan(string SourceName, int TaskCount, string? ExistingContainerId, string? ExistingName);

public enum ImportItemOutcome
{
    New = 0,
    Duplicate = 1,
    Conflict = 2,
}

public sealed record ImportPreviewItem(int Line, string Title, string? ListName, TaskItemStatus Status, int Subtasks, ImportItemOutcome Outcome, string? Detail);

/// <summary>What a commit would do, for the user to review (PLAN §14: counts, warnings, duplicates, target lists).</summary>
public sealed record ImportPreview(
    ImportSourceKind Kind,
    string SourceName,
    string FileFingerprint,
    int NewTasks,
    int Duplicates,
    int Conflicts,
    int Subtasks,
    int DiaryEntries,
    IReadOnlyList<ImportListPlan> Lists,
    IReadOnlyList<ImportPreviewItem> Items,
    IReadOnlyList<ImportIssue> Errors,
    IReadOnlyList<ImportIssue> Warnings)
{
    public bool CanCommit => Errors.Count == 0 && NewTasks > 0;
}

/// <summary>
/// The user's choices for a commit. <see cref="ListTargets"/> maps a source list name to an
/// existing container ID; unmapped names use a same-named list or create one. Items without
/// a list go to <see cref="DefaultContainerId"/> (the Inbox when null).
/// </summary>
public sealed record ImportCommitOptions(
    IReadOnlyDictionary<string, string> ListTargets,
    string? DefaultContainerId,
    string? BackupFileName);

public sealed record ImportResult(string BatchId, int Created, int Duplicates, int Conflicts, IReadOnlyList<string> CreatedTaskIds, IReadOnlyList<string> CreatedListNames);

public sealed record ImportBatchRecord(
    string Id,
    string ProfileId,
    ImportSourceKind SourceKind,
    string SourceName,
    string FileFingerprint,
    DateTimeOffset ImportedAtUtc,
    int CreatedCount,
    int SkippedCount,
    string? BackupFileName);
