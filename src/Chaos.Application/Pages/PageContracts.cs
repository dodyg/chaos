using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Pages;

public sealed record CreatePageCommand(string OperationId, string? ParentPageId, string Title, string Markdown);

/// <summary>Stores a daily page the first time something is written on it (SPEC "created the first time you write in it").</summary>
public sealed record CreateDailyPageCommand(string OperationId, DateOnly Date, string Markdown);

/// <summary>
/// An autosave of one page's title and text. <see cref="ClientRevision"/> is the editor's
/// increasing revision: the store applies it only when newer than the one it acknowledged.
/// </summary>
public sealed record SavePageCommand(string PageId, long ClientRevision, string Title, string Markdown);

/// <summary>Moves a page under another page (or to the top level when null) at a sibling index.</summary>
public sealed record MovePageCommand(string OperationId, string PageId, long ExpectedVersion, string? ParentPageId, int InsertionIndex);

public sealed record PageCommand(string OperationId, string PageId, long ExpectedVersion);

public sealed record RestorePageVersionCommand(string OperationId, string PageId, long ExpectedVersion, long Number);

/// <summary>"Make task" on a selection of a page; offsets are UTF-16 into the text at <see cref="SourceRevision"/>.</summary>
public sealed record MakeTaskFromPageCommand(string OperationId, string PageId, long SourceRevision, int Start, int End, string Quote, CaptureTaskDraft Draft);

/// <summary>Adds text to the end of a date's daily page, creating the page when the day has none.</summary>
public sealed record AppendToDailyPageCommand(string OperationId, DateOnly Date, string Markdown);

/// <summary>Turns a pad note into a page: the page holds the note's text and the note is deleted (restorable).</summary>
public sealed record MovePadNoteToPageCommand(string OperationId, string PadItemId, long ExpectedVersion, string? ParentPageId, string? Title);

/// <summary>Appends a pad note to a date's daily page and archives the note.</summary>
public sealed record AppendPadNoteToDailyCommand(string OperationId, string PadItemId, long ExpectedVersion, DateOnly Date);

/// <summary>A page as the tree, links and lists show it, without its text.</summary>
public sealed record PageSummary(string Id, string? ParentPageId, string Title, long Position, DateOnly? DailyDate, string? Icon,
    IReadOnlyList<string> Aliases, long Version, DateTimeOffset UpdatedAtUtc, bool Deleted, int WordCount);

public sealed record PageTaskLink(PageTaskLinkRecord Link, string Title, bool Deleted);

/// <summary>Something that links to a page: another page or a task, with the line the link sits on.</summary>
public sealed record PageBacklink(string Kind, string Id, string Title, string Line);

/// <summary>A page as its view shows it: the row, front-matter tags, tasks made from it and backlinks.</summary>
public sealed record PageView(PageRecord Page, string? Icon, IReadOnlyList<string> TagIds, IReadOnlyList<PageTaskLink> Tasks, IReadOnlyList<PageBacklink> Backlinks);

public sealed record PageMutationResult(PageRecord Page, bool Replayed);

public sealed record PagesResult(IReadOnlyList<PageRecord> Pages, bool Replayed);

public sealed record PageSaveResult(PageRecord Page, bool Applied);

public sealed record MakeTaskFromPageResult(TaskRecord Task, PageTaskLinkRecord Link, bool Replayed);

/// <summary>Where a task's page backlink points now.</summary>
public sealed record PageAnchor(string PageId, string TaskId, PadAnchorState State, int? Start, int? End, string Quote, bool Deleted);

/// <summary>A readable label for an internal page link, resolved by ID.</summary>
public sealed record PageLinkTarget(string Id, string Title, string? Icon, bool Deleted, DateOnly? DailyDate);
