using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tags;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tags;

public sealed partial class TagCommands
{
    public Task<IReadOnlyList<TagWithPath>> ListPersonalTagsOnListAsync(string containerId, CancellationToken ct = default) =>
        store.QueryAsync(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in await session.Tasks.ListByContainerAsync(containerId, token))
            {
                ids.UnionWith(await session.Tags.ListTaskTagIdsAsync(task.Id, token));
                foreach (var subtask in await session.Subtasks.ListByTaskAsync(task.Id, token))
                    ids.UnionWith(await session.Tags.ListSubtaskTagIdsAsync(subtask.Id, token));
            }
            return (IReadOnlyList<TagWithPath>)[.. TreeOrder(await session.Tags.ListByOwnerAsync(profile.Id, token)).Where(row => ids.Contains(row.Tag.Id))];
        }, ct);

    public Task<IReadOnlyList<TagWithPath>> ListSpaceAsync(string spaceId, CancellationToken ct = default) =>
        store.QueryAsync(async (session, token) => TreeOrder(await session.Tags.ListBySpaceAsync(spaceId, token)), ct);
    /// <summary>Counts what a merge would change, without changing anything (PLAN §6: preview affected counts).</summary>
    public async Task<TagMergePreview> PreviewMergeAsync(string sourceTagId, string targetTagId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var source = await RequireTagAsync(session, sourceTagId, token);
            var target = await RequireTagAsync(session, targetTagId, token);
            await RequireMergeableAsync(session, source, target, token);
            var counter = new MergeCounter();
            var profile = await RequireProfileAsync(session, token);
            await MergeIntoAsync(session, source, target, counter, apply: false, ownerId: source.SpaceId ?? profile.Id, now: default, token);
            return counter.ToPreview();
        }, ct);

    /// <summary>Active tags of the local profile with display paths, in tree order (parents before children).</summary>
    public async Task<IReadOnlyList<TagWithPath>> ListAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            return TreeOrder(await session.Tags.ListByOwnerAsync(profile.Id, token));
        }, ct);

    public async Task<IReadOnlyList<TagRecord>> ListDeletedAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var profile = await RequireProfileAsync(session, token);
            return await session.Tags.ListDeletedByOwnerAsync(profile.Id, token);
        }, ct);

    public async Task<IReadOnlyList<string>> ListTargetTagsAsync(TagTargetKind kind, string targetId, CancellationToken ct = default)
    {
        RequireTargetKind(kind);
        return await store.QueryAsync((session, token) => ListTargetTagIdsAsync(session, kind, targetId, token), ct);
    }

    /// <summary>
    /// Existing tags that match what the person is typing (PLAN §6: suggest existing tags so
    /// errand and errands do not both appear). Exact identity first, then prefix and near
    /// matches; nothing is created or merged here.
    /// </summary>
    public async Task<IReadOnlyList<TagWithPath>> SuggestAsync(string? text, int limit = 8, CancellationToken ct = default)
    {
        var input = (text ?? string.Empty).Trim().TrimStart('#');
        var lastSegment = input.Split('/')[^1].Trim();
        if (lastSegment.Length == 0)
        {
            return [];
        }

        var key = TagName.Normalize(lastSegment);
        var all = await ListAsync(ct);
        return [.. all
            .Where(row => TagName.IsNearMatch(key, row.Tag.NormalizedName))
            .OrderBy(row => row.Tag.NormalizedName == key ? 0 : row.Tag.NormalizedName.StartsWith(key, StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(row => row.Path, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, ignoreCase: true))
            .ThenBy(row => row.Tag.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 50))];
    }

    /// <summary>Orders a flat tag list depth-first with display paths; orphans (parent not active) are listed at the root.</summary>
    public static IReadOnlyList<TagWithPath> TreeOrder(IReadOnlyList<TagRecord> tags)
    {
        var byParent = tags.ToLookup(tag => tag.ParentTagId is { } parent && tags.Any(row => row.Id == parent) ? parent : null);
        var ordered = new List<TagWithPath>(tags.Count);
        void Walk(string? parentId, string prefix, int depth)
        {
            if (depth > TagName.MaxDepth * 2)
            {
                return; // defensive bound; cycles are rejected on write
            }

            foreach (var tag in byParent[parentId].OrderBy(row => row.Position).ThenBy(row => row.NormalizedName, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal))
            {
                var path = prefix.Length == 0 ? tag.DisplayName : $"{prefix}/{tag.DisplayName}";
                ordered.Add(new TagWithPath(tag, path));
                Walk(tag.Id, path, depth + 1);
            }
        }

        Walk(null, string.Empty, 0);
        return ordered;
    }
}
