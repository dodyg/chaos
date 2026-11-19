using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Pages;

public sealed partial class PageCommands
{
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
}
