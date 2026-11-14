namespace Chaos.Application.Contracts;

/// <summary>
/// One page (SPEC Pages): titled Markdown in a space, optionally under another page, or a
/// daily page holding one local date. DraftVersion is the highest editor revision the store
/// acknowledged, so a delayed or repeated autosave never replaces newer text.
/// </summary>
public sealed record PageRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public string? ParentPageId { get; init; }
    public required string Title { get; init; }
    public required string RawMarkdown { get; init; }
    public required long Position { get; init; }
    public DateOnly? DailyDate { get; init; }
    public required long DraftVersion { get; init; }
}

/// <summary>One earlier version of a page: the title and text as they were after one sitting of writing.</summary>
public sealed record PageRevisionRecord(string PageId, long Number, string Title, string RawMarkdown, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

/// <summary>The backlink from a task to the page selection it was made from (same anchor rules as the pad).</summary>
public sealed record PageTaskLinkRecord(
    string PageId,
    string TaskId,
    long SourceRevision,
    int StartIndex,
    int EndIndex,
    string Quote,
    DateTimeOffset CreatedAtUtc,
    string? ContextBefore = null,
    string? ContextAfter = null);
