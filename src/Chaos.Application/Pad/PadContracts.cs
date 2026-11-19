using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Pad;

public sealed record CreatePadItemCommand(string OperationId, string Markdown);

/// <summary>
/// An autosave of one item's text. <see cref="ClientRevision"/> is the editor's monotonically
/// increasing revision: the store applies the text only when it is newer than the revision
/// it already acknowledged, so a delayed, repeated or reordered save never replaces newer text.
/// </summary>
public sealed record SavePadTextCommand(string ItemId, long ClientRevision, string Markdown);

public sealed record CreatePadDrawingCommand(string OperationId, int Width = PadDrawingValidation.DefaultWidth, int Height = PadDrawingValidation.DefaultHeight);

/// <summary>
/// An autosave of a drawing's strokes, with the same revision rule as <see cref="SavePadTextCommand"/>.
/// <see cref="PreviewAttachmentId"/> (null keeps the current one) names a PNG attachment of this
/// item that becomes the preview; the previous preview is then removed.
/// </summary>
public sealed record SavePadDrawingCommand(string ItemId, long ClientRevision, string StrokesJson, string? PreviewAttachmentId = null);

/// <summary>"Make task" on a rectangle of a drawing; the region is in the drawing's own units.</summary>
public sealed record MakeTaskFromRegionCommand(
    string OperationId,
    string PadItemId,
    long SourceRevision,
    PadRegion Region,
    string Title,
    string? Notes,
    string? ContainerId,
    DateOnly? PlannedDate,
    DateOnly? DueDate,
    IReadOnlyList<string> TagIds,
    CaptureTaskDraft? Draft = null);

public sealed record PadItemChange(string ItemId, long ExpectedVersion);

public sealed record PadItemsCommand(string OperationId, IReadOnlyList<PadItemChange> Items);

public sealed record PadItemCommand(string OperationId, string ItemId, long ExpectedVersion);

public sealed record ReorderPadItemCommand(string OperationId, string ItemId, long ExpectedVersion, int InsertionIndex);

/// <summary>
/// "Make task" on a selection of a pad item (PLAN §9). Start/End are UTF-16 offsets into the
/// item's text at <see cref="SourceRevision"/> and <see cref="Quote"/> is the selected text.
/// The renderer parses the selection with quick add and sends the confirmed <see cref="Draft"/>;
/// without one, the explicit title/notes/list/date/tag fields describe the task.
/// </summary>
public sealed record MakeTaskFromPadCommand(
    string OperationId,
    string PadItemId,
    long SourceRevision,
    int Start,
    int End,
    string Quote,
    string Title,
    string? Notes,
    string? ContainerId,
    DateOnly? PlannedDate,
    DateOnly? DueDate,
    IReadOnlyList<string> TagIds,
    CaptureTaskDraft? Draft = null);

/// <summary>Finishes the weekly pad review, archiving the items the user chose (possibly none).</summary>
public sealed record CompletePadReviewCommand(string OperationId, IReadOnlyList<PadItemChange> Archive);

public sealed record PadTaskLink(ScratchTaskLinkRecord Link, string Title, bool Deleted);

/// <summary>A pad item as the pad shows it: the row, its active tags and the tasks made from it.</summary>
public sealed record PadItemView(PadItemRecord Item, IReadOnlyList<string> TagIds, IReadOnlyList<PadTaskLink> Tasks, PadDrawingRecord? Drawing = null);

public sealed record PadMutationResult(PadItemRecord Item, bool Replayed);

public sealed record PadDrawingView(PadItemRecord Item, PadDrawingRecord Drawing);

public sealed record PadDrawingSaveResult(PadItemRecord Item, PadDrawingRecord Drawing, bool Applied);

public sealed record PadItemsResult(IReadOnlyList<PadItemRecord> Items, bool Replayed);

/// <summary>Applied is false when the store already held this or a newer revision; Item is then the stored state.</summary>
public sealed record PadSaveResult(PadItemRecord Item, bool Applied);

public sealed record MakeTaskFromPadResult(TaskRecord Task, ScratchTaskLinkRecord Link, bool Replayed);

public enum PadAnchorState
{
    /// <summary>The item text is unchanged since the task was made: the stored offsets are valid.</summary>
    Exact = 0,
    /// <summary>The text changed but the quoted text is still there: these are its current offsets.</summary>
    Relocated = 1,
    /// <summary>The quoted text is gone: open the item and show the old quote, never another spot.</summary>
    Missing = 2,
}

/// <summary>Where a task's pad backlink points now (PLAN §9 stale-anchor fallback).</summary>
public sealed record PadAnchor(string PadItemId, string TaskId, PadAnchorState State, int? Start, int? End, string Quote, bool Archived, bool Deleted, PadRegion? Region = null);

public sealed record PadLinkTarget(string Id, string Excerpt, bool Deleted);

/// <summary>The weekly review prompt: due when a week passed since the last review and old items exist.</summary>
public sealed record PadReviewStatus(bool Due, DateTimeOffset? LastReviewedAtUtc, IReadOnlyList<PadItemRecord> Suggestions);
