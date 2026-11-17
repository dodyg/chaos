namespace Chaos.Application.Contracts;

public enum PadItemKind
{
    Text = 0,
    Drawing = 1,
}

/// <summary>
/// The strokes of a drawing pad item (PLAN2 P10). <see cref="StrokesJson"/> is the whole drawing
/// (<c>{"v":1,"strokes":[...]}</c>), merged whole when two devices edit it. Width and Height are
/// the drawing units the strokes were drawn in. <see cref="Revision"/> is the editor's autosave
/// counter: a save with an older revision never replaces newer strokes.
/// </summary>
public sealed record PadDrawingRecord(
    string PadItemId,
    string StrokesJson,
    int Width,
    int Height,
    string? PreviewAttachmentId,
    long Revision,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>A rectangle in drawing units: the part of a drawing a task was made from.</summary>
public sealed record PadRegion(double X, double Y, double W, double H);

/// <summary>
/// One Personal scratch pad item (PLAN §5 ScratchPadItems, §9): untitled raw Markdown with a
/// manual position, optional archive and a draft version. DraftVersion is the highest editor
/// revision the store has acknowledged, so a delayed or repeated autosave can never replace
/// newer text.
/// </summary>
public sealed record PadItemRecord : AggregateRecord
{
    /// <summary>Typed text (default) or a drawing; the numbers are stored (migration 032).</summary>
    public PadItemKind Kind { get; init; } = PadItemKind.Text;
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
    string? ContextAfter = null,
    string? RegionJson = null);
