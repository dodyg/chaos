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

/// <summary>
/// Pages (SPEC Pages): a tree of titled Markdown pages and one daily page per date, saved as
/// you type with version history, linked to each other and to tasks with backlinks, tagged
/// from front matter, and joined to the pad and to tasks through Make task. Structural changes
/// are recorded operations (receipt, history, undo); autosaves are not, as on the pad.
/// </summary>
public sealed partial class PageCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>Saves within this quiet gap of the newest version update it instead of starting a new one.</summary>
    public static readonly TimeSpan RevisionGap = TimeSpan.FromMinutes(10);

    /// <summary>A version never spans longer than this, even while writing continues.</summary>
    public static readonly TimeSpan RevisionSpan = TimeSpan.FromHours(1);

    public const int MaxTitleLength = 200;

    /// <summary>Characters of context kept on each side of a Make task quote.</summary>
    public const int ContextLength = PadCommands.ContextLength;

    public async Task<IReadOnlyList<PageSummary>> ListAsync(CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageSummary>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            return [.. (await session.Pages.ListBySpaceAsync(space.Id, token)).Where(page => page.DailyDate is null).Select(Summary)];
        }, ct);

    /// <summary>Daily pages newest first, optionally only between two dates (inclusive).</summary>
    public async Task<IReadOnlyList<PageSummary>> ListDailyAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageSummary>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            return [.. (await session.Pages.ListBySpaceAsync(space.Id, token))
                .Where(page => page.DailyDate is { } date && (from is null || date >= from) && (to is null || date <= to))
                .OrderByDescending(page => page.DailyDate)
                .Select(Summary)];
        }, ct);

    public async Task<IReadOnlyList<PageSummary>> ListDeletedAsync(CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageSummary>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            return [.. (await session.Pages.ListDeletedBySpaceAsync(space.Id, token)).Select(Summary)];
        }, ct);

    public async Task<PageView> GetAsync(string pageId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var page = await session.Pages.GetIncludingDeletedAsync(pageId, token) ?? throw NotFound();
            return await ViewAsync(session, page, token);
        }, ct);

    /// <summary>The daily page of a date, or null when nothing was written that day.</summary>
    public async Task<PageView?> GetDailyAsync(DateOnly date, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            return await session.Pages.FindDailyAsync(space.Id, date, token) is { } page ? await ViewAsync(session, page, token) : null;
        }, ct);

    public async Task<IReadOnlyList<PageSummary>> SearchAsync(string text, int limit, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageSummary>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            var pages = new List<PageSummary>();
            foreach (var id in await session.Pages.SearchAsync(space.Id, text, Math.Clamp(limit, 1, 100), token))
            {
                if (await session.Pages.GetAsync(id, token) is { } page)
                {
                    pages.Add(Summary(page));
                }
            }

            // Titles and aliases that start with the words match too, so [[ finds a page by any of its names.
            var key = text.Trim();
            if (key.Length > 0)
            {
                foreach (var page in await session.Pages.ListBySpaceAsync(space.Id, token))
                {
                    if (pages.Count >= limit) break;
                    if (pages.Any(found => found.Id == page.Id)) continue;
                    var names = PageFrontMatter.Parse(page.RawMarkdown).Aliases.Prepend(page.Title);
                    if (names.Any(name => name.Contains(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        pages.Add(Summary(page));
                    }
                }
            }

            return pages;
        }, ct);

    /// <summary>Pages tagged (in front matter) with the tag or a tag nested inside it.</summary>
    public async Task<IReadOnlyList<PageSummary>> ListByTagAsync(string tagId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageSummary>>(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw NoProfile();
            var all = await session.Tags.ListByOwnerAsync(profile.Id, token);
            var scope = new HashSet<string>(StringComparer.Ordinal) { tagId };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var tag in all)
                {
                    if (tag.ParentTagId is { } parent && scope.Contains(parent) && scope.Add(tag.Id))
                    {
                        grew = true;
                    }
                }
            }

            return [.. (await session.Pages.ListByTagsAsync(scope, token)).Select(Summary)];
        }, ct);

    /// <summary>Readable labels for internal page links, resolved by ID so renames and moves keep them working.</summary>
    public async Task<IReadOnlyList<PageLinkTarget>> ResolveLinksAsync(IReadOnlyList<string> pageIds, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageLinkTarget>>(async (session, token) =>
        {
            var targets = new List<PageLinkTarget>();
            foreach (var id in pageIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                if (await session.Pages.GetIncludingDeletedAsync(id, token) is { } page)
                {
                    targets.Add(new PageLinkTarget(page.Id, page.Title, PageFrontMatter.Parse(page.RawMarkdown).Icon, page.DeletedAtUtc is not null, page.DailyDate));
                }
            }

            return targets;
        }, ct);

    /// <summary>
    /// The pages that <c>[[Name]]</c> links point at, by title or alias (case-insensitive). A
    /// name no page has is left out: following that link creates the page.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, PageLinkTarget Target)>> ResolveNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<(string, PageLinkTarget)>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            var pages = await session.Pages.ListBySpaceAsync(space.Id, token);
            var found = new List<(string, PageLinkTarget)>();
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                var key = name.Trim();
                var page = pages.FirstOrDefault(page => string.Equals(page.Title, key, StringComparison.OrdinalIgnoreCase))
                    ?? pages.FirstOrDefault(page => PageFrontMatter.Parse(page.RawMarkdown).Aliases.Any(alias => string.Equals(alias, key, StringComparison.OrdinalIgnoreCase)));
                if (page is not null)
                {
                    var frontMatter = PageFrontMatter.Parse(page.RawMarkdown);
                    found.Add((name, new PageLinkTarget(page.Id, page.Title, frontMatter.Icon, false, page.DailyDate)));
                }
            }

            return found;
        }, ct);

    /// <summary>Earlier versions of a page, newest first.</summary>
    public async Task<IReadOnlyList<PageRevisionRecord>> ListRevisionsAsync(string pageId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            _ = await session.Pages.GetIncludingDeletedAsync(pageId, token) ?? throw NotFound();
            return await session.Pages.ListRevisionsAsync(pageId, token);
        }, ct);

    /// <summary>The pages that link to a task ("Mentioned in"), with the line of each link.</summary>
    public async Task<IReadOnlyList<PageBacklink>> MentionsOfTaskAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PageBacklink>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            var needle = $"chaos:task/{taskId}";
            return [.. (await session.Pages.ListContainingAsync(space.Id, [needle], token))
                .Select(page => new PageBacklink("page", page.Id, page.Title, LineContaining(page.RawMarkdown, [needle])))];
        }, ct);

    /// <summary>Where the page backlink of a task points now; null when the task was not made from a page.</summary>
    public async Task<PageAnchor?> LocateAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var link = await session.Pages.FindTaskLinkAsync(taskId, token);
            if (link is null)
            {
                return null;
            }

            var page = await session.Pages.GetIncludingDeletedAsync(link.PageId, token) ?? throw NotFound();
            var found = PadCommands.Locate(page.RawMarkdown, page.DraftVersion, AsPadLink(link));
            var state = found is null ? PadAnchorState.Missing
                : page.DraftVersion == link.SourceRevision && found.Value.Start == link.StartIndex ? PadAnchorState.Exact
                : PadAnchorState.Relocated;
            return new PageAnchor(page.Id, taskId, state, found?.Start, found?.End, link.Quote, page.DeletedAtUtc is not null);
        }, ct);

    public async Task<PageMutationResult> CreateAsync(CreatePageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with
        {
            Title = Title(command.Title),
            Markdown = TaskFieldValidation.Notes(command.Markdown, "markdown"),
            ParentPageId = string.IsNullOrWhiteSpace(command.ParentPageId) ? null : command.ParentPageId,
        };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "page.create", normalized, now, async inner =>
            {
                var (_, space) = await PersonalSpaceAsync(session, inner);
                await RequireParentAsync(session, space.Id, normalized.ParentPageId, null, inner);
                var page = await AddPageAsync(session, space.Id, normalized.ParentPageId, normalized.Title, normalized.Markdown, null, now, inner);
                return new PageMutationResult(page, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Stores the daily page of a date with its first text. When the day already has a page
    /// (written from elsewhere meanwhile) that page is returned unchanged, so the caller saves
    /// its text through the usual autosave instead.
    /// </summary>
    public async Task<PageMutationResult> CreateDailyAsync(CreateDailyPageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Markdown = TaskFieldValidation.Notes(command.Markdown, "markdown") };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "page.create-daily", normalized, now, async inner =>
            {
                var (_, space) = await PersonalSpaceAsync(session, inner);
                if (await session.Pages.FindDailyAsync(space.Id, normalized.Date, inner) is { } existing)
                {
                    return new PageMutationResult(existing, Replayed: false);
                }

                var page = await AddPageAsync(session, space.Id, null, StorageFormats.Date(normalized.Date), normalized.Markdown, normalized.Date, now, inner);
                return new PageMutationResult(page, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Autosave: applies the title and text when the revision is newer than the acknowledged one,
    /// and folds them into the newest version or starts a new one. Not an undo entry.
    /// </summary>
    public async Task<PageSaveResult> SaveAsync(SavePageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.ClientRevision < 1)
        {
            throw AppException.Validation("clientRevision", "The editor revision must be 1 or more.");
        }

        var markdown = TaskFieldValidation.Notes(command.Markdown, "markdown");
        return await InWriteAsync(async (session, _, now, token) =>
        {
            var page = await session.Pages.GetIncludingDeletedAsync(command.PageId, token) ?? throw NotFound();
            if (page.DeletedAtUtc is not null)
            {
                throw AppException.Validation("pageId", "This page was deleted. Restore it to keep writing; your text is still in the editor.");
            }

            if (command.ClientRevision <= page.DraftVersion)
            {
                return new PageSaveResult(page, Applied: false);
            }

            var title = page.DailyDate is null ? Title(command.Title) : page.Title;
            var applied = await session.Pages.UpdateAsync(
                page with { Title = title, RawMarkdown = markdown, DraftVersion = command.ClientRevision, Version = page.Version + 1, UpdatedAtUtc = now },
                page.Version, token) ?? throw Conflict();
            await RecordRevisionAsync(session, applied, now, forceNew: false, token);
            return new PageSaveResult(applied, Applied: true);
        }, ct);
    }

    /// <summary>Moves a page in the tree; a page can never go under itself or one of its own pages.</summary>
    public async Task<PageMutationResult> MoveAsync(MovePageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { ParentPageId = string.IsNullOrWhiteSpace(command.ParentPageId) ? null : command.ParentPageId };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "page.move", normalized, now, async inner =>
            {
                var page = await session.Pages.GetAsync(normalized.PageId, inner) ?? throw NotFound();
                RequireVersion(page, normalized.ExpectedVersion);
                if (page.DailyDate is not null)
                {
                    throw AppException.Validation("pageId", "Daily pages are listed by date and cannot be moved into the page tree.");
                }

                await RequireParentAsync(session, page.SpaceId, normalized.ParentPageId, page.Id, inner);
                var moved = page;
                if (page.ParentPageId != normalized.ParentPageId)
                {
                    moved = await session.Pages.UpdateAsync(page with
                    {
                        ParentPageId = normalized.ParentPageId,
                        Position = await session.Pages.GetNextPositionAsync(page.SpaceId, normalized.ParentPageId, inner),
                        Version = page.Version + 1,
                        UpdatedAtUtc = now,
                    }, page.Version, inner) ?? throw Conflict();
                }

                var siblings = (await session.Pages.ListBySpaceAsync(page.SpaceId, inner))
                    .Where(row => row.DailyDate is null && row.ParentPageId == normalized.ParentPageId)
                    .ToList();
                foreach (var assignment in RankOrdering.Place([.. siblings.Select(row => (row.Id, row.Position, row.Version))], moved.Id, normalized.InsertionIndex))
                {
                    var row = siblings.Single(candidate => candidate.Id == assignment.Id);
                    var applied = await session.Pages.UpdateAsync(row with { Position = assignment.NewPosition, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner)
                        ?? throw Conflict();
                    if (applied.Id == moved.Id)
                    {
                        moved = applied;
                    }
                }

                return new PageMutationResult(moved, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Deletes a page and every page under it, as one undoable change; Restore brings them back.</summary>
    public async Task<PagesResult> DeleteAsync(PageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "page.delete", command, now, async inner =>
            {
                var page = await session.Pages.GetIncludingDeletedAsync(command.PageId, inner) ?? throw NotFound();
                RequireVersion(page, command.ExpectedVersion);
                if (page.DeletedAtUtc is not null)
                {
                    return new PagesResult([page], Replayed: false);
                }

                var all = await session.Pages.ListBySpaceAsync(page.SpaceId, inner);
                var changed = new List<PageRecord>();
                foreach (var row in Subtree(all, page))
                {
                    changed.Add(await session.Pages.UpdateAsync(row with { DeletedAtUtc = now, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner) ?? throw Conflict());
                }

                return new PagesResult(changed, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Restores a deleted page with the pages deleted together with it. When its parent is
    /// still deleted it comes back at the top level; a daily page whose day was written again
    /// meanwhile cannot come back, since a day holds one page.
    /// </summary>
    public async Task<PagesResult> RestoreAsync(PageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "page.restore", command, now, async inner =>
            {
                var page = await session.Pages.GetIncludingDeletedAsync(command.PageId, inner) ?? throw NotFound();
                RequireVersion(page, command.ExpectedVersion);
                if (page.DeletedAtUtc is not { } deletedAt)
                {
                    return new PagesResult([page], Replayed: false);
                }

                if (page.DailyDate is { } date && await session.Pages.FindDailyAsync(page.SpaceId, date, inner) is not null)
                {
                    throw AppException.Validation("pageId", "That day already has a daily page. Copy what you need from the deleted page's versions instead.");
                }

                var parentActive = page.ParentPageId is null || await session.Pages.GetAsync(page.ParentPageId, inner) is not null;
                var deleted = await session.Pages.ListDeletedBySpaceAsync(page.SpaceId, inner);
                var changed = new List<PageRecord>();
                foreach (var row in Subtree(deleted, page).Where(row => row.DeletedAtUtc == deletedAt))
                {
                    var restored = row with { DeletedAtUtc = null, Version = row.Version + 1, UpdatedAtUtc = now };
                    if (row.Id == page.Id && !parentActive)
                    {
                        restored = restored with { ParentPageId = null, Position = await session.Pages.GetNextPositionAsync(page.SpaceId, null, inner) };
                    }

                    changed.Add(await session.Pages.UpdateAsync(restored, row.Version, inner) ?? throw Conflict());
                }

                return new PagesResult(changed, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Brings back the title and text of an earlier version as a new version; undo returns to the text before.</summary>
    public async Task<PageMutationResult> RestoreVersionAsync(RestorePageVersionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "page.restore-version", command, now, async inner =>
            {
                var page = await session.Pages.GetAsync(command.PageId, inner) ?? throw NotFound();
                RequireVersion(page, command.ExpectedVersion);
                var revision = await session.Pages.GetRevisionAsync(page.Id, command.Number, inner)
                    ?? throw AppException.NotFound("That version of the page no longer exists.");
                var applied = await session.Pages.UpdateAsync(page with
                {
                    Title = page.DailyDate is null ? revision.Title : page.Title,
                    RawMarkdown = revision.RawMarkdown,
                    DraftVersion = page.DraftVersion + 1,
                    Version = page.Version + 1,
                    UpdatedAtUtc = now,
                }, page.Version, inner) ?? throw Conflict();
                await RecordRevisionAsync(session, applied, now, forceNew: true, inner);
                return new PageMutationResult(applied, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Creates the task and its backlink in one transaction. The page text is never changed.
    /// When the editor's revision is current the offsets must select the quote exactly; when the
    /// text moved on, the quote is found again or the command is refused.
    /// </summary>
    public async Task<MakeTaskFromPageResult> MakeTaskAsync(MakeTaskFromPageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var draft = CaptureCommands.Normalize(command.Draft, "draft");
        var normalized = command with { Draft = draft };
        if (string.IsNullOrWhiteSpace(normalized.Quote))
        {
            throw AppException.Validation("quote", "Select some text on the page first.");
        }

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "page.make-task", normalized, now, async inner =>
            {
                var page = await session.Pages.GetAsync(normalized.PageId, inner) ?? throw NotFound();
                var (start, end) = AnchorForNewTask(page, normalized);
                var task = await CaptureCommands.CreateInSessionAsync(session, ids, profile, draft, normalized.OperationId, now, ["Page"], inner);
                var text = page.RawMarkdown;
                var link = new PageTaskLinkRecord(page.Id, task.Id, page.DraftVersion, start, end, text[start..end], now,
                    ContextBefore: text[Math.Max(0, start - ContextLength)..start], ContextAfter: text[end..Math.Min(text.Length, end + ContextLength)]);
                await session.Pages.AddTaskLinkAsync(link, inner);
                return new MakeTaskFromPageResult(task, link, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<PageMutationResult> AppendToDailyAsync(AppendToDailyPageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Markdown = TaskFieldValidation.Notes(command.Markdown, "markdown") };
        if (string.IsNullOrWhiteSpace(normalized.Markdown))
        {
            throw AppException.Validation("markdown", "There is nothing to add.");
        }

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "page.append", normalized, now, async inner =>
                new PageMutationResult(await AppendDailyInSessionAsync(session, normalized.Date, normalized.Markdown, now, inner), Replayed: false), token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Makes a page from a pad note: its first line is the title unless one is given; the note is deleted (restorable) in the same change.</summary>
    public async Task<PageMutationResult> MovePadNoteAsync(MovePadNoteToPageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { ParentPageId = string.IsNullOrWhiteSpace(command.ParentPageId) ? null : command.ParentPageId, Title = string.IsNullOrWhiteSpace(command.Title) ? null : command.Title.Trim() };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "page.from-pad", normalized, now, async inner =>
            {
                var note = await session.Pad.GetAsync(normalized.PadItemId, inner) ?? throw AppException.NotFound("That pad note no longer exists. Refresh and try again.");
                if (note.Version != normalized.ExpectedVersion)
                {
                    throw AppException.VersionConflict("This note changed while you were working. Refresh and try again.");
                }

                var (_, space) = await PersonalSpaceAsync(session, inner);
                await RequireParentAsync(session, space.Id, normalized.ParentPageId, null, inner);
                var (title, body) = TitleAndBody(note.RawMarkdown, normalized.Title);
                var page = await AddPageAsync(session, space.Id, normalized.ParentPageId, title, body, null, now, inner);
                _ = await session.Pad.UpdateAsync(note with { DeletedAtUtc = now, Version = note.Version + 1, UpdatedAtUtc = now }, note.Version, inner)
                    ?? throw AppException.VersionConflict("This note changed while you were working. Refresh and try again.");
                return new PageMutationResult(page, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Adds a pad note's text to a daily page and archives the note, as one undoable change.</summary>
    public async Task<PageMutationResult> AppendPadNoteAsync(AppendPadNoteToDailyCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "page.append-pad", command, now, async inner =>
            {
                var note = await session.Pad.GetAsync(command.PadItemId, inner) ?? throw AppException.NotFound("That pad note no longer exists. Refresh and try again.");
                if (note.Version != command.ExpectedVersion)
                {
                    throw AppException.VersionConflict("This note changed while you were working. Refresh and try again.");
                }

                if (string.IsNullOrWhiteSpace(note.RawMarkdown))
                {
                    throw AppException.Validation("padItemId", "This note is empty, so there is nothing to add.");
                }

                var page = await AppendDailyInSessionAsync(session, command.Date, note.RawMarkdown, now, inner);
                if (note.ArchivedAtUtc is null)
                {
                    _ = await session.Pad.UpdateAsync(note with { ArchivedAtUtc = now, Version = note.Version + 1, UpdatedAtUtc = now }, note.Version, inner)
                        ?? throw AppException.VersionConflict("This note changed while you were working. Refresh and try again.");
                }

                return new PageMutationResult(page, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>The first line of a note as a title (without Markdown markers) and the rest as the page text.</summary>
    public static (string Title, string Body) TitleAndBody(string markdown, string? title)
    {
        if (title is not null)
        {
            return (Title(title), markdown);
        }

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            var heading = HeadingMarker().Match(lines[index]);
            if (heading.Success)
            {
                // A heading names the page and leaves the text; any other first line stays in it.
                var rest = string.Join('\n', lines.Skip(index + 1)).TrimStart('\n');
                return (Title(Clip(heading.Groups[1].Value)), rest);
            }

            return (Title(Clip(PadExcerpt.Of(lines[index]) ?? "Untitled")), text);
        }

        return ("Untitled", text);
    }

    private static string Clip(string value) => value.Length > MaxTitleLength ? value[..MaxTitleLength].TrimEnd() : value.TrimEnd('…');

    private async Task<PageRecord> AppendDailyInSessionAsync(IDataSession session, DateOnly date, string markdown, DateTimeOffset now, CancellationToken token)
    {
        var (_, space) = await PersonalSpaceAsync(session, token);
        var addition = markdown.Trim('\n', '\r');
        if (await session.Pages.FindDailyAsync(space.Id, date, token) is not { } page)
        {
            return await AddPageAsync(session, space.Id, null, StorageFormats.Date(date), addition + "\n", date, now, token);
        }

        var current = page.RawMarkdown.TrimEnd('\n', '\r');
        var combined = TaskFieldValidation.Notes(current.Length == 0 ? addition + "\n" : $"{current}\n\n{addition}\n", "markdown");
        var applied = await session.Pages.UpdateAsync(page with { RawMarkdown = combined, DraftVersion = page.DraftVersion + 1, Version = page.Version + 1, UpdatedAtUtc = now }, page.Version, token)
            ?? throw Conflict();
        await RecordRevisionAsync(session, applied, now, forceNew: false, token);
        return applied;
    }

    private async Task<PageRecord> AddPageAsync(IDataSession session, string spaceId, string? parentId, string title, string markdown, DateOnly? daily, DateTimeOffset now, CancellationToken token)
    {
        var page = new PageRecord
        {
            // §7.5: a daily page's id is its natural key (one page per space and date); ordinary
            // pages have no natural key and keep random ids.
            Id = daily is { } date ? StorageFormats.Id(NameBasedId.Create(NameBasedId.Names.DailyPage(spaceId, date))) : ids.NewId().ToString(),
            SpaceId = spaceId,
            ParentPageId = parentId,
            Title = title,
            RawMarkdown = markdown,
            Position = daily is null ? await session.Pages.GetNextPositionAsync(spaceId, parentId, token) : 0,
            DailyDate = daily,
            DraftVersion = 0,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await session.Pages.AddAsync(page, token);
        await RecordRevisionAsync(session, page, now, forceNew: true, token);
        return page;
    }

    /// <summary>
    /// Keeps the version trail current: the newest version follows the text while writing goes
    /// on (a gap under <see cref="RevisionGap"/>, within <see cref="RevisionSpan"/>), otherwise
    /// a new version starts. An unchanged text never adds a version.
    /// </summary>
    private static async Task RecordRevisionAsync(IDataSession session, PageRecord page, DateTimeOffset now, bool forceNew, CancellationToken token)
    {
        var latest = (await session.Pages.ListRevisionsAsync(page.Id, token)).FirstOrDefault();
        if (latest is not null && latest.Title == page.Title && latest.RawMarkdown == page.RawMarkdown)
        {
            return;
        }

        if (!forceNew && latest is not null && now - latest.UpdatedAtUtc < RevisionGap && now - latest.CreatedAtUtc < RevisionSpan)
        {
            await session.Pages.UpdateRevisionAsync(latest with { Title = page.Title, RawMarkdown = page.RawMarkdown, UpdatedAtUtc = now }, token);
            return;
        }

        await session.Pages.AddRevisionAsync(new PageRevisionRecord(page.Id, (latest?.Number ?? 0) + 1, page.Title, page.RawMarkdown, now, now), token);
    }

    private static (int Start, int End) AnchorForNewTask(PageRecord page, MakeTaskFromPageCommand command)
    {
        var text = page.RawMarkdown;
        if (command.SourceRevision == page.DraftVersion)
        {
            if (command.Start < 0 || command.End > text.Length || command.Start >= command.End)
            {
                throw AppException.Validation("start", "The selection is outside the page. Select the text again.");
            }

            if (string.CompareOrdinal(text, command.Start, command.Quote, 0, command.Quote.Length) != 0 || command.End - command.Start != command.Quote.Length)
            {
                throw AppException.Validation("quote", "The selected text does not match the saved page. Select the text again.");
            }

            return (command.Start, command.End);
        }

        return PadCommands.Locate(text, page.DraftVersion, new ScratchTaskLinkRecord(page.Id, string.Empty, command.SourceRevision, command.Start, command.End, command.Quote, default))
            ?? throw AppException.Validation("quote", "The selected text changed before the task was made. Select it again.");
    }

    private static ScratchTaskLinkRecord AsPadLink(PageTaskLinkRecord link) =>
        new(link.PageId, link.TaskId, link.SourceRevision, link.StartIndex, link.EndIndex, link.Quote, link.CreatedAtUtc, link.ContextBefore, link.ContextAfter);

    /// <summary>The page and every page under it (active or deleted alike, from the given rows), parents first.</summary>
    private static List<PageRecord> Subtree(IReadOnlyList<PageRecord> rows, PageRecord root)
    {
        var result = new List<PageRecord> { root };
        for (var index = 0; index < result.Count; index++)
        {
            var parentId = result[index].Id;
            result.AddRange(rows.Where(row => row.ParentPageId == parentId && result.All(found => found.Id != row.Id)));
        }

        return result;
    }

    private static async Task RequireParentAsync(IDataSession session, string spaceId, string? parentId, string? movingId, CancellationToken token)
    {
        if (parentId is null)
        {
            return;
        }

        var parent = await session.Pages.GetAsync(parentId, token) ?? throw AppException.Validation("parentPageId", "The page to put it under no longer exists.");
        if (parent.SpaceId != spaceId)
        {
            throw AppException.Validation("parentPageId", "A page can only sit under a page of the same space.");
        }

        if (parent.DailyDate is not null)
        {
            throw AppException.Validation("parentPageId", "Pages cannot sit under a daily page.");
        }

        for (var current = parent; current is not null;)
        {
            if (current.Id == movingId)
            {
                throw AppException.Validation("parentPageId", "A page cannot move under itself or one of its own pages.");
            }

            current = current.ParentPageId is { } next ? await session.Pages.GetAsync(next, token) : null;
        }
    }

    private static async Task<PageView> ViewAsync(IDataSession session, PageRecord page, CancellationToken token)
    {
        var tasks = new List<PageTaskLink>();
        foreach (var link in await session.Pages.ListTaskLinksAsync(page.Id, token))
        {
            if (await session.Tasks.GetIncludingDeletedAsync(link.TaskId, token) is { } task)
            {
                tasks.Add(new PageTaskLink(link, task.Title, task.DeletedAtUtc is not null));
            }
        }

        var frontMatter = PageFrontMatter.Parse(page.RawMarkdown);
        var needles = new List<string> { $"chaos:page/{page.Id}" };
        foreach (var name in frontMatter.Aliases.Prepend(page.Title).Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            needles.Add($"[[{name}]]");
            needles.Add($"[[{name}|");
        }

        var backlinks = new List<PageBacklink>();
        foreach (var source in await session.Pages.ListContainingAsync(page.SpaceId, needles, token))
        {
            if (source.Id != page.Id)
            {
                backlinks.Add(new PageBacklink("page", source.Id, source.Title, LineContaining(source.RawMarkdown, needles)));
            }
        }

        foreach (var (id, title, notes) in await session.Pages.ListTasksContainingAsync(needles, token))
        {
            backlinks.Add(new PageBacklink("task", id, title, LineContaining(notes, needles)));
        }

        return new PageView(page, frontMatter.Icon, await session.Pages.ListTagIdsAsync(page.Id, token), tasks, backlinks);
    }

    /// <summary>The trimmed line holding the first of the strings (case-insensitive), clipped for display.</summary>
    public static string LineContaining(string text, IReadOnlyList<string> needles)
    {
        foreach (var line in text.Split('\n'))
        {
            if (needles.Any(needle => line.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                var trimmed = line.Trim();
                return trimmed.Length > 240 ? string.Concat(trimmed.AsSpan(0, 239), "…") : trimmed;
            }
        }

        return string.Empty;
    }

    private static PageSummary Summary(PageRecord page)
    {
        var frontMatter = PageFrontMatter.Parse(page.RawMarkdown);
        return new PageSummary(page.Id, page.ParentPageId, page.Title, page.Position, page.DailyDate, frontMatter.Icon, frontMatter.Aliases,
            page.Version, page.UpdatedAtUtc, page.DeletedAtUtc is not null, WordCount(PageFrontMatter.Body(page.RawMarkdown)));
    }

    /// <summary>Words in the text: runs of letters or digits (each CJK character counting as one is out of scope).</summary>
    public static int WordCount(string text) => WordPattern().Count(text);

    private static string Title(string? raw)
    {
        var title = (raw ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw AppException.Validation("title", "Give the page a title.");
        }

        if (title.Length > MaxTitleLength)
        {
            throw AppException.Validation("title", $"A page title can be at most {MaxTitleLength} characters.");
        }

        if (title.Contains('\n') || title.Contains('\r'))
        {
            throw AppException.Validation("title", "A page title is one line.");
        }

        return title;
    }

    private static async Task<(ProfileRecord Profile, SpaceRecord Space)> PersonalSpaceAsync(IDataSession session, CancellationToken token)
    {
        var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw NoProfile();
        var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet. Restart Chaos to complete first-run setup.");
        return (profile, space);
    }

    private static AppException NoProfile() =>
        new(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");

    private static void RequireVersion(PageRecord page, long expectedVersion)
    {
        if (page.Version != expectedVersion)
        {
            throw Conflict();
        }
    }

    private static AppException NotFound() => AppException.NotFound("That page no longer exists. Refresh and try again.");

    private static AppException Conflict() => AppException.VersionConflict("This page changed while you were working. Refresh and try again.");

    private async Task<T> InWriteAsync<T>(Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body, CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*")]
    private static partial Regex WordPattern();
}
