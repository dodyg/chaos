namespace Chaos.Application.Contracts;

/// <summary>
/// One Personal scratch pad item (PLAN §5 ScratchPadItems, §9): untitled raw Markdown with a
/// manual position, optional archive and a draft version. DraftVersion is the highest editor
/// revision the store has acknowledged, so a delayed or repeated autosave can never replace
/// newer text.
/// </summary>
public sealed record PadItemRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string RawMarkdown { get; init; }
    public required long Position { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
    public required long DraftVersion { get; init; }
}

/// <summary>
/// The backlink from a task to the pad selection it was made from (PLAN §5 ScratchTaskLinks).
/// Start/End are UTF-16 offsets into the item's text at <see cref="SourceRevision"/> (its
/// DraftVersion then); <see cref="Quote"/> is the selected text, the fallback anchor once the
/// text changes. The context strings hold a few characters on each side of it then, which
/// pick the right copy when the quote occurs more than once.
/// </summary>
public sealed record ScratchTaskLinkRecord(
    string PadItemId,
    string TaskId,
    long SourceRevision,
    int StartIndex,
    int EndIndex,
    string Quote,
    DateTimeOffset CreatedAtUtc,
    string? ContextBefore = null,
    string? ContextAfter = null);
