namespace Chaos.Application.Contracts;

/// <summary>
/// A dated Markdown update on a task (PLAN §5 DiaryEntries, §9). The author is the local
/// profile; EntryAtUtc is captured explicitly when the entry is written. Revision counts the
/// saved states kept in <see cref="DiaryRevisionRecord"/>.
/// </summary>
public sealed record DiaryEntryRecord : AggregateRecord
{
    public required string TaskId { get; init; }
    public required string AuthorProfileId { get; init; }
    public required DateTimeOffset EntryAtUtc { get; init; }
    public required string RawMarkdown { get; init; }
    public required long Revision { get; init; }
    public DateTimeOffset? EditedAtUtc { get; init; }
}

/// <summary>One saved state of a diary entry; the store appends one for every add and update.</summary>
public sealed record DiaryRevisionRecord(string EntryId, long Revision, string RawMarkdown, bool Deleted, DateTimeOffset RecordedAtUtc);

/// <summary>
/// One reversible user operation (PLAN §5 UndoEntries, §9). InversePayload holds the
/// versioned change set (before-images); ExpectedVersions maps each affected entity to the
/// version the operation left it at, so a later edit makes the undo a conflict.
/// </summary>
public sealed record UndoEntryRecord
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required string OperationId { get; init; }
    public required string Label { get; init; }
    public required string InversePayload { get; init; }
    public required string ExpectedVersions { get; init; }
    public required DateTimeOffset AppliedAtUtc { get; init; }
    public DateTimeOffset? UndoneAtUtc { get; init; }
}

/// <summary>Marker that one checklist item was already converted into a subtask (PLAN §9).</summary>
public sealed record ChecklistConversionRecord(string TaskId, string ItemKey, string SubtaskId, string OperationId, DateTimeOffset CreatedAtUtc);
