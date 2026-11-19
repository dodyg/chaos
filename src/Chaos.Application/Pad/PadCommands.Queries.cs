using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Pad;

public sealed partial class PadCommands
{
    public async Task<IReadOnlyList<PadItemView>> ListAsync(bool archived, string? tagId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PadItemView>>(async (session, token) =>
        {
            var (profile, space) = await PersonalSpaceAsync(session, token);
            var items = await session.Pad.ListBySpaceAsync(space.Id, archived, token);
            HashSet<string>? scope = null;
            if (!string.IsNullOrEmpty(tagId))
            {
                scope = await TagScopeAsync(session, profile.Id, tagId, token);
            }

            var views = new List<PadItemView>(items.Count);
            foreach (var item in items)
            {
                var view = await ViewAsync(session, item, token);
                if (scope is null || view.TagIds.Any(scope.Contains))
                {
                    views.Add(view);
                }
            }

            return views;
        }, ct);

    public async Task<PadItemView> GetAsync(string itemId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var item = await session.Pad.GetIncludingDeletedAsync(itemId, token) ?? throw NotFound();
            return await ViewAsync(session, item, token);
        }, ct);

    public async Task<IReadOnlyList<PadItemRecord>> ListDeletedAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            return await session.Pad.ListDeletedBySpaceAsync(space.Id, token);
        }, ct);

    /// <summary>Active items (archived included) whose typed text matches, best first.</summary>
    public async Task<IReadOnlyList<PadItemView>> SearchAsync(string text, int limit, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PadItemView>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            var views = new List<PadItemView>();
            foreach (var id in await session.Pad.SearchAsync(space.Id, text, Math.Clamp(limit, 1, 100), token))
            {
                if (await session.Pad.GetAsync(id, token) is { } item)
                {
                    views.Add(await ViewAsync(session, item, token));
                }
            }

            return views;
        }, ct);

    /// <summary>Readable labels for `[[` pad links, resolved by ID so edits keep the link working.</summary>
    public async Task<IReadOnlyList<PadLinkTarget>> ResolveLinksAsync(IReadOnlyList<string> itemIds, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PadLinkTarget>>(async (session, token) =>
        {
            var targets = new List<PadLinkTarget>();
            foreach (var id in itemIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                if (await session.Pad.GetIncludingDeletedAsync(id, token) is { } item)
                {
                    targets.Add(new PadLinkTarget(item.Id, PadExcerpt.Of(item.RawMarkdown) ?? "empty note", item.DeletedAtUtc is not null));
                }
            }

            return targets;
        }, ct);

    /// <summary>Where the pad backlink of a task points now; null when the task was not made from the pad.</summary>
    public async Task<PadAnchor?> LocateAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var link = await session.Pad.FindTaskLinkAsync(taskId, token);
            if (link is null)
            {
                return null;
            }

            var item = await session.Pad.GetIncludingDeletedAsync(link.PadItemId, token) ?? throw NotFound();
            if (link.RegionJson is not null)
            {
                // Regions are fixed coordinates in the drawing, so the spot never moves; a drawing
                // that was deleted has nothing to point at.
                var region = PadDrawingValidation.ParseRegion(link.RegionJson);
                var regionState = region is null || item.DeletedAtUtc is not null ? PadAnchorState.Missing : PadAnchorState.Exact;
                return new PadAnchor(item.Id, taskId, regionState, null, null, string.Empty, item.ArchivedAtUtc is not null, item.DeletedAtUtc is not null, region);
            }

            var found = Locate(item.RawMarkdown, item.DraftVersion, link);
            var state = found is null ? PadAnchorState.Missing
                : item.DraftVersion == link.SourceRevision && found.Value.Start == link.StartIndex ? PadAnchorState.Exact
                : PadAnchorState.Relocated;
            return new PadAnchor(item.Id, taskId, state, found?.Start, found?.End, link.Quote, item.ArchivedAtUtc is not null, item.DeletedAtUtc is not null);
        }, ct);

    public async Task<PadDrawingView> GetDrawingAsync(string itemId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var item = await session.Pad.GetIncludingDeletedAsync(itemId, token) ?? throw NotFound();
            var drawing = await session.Pad.GetDrawingAsync(itemId, token) ?? throw NotFound();
            return new PadDrawingView(item, drawing);
        }, ct);

    public async Task<PadReviewStatus> ReviewStatusAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var (profile, space) = await PersonalSpaceAsync(session, token);
            var now = clock.GetUtcNow();
            var receipts = await session.Receipts.ListByKindAsync(profile.Id, "pad.review", token);
            DateTimeOffset? last = receipts.Count == 0 ? null : receipts.Max(receipt => receipt.CommittedAtUtc);
            var suggestions = (await session.Pad.ListBySpaceAsync(space.Id, archived: false, token))
                .Where(item => now - item.UpdatedAtUtc >= ReviewAge)
                .ToList();
            var due = suggestions.Count > 0 && (last is null || now - last.Value >= ReviewAge);
            return new PadReviewStatus(due, last, suggestions);
        }, ct);
}
