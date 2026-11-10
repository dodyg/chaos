using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tags;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tags;

public sealed record CreateTagCommand(string OperationId, string Path, string? ParentTagId, string? ColorKey);
public sealed record RenameTagCommand(string OperationId, string TagId, long ExpectedVersion, string Name);
public sealed record SetTagColorCommand(string OperationId, string TagId, long ExpectedVersion, string? ColorKey);
public sealed record MoveTagCommand(string OperationId, string TagId, long ExpectedVersion, string? ParentTagId);
public sealed record MergeTagCommand(string OperationId, string SourceTagId, long SourceVersion, string TargetTagId, long TargetVersion);
public sealed record DeleteTagCommand(string OperationId, string TagId, long ExpectedVersion);
public sealed record RestoreTagCommand(string OperationId, string TagId, long ExpectedVersion);

/// <summary>What a tag is attached to.</summary>
public enum TagTargetKind
{
    Task = 0,
    Subtask = 1,
    Pad = 2,
}

/// <summary>Attach a tag by ID, or by a path such as home/garden that selects or creates nested segments.</summary>
public sealed record LinkTagCommand(string OperationId, TagTargetKind TargetKind, string TargetId, string? TagId, string? Path);
public sealed record UnlinkTagCommand(string OperationId, TagTargetKind TargetKind, string TargetId, string TagId);

/// <summary>Everything a tag deletion changed, kept in the operation receipt so restore is exact.</summary>
public sealed record TagDeletionRecovery(
    IReadOnlyList<TagRecord> TagsBefore,
    IReadOnlyList<TagRecord> TagsAfter,
    IReadOnlyList<TagLink> TaskLinks,
    IReadOnlyList<TagLink> SubtaskLinks,
    IReadOnlyList<TagLink>? PadLinks = null);

public sealed record TagMutationResult(TagRecord Tag, bool Replayed, IReadOnlyList<TagRecord> Created, TagDeletionRecovery? Recovery = null);
public sealed record TagLinkResult(string? TagId, IReadOnlyList<string> TagIds, bool Changed, bool Replayed, IReadOnlyList<TagRecord> Created);

/// <summary>
/// The effect of merging one tag into another (PLAN §6): links that move, links already
/// present on the target (deduplicated), children that move under the target, and children
/// whose name collides with a target child and therefore merge recursively.
/// </summary>
public sealed record TagMergePreview(
    long TaskLinksMoved,
    long TaskLinksAlreadyPresent,
    long SubtaskLinksMoved,
    long SubtaskLinksAlreadyPresent,
    long ChildTagsMoved,
    long ChildTagsMerged,
    long PadLinksMoved = 0,
    long PadLinksAlreadyPresent = 0);

public sealed record TagMergeResult(TagRecord Target, TagMergePreview Applied, bool Replayed);

/// <summary>A tag with its display path, such as home/garden.</summary>
public sealed record TagWithPath(TagRecord Tag, string Path);

/// <summary>
/// Tag tree, management and tagging (PLAN §6 Tags, §15 WP3): nested tags by adjacency,
/// sibling-unique identity keys, cycle-safe moves, atomic recursive merge with
/// deduplication, reversible deletion that removes links but never tasks, and task/subtask
/// tagging with path creation. Every mutation carries an OperationId and replays like the
/// task commands. Full per-task history and undo for tag changes arrive with package 4.
/// </summary>
public sealed class TagCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    private const string Conflict = "This tag changed elsewhere while you were working. Refresh and try again.";

    public async Task<TagMutationResult> CreateAsync(CreateTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var segments = Validate(() => TagName.Path(command.Path), "path");
        var color = Validate(() => TagColors.Normalize(command.ColorKey), "colorKey");

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "tag.create", command, now, async inner =>
                {
                    TagRecord? parent = null;
                    if (command.ParentTagId is { } parentId)
                    {
                        parent = await RequireTagAsync(session, parentId, inner);
                    }

                    var existingLeaf = await FindPathAsync(session, profile.Id, parent?.Id, segments, inner);
                    if (existingLeaf is not null)
                    {
                        throw AppException.Validation("path", $"A tag named \"{existingLeaf.DisplayName}\" already exists here. Pick it instead of creating another.");
                    }

                    await RequireDepthAsync(session, parent, segments.Count, inner);
                    var (leaf, created) = await EnsurePathAsync(session, profile.Id, parent?.Id, segments, now, inner);
                    if (color is not null && leaf.ColorKey != color)
                    {
                        leaf = await ApplyUpdateAsync(session, leaf with { ColorKey = color, Version = leaf.Version + 1, UpdatedAtUtc = now }, leaf.Version, inner);
                        created = [.. created.Select(row => row.Id == leaf.Id ? leaf : row)];
                    }

                    return new TagMutationResult(leaf, Replayed: false, created);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<TagMutationResult> RenameAsync(RenameTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var segment = Validate(() => TagName.Segment(command.Name), "name");

        return await MutateTagAsync(command.OperationId, "tag.rename", command, command.TagId, command.ExpectedVersion, async (session, profile, tag, now, inner) =>
        {
            await RequireFreeSiblingNameAsync(session, profile.Id, tag.ParentTagId, segment.NormalizedName, tag.Id, inner);
            // Identity changes in place: links keep pointing at the same tag ID (PLAN §6).
            return tag.DisplayName == segment.DisplayName && tag.NormalizedName == segment.NormalizedName
                ? tag
                : tag with { DisplayName = segment.DisplayName, NormalizedName = segment.NormalizedName, Version = tag.Version + 1, UpdatedAtUtc = now };
        }, ct);
    }

    public async Task<TagMutationResult> SetColorAsync(SetTagColorCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var color = Validate(() => TagColors.Normalize(command.ColorKey), "colorKey");

        return await MutateTagAsync(command.OperationId, "tag.set-color", command, command.TagId, command.ExpectedVersion, (_, _, tag, now, _) =>
            Task.FromResult(tag.ColorKey == color ? tag : tag with { ColorKey = color, Version = tag.Version + 1, UpdatedAtUtc = now }), ct);
    }

    /// <summary>Moves a tag (with its subtree) under another tag or to the root; cycles and too-deep trees are rejected.</summary>
    public async Task<TagMutationResult> MoveAsync(MoveTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await MutateTagAsync(command.OperationId, "tag.move", command, command.TagId, command.ExpectedVersion, async (session, profile, tag, now, inner) =>
        {
            if (tag.ParentTagId == command.ParentTagId)
            {
                return tag;
            }

            TagRecord? parent = null;
            if (command.ParentTagId is { } parentId)
            {
                parent = await RequireTagAsync(session, parentId, inner);
                var subtree = await ListSubtreeAsync(session, tag, inner);
                if (subtree.Any(row => row.Id == parent.Id))
                {
                    throw AppException.Validation("parentTagId", "A tag cannot move inside itself or one of its own nested tags.");
                }

                var height = await SubtreeHeightAsync(session, tag, inner);
                await RequireDepthAsync(session, parent, height, inner);
            }

            await RequireFreeSiblingNameAsync(session, profile.Id, parent?.Id, tag.NormalizedName, tag.Id, inner);
            return tag with
            {
                ParentTagId = parent?.Id,
                Position = await session.Tags.GetNextPositionAsync(profile.Id, parent?.Id, inner),
                Version = tag.Version + 1,
                UpdatedAtUtc = now,
            };
        }, ct);
    }

    /// <summary>Counts what a merge would change, without changing anything (PLAN §6: preview affected counts).</summary>
    public async Task<TagMergePreview> PreviewMergeAsync(string sourceTagId, string targetTagId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var source = await RequireTagAsync(session, sourceTagId, token);
            var target = await RequireTagAsync(session, targetTagId, token);
            await RequireMergeableAsync(session, source, target, token);
            var counter = new MergeCounter();
            await MergeIntoAsync(session, source, target, counter, apply: false, ownerId: source.OwnerProfileId, now: default, token);
            return counter.ToPreview();
        }, ct);

    /// <summary>
    /// Reassigns every task and subtask link from source to target with deduplication, moves
    /// source children under the target, recursively merges children whose names collide,
    /// then tombstones the source tags — all in one transaction (PLAN §6).
    /// </summary>
    public async Task<TagMergeResult> MergeAsync(MergeTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "tag.merge", command, now, async inner =>
                {
                    var source = await RequireTagAsync(session, command.SourceTagId, inner);
                    var target = await RequireTagAsync(session, command.TargetTagId, inner);
                    RequireCurrentVersion(source, command.SourceVersion);
                    RequireCurrentVersion(target, command.TargetVersion);
                    await RequireMergeableAsync(session, source, target, inner);

                    var counter = new MergeCounter();
                    await MergeIntoAsync(session, source, target, counter, apply: true, profile.Id, now, inner);
                    var current = await session.Tags.GetAsync(target.Id, inner) ?? target;
                    return new TagMergeResult(current, counter.ToPreview(), Replayed: false);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Tombstones the tag and its nested tags and removes their links; tasks are never deleted (PLAN §6).</summary>
    public async Task<TagMutationResult> DeleteAsync(DeleteTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "tag.delete", command, now, async inner =>
                {
                    var tag = await RequireTagAsync(session, command.TagId, inner);
                    RequireCurrentVersion(tag, command.ExpectedVersion);

                    var before = new List<TagRecord>();
                    var after = new List<TagRecord>();
                    var taskLinks = new List<TagLink>();
                    var subtaskLinks = new List<TagLink>();
                    var padLinks = new List<TagLink>();
                    foreach (var row in await ListSubtreeAsync(session, tag, inner))
                    {
                        foreach (var taskId in await session.Tags.ListTaskIdsAsync(row.Id, inner))
                        {
                            await session.Tags.RemoveTaskLinkAsync(taskId, row.Id, inner);
                            taskLinks.Add(new TagLink(taskId, row.Id));
                        }

                        foreach (var subtaskId in await session.Tags.ListSubtaskIdsAsync(row.Id, inner))
                        {
                            await session.Tags.RemoveSubtaskLinkAsync(subtaskId, row.Id, inner);
                            subtaskLinks.Add(new TagLink(subtaskId, row.Id));
                        }

                        foreach (var padItemId in await session.Tags.ListPadItemIdsAsync(row.Id, inner))
                        {
                            await session.Tags.RemovePadLinkAsync(padItemId, row.Id, inner);
                            padLinks.Add(new TagLink(padItemId, row.Id));
                        }

                        before.Add(row);
                        after.Add(await ApplyUpdateAsync(session, row with { DeletedAtUtc = now, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner));
                    }

                    return new TagMutationResult(after[0], Replayed: false, [], new TagDeletionRecovery(before, after, taskLinks, subtaskLinks, padLinks));
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Restores a deleted tag subtree and the links its deletion removed. Refuses atomically
    /// when a restored tag changed afterwards, its parent is still deleted, or an active
    /// sibling now uses the same name.
    /// </summary>
    public async Task<TagMutationResult> RestoreAsync(RestoreTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "tag.restore", command, now, async inner =>
                {
                    var tag = await session.Tags.GetIncludingDeletedAsync(command.TagId, inner)
                        ?? throw AppException.NotFound("That tag no longer exists.");
                    RequireCurrentVersion(tag, command.ExpectedVersion);
                    if (tag.DeletedAtUtc is null)
                    {
                        return new TagMutationResult(tag, Replayed: false, []);
                    }

                    var receipts = await session.Receipts.ListByKindAsync(profile.Id, "tag.delete", inner);
                    var recovery = receipts
                        .Select(receipt => StoredJsonFormats.ReadSnapshot<StoredResultEnvelope<TagMutationResult>>(receipt.ResultJson)?.Result)
                        .FirstOrDefault(stored => stored?.Tag.Id == tag.Id && stored.Tag.Version == tag.Version)?.Recovery
                        ?? throw AppException.Validation("tagId", "This tag was removed as part of another change. Restore the tag that was deleted, or restore a backup.");

                    if (tag.ParentTagId is { } parentId && await session.Tags.GetAsync(parentId, inner) is null)
                    {
                        throw AppException.Validation("tagId", "Restore the parent tag first, then this one.");
                    }

                    await RequireFreeSiblingNameAsync(session, profile.Id, tag.ParentTagId, tag.NormalizedName, tag.Id, inner);
                    foreach (var deleted in recovery.TagsAfter)
                    {
                        var current = await session.Tags.GetIncludingDeletedAsync(deleted.Id, inner)
                            ?? throw AppException.VersionConflict("A nested tag changed after deletion. Refresh before restoring.");
                        RequireCurrentVersion(current, deleted.Version);
                    }

                    TagRecord restoredRoot = tag;
                    foreach (var (beforeRow, afterRow) in recovery.TagsBefore.Zip(recovery.TagsAfter))
                    {
                        var restored = await ApplyUpdateAsync(session, beforeRow with { DeletedAtUtc = null, Version = afterRow.Version + 1, UpdatedAtUtc = now }, afterRow.Version, inner);
                        if (restored.Id == tag.Id)
                        {
                            restoredRoot = restored;
                        }
                    }

                    foreach (var link in recovery.TaskLinks)
                    {
                        if (await session.Tasks.GetIncludingDeletedAsync(link.TargetId, inner) is not null)
                        {
                            await session.Tags.AddTaskLinkAsync(link.TargetId, link.TagId, inner);
                        }
                    }

                    foreach (var link in recovery.SubtaskLinks)
                    {
                        if (await session.Subtasks.GetIncludingDeletedAsync(link.TargetId, inner) is not null)
                        {
                            await session.Tags.AddSubtaskLinkAsync(link.TargetId, link.TagId, inner);
                        }
                    }

                    foreach (var link in recovery.PadLinks ?? [])
                    {
                        if (await session.Pad.GetIncludingDeletedAsync(link.TargetId, inner) is not null)
                        {
                            await session.Tags.AddPadLinkAsync(link.TargetId, link.TagId, inner);
                        }
                    }

                    return new TagMutationResult(restoredRoot, Replayed: false, []);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Tags a task or subtask; a path selects or creates nested segments. Tagging never moves the task (PLAN §6 drag-to-tag).</summary>
    public async Task<TagLinkResult> LinkAsync(LinkTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireTargetKind(command.TargetKind);
        if (command.TagId is null == command.Path is null)
        {
            throw AppException.Validation("tagId", "Choose an existing tag or type a tag name.");
        }

        var segments = command.Path is null ? null : Validate(() => TagName.Path(command.Path), "path");

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "tag.link", command, now, async inner =>
                {
                    await RequireTargetAsync(session, command.TargetKind, command.TargetId, inner);
                    TagRecord tag;
                    IReadOnlyList<TagRecord> created = [];
                    if (segments is null)
                    {
                        tag = await RequireTagAsync(session, command.TagId!, inner);
                    }
                    else
                    {
                        await RequireDepthAsync(session, null, segments.Count, inner);
                        (tag, created) = await EnsurePathAsync(session, profile.Id, null, segments, now, inner);
                    }

                    var changed = command.TargetKind switch
                    {
                        TagTargetKind.Task => await session.Tags.AddTaskLinkAsync(command.TargetId, tag.Id, inner),
                        TagTargetKind.Subtask => await session.Tags.AddSubtaskLinkAsync(command.TargetId, tag.Id, inner),
                        _ => await session.Tags.AddPadLinkAsync(command.TargetId, tag.Id, inner),
                    };
                    return new TagLinkResult(tag.Id, await ListTargetTagIdsAsync(session, command.TargetKind, command.TargetId, inner), changed, Replayed: false, created);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<TagLinkResult> UnlinkAsync(UnlinkTagCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireTargetKind(command.TargetKind);

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "tag.unlink", command, now, async inner =>
                {
                    await RequireTargetAsync(session, command.TargetKind, command.TargetId, inner);
                    var changed = command.TargetKind switch
                    {
                        TagTargetKind.Task => await session.Tags.RemoveTaskLinkAsync(command.TargetId, command.TagId, inner),
                        TagTargetKind.Subtask => await session.Tags.RemoveSubtaskLinkAsync(command.TargetId, command.TagId, inner),
                        _ => await session.Tags.RemovePadLinkAsync(command.TargetId, command.TagId, inner),
                    };
                    return new TagLinkResult(command.TagId, await ListTargetTagIdsAsync(session, command.TargetKind, command.TargetId, inner), changed, Replayed: false, []);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

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

    private async Task<TagMutationResult> MutateTagAsync<TCommand>(
        string operationId,
        string kind,
        TCommand command,
        string tagId,
        long expectedVersion,
        Func<IDataSession, ProfileRecord, TagRecord, DateTimeOffset, CancellationToken, Task<TagRecord>> change,
        CancellationToken ct) where TCommand : notnull =>
        await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, operationId, kind, command, now, async inner =>
                {
                    var tag = await RequireTagAsync(session, tagId, inner);
                    RequireCurrentVersion(tag, expectedVersion);
                    var updated = await change(session, profile, tag, now, inner);
                    var applied = ReferenceEquals(updated, tag) ? tag : await ApplyUpdateAsync(session, updated, tag.Version, inner);
                    return new TagMutationResult(applied, Replayed: false, []);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);

    private sealed class MergeCounter
    {
        public long TaskMoved, TaskPresent, SubtaskMoved, SubtaskPresent, ChildMoved, ChildMerged, PadMoved, PadPresent;

        public TagMergePreview ToPreview() => new(TaskMoved, TaskPresent, SubtaskMoved, SubtaskPresent, ChildMoved, ChildMerged, PadMoved, PadPresent);
    }

    private static async Task MergeIntoAsync(
        IDataSession session,
        TagRecord source,
        TagRecord target,
        MergeCounter counter,
        bool apply,
        string ownerId,
        DateTimeOffset now,
        CancellationToken token)
    {
        var targetTasks = (await session.Tags.ListTaskIdsAsync(target.Id, token)).ToHashSet(StringComparer.Ordinal);
        foreach (var taskId in await session.Tags.ListTaskIdsAsync(source.Id, token))
        {
            if (targetTasks.Contains(taskId)) counter.TaskPresent++; else counter.TaskMoved++;
            if (apply)
            {
                await session.Tags.RemoveTaskLinkAsync(taskId, source.Id, token);
                await session.Tags.AddTaskLinkAsync(taskId, target.Id, token);
            }
        }

        var targetSubtasks = (await session.Tags.ListSubtaskIdsAsync(target.Id, token)).ToHashSet(StringComparer.Ordinal);
        foreach (var subtaskId in await session.Tags.ListSubtaskIdsAsync(source.Id, token))
        {
            if (targetSubtasks.Contains(subtaskId)) counter.SubtaskPresent++; else counter.SubtaskMoved++;
            if (apply)
            {
                await session.Tags.RemoveSubtaskLinkAsync(subtaskId, source.Id, token);
                await session.Tags.AddSubtaskLinkAsync(subtaskId, target.Id, token);
            }
        }

        var targetPads = (await session.Tags.ListPadItemIdsAsync(target.Id, token)).ToHashSet(StringComparer.Ordinal);
        foreach (var padItemId in await session.Tags.ListPadItemIdsAsync(source.Id, token))
        {
            if (targetPads.Contains(padItemId)) counter.PadPresent++; else counter.PadMoved++;
            if (apply)
            {
                await session.Tags.RemovePadLinkAsync(padItemId, source.Id, token);
                await session.Tags.AddPadLinkAsync(padItemId, target.Id, token);
            }
        }

        foreach (var child in await session.Tags.ListChildrenAsync(source.Id, token))
        {
            var collision = await session.Tags.FindChildAsync(ownerId, target.Id, child.NormalizedName, token);
            if (collision is not null)
            {
                counter.ChildMerged++;
                await MergeIntoAsync(session, child, collision, counter, apply, ownerId, now, token);
            }
            else
            {
                counter.ChildMoved++;
                if (apply)
                {
                    var position = await session.Tags.GetNextPositionAsync(ownerId, target.Id, token);
                    await ApplyUpdateAsync(session, child with { ParentTagId = target.Id, Position = position, Version = child.Version + 1, UpdatedAtUtc = now }, child.Version, token);
                }
            }
        }

        if (apply)
        {
            var current = await session.Tags.GetAsync(source.Id, token) ?? throw AppException.VersionConflict(Conflict);
            await ApplyUpdateAsync(session, current with { DeletedAtUtc = now, Version = current.Version + 1, UpdatedAtUtc = now }, current.Version, token);
        }
    }

    private static async Task RequireMergeableAsync(IDataSession session, TagRecord source, TagRecord target, CancellationToken token)
    {
        if (source.Id == target.Id)
        {
            throw AppException.Validation("targetTagId", "Choose a different tag to merge into.");
        }

        if ((await ListSubtreeAsync(session, source, token)).Any(row => row.Id == target.Id))
        {
            throw AppException.Validation("targetTagId", "A tag cannot be merged into one of its own nested tags.");
        }
    }

    /// <summary>The tag and every active descendant, parents before children (bounded by the depth limit).</summary>
    private static async Task<IReadOnlyList<TagRecord>> ListSubtreeAsync(IDataSession session, TagRecord root, CancellationToken token)
    {
        var result = new List<TagRecord> { root };
        var frontier = new List<TagRecord> { root };
        for (var depth = 0; depth < TagName.MaxDepth * 2 && frontier.Count > 0; depth++)
        {
            var next = new List<TagRecord>();
            foreach (var tag in frontier)
            {
                next.AddRange(await session.Tags.ListChildrenAsync(tag.Id, token));
            }

            result.AddRange(next);
            frontier = next;
        }

        return result;
    }

    /// <summary>Levels in the subtree rooted at the tag, counting the tag itself as 1.</summary>
    private static async Task<int> SubtreeHeightAsync(IDataSession session, TagRecord root, CancellationToken token)
    {
        var height = 1;
        var frontier = new List<TagRecord> { root };
        while (frontier.Count > 0 && height <= TagName.MaxDepth * 2)
        {
            var next = new List<TagRecord>();
            foreach (var tag in frontier)
            {
                next.AddRange(await session.Tags.ListChildrenAsync(tag.Id, token));
            }

            if (next.Count > 0) height++;
            frontier = next;
        }

        return height;
    }

    /// <summary>Rejects a tree deeper than the limit when <paramref name="levels"/> are placed under <paramref name="parent"/>.</summary>
    private static async Task RequireDepthAsync(IDataSession session, TagRecord? parent, int levels, CancellationToken token)
    {
        var depth = 0;
        for (var current = parent; current is not null && depth <= TagName.MaxDepth; depth++)
        {
            current = current.ParentTagId is { } parentId ? await session.Tags.GetAsync(parentId, token) : null;
        }

        if (depth + levels > TagName.MaxDepth)
        {
            throw AppException.Validation("parentTagId", $"Tags can be nested at most {TagName.MaxDepth} levels deep.");
        }
    }

    private static async Task<TagRecord?> FindPathAsync(IDataSession session, string ownerId, string? parentId, IReadOnlyList<TagSegment> segments, CancellationToken token)
    {
        TagRecord? current = null;
        foreach (var segment in segments)
        {
            current = await session.Tags.FindChildAsync(ownerId, current?.Id ?? parentId, segment.NormalizedName, token);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>Selects existing segments by identity key and creates the missing ones (PLAN §6 slash-delimited tags).</summary>
    private async Task<(TagRecord Leaf, IReadOnlyList<TagRecord> Created)> EnsurePathAsync(
        IDataSession session,
        string ownerId,
        string? parentId,
        IReadOnlyList<TagSegment> segments,
        DateTimeOffset now,
        CancellationToken token)
    {
        var created = new List<TagRecord>();
        TagRecord? current = null;
        foreach (var segment in segments)
        {
            var under = current?.Id ?? parentId;
            var existing = await session.Tags.FindChildAsync(ownerId, under, segment.NormalizedName, token);
            if (existing is null)
            {
                existing = new TagRecord
                {
                    Id = ids.NewId().ToString(),
                    OwnerProfileId = ownerId,
                    DisplayName = segment.DisplayName,
                    NormalizedName = segment.NormalizedName,
                    ParentTagId = under,
                    Position = await session.Tags.GetNextPositionAsync(ownerId, under, token),
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Tags.AddAsync(existing, token);
                created.Add(existing);
            }

            current = existing;
        }

        return (current!, created);
    }

    private static async Task RequireFreeSiblingNameAsync(IDataSession session, string ownerId, string? parentId, string normalizedName, string selfId, CancellationToken token)
    {
        var existing = await session.Tags.FindChildAsync(ownerId, parentId, normalizedName, token);
        if (existing is not null && existing.Id != selfId)
        {
            throw AppException.Validation("name", $"A tag named \"{existing.DisplayName}\" already exists here. Choose another name, or merge the two tags.");
        }
    }

    private static async Task RequireTargetAsync(IDataSession session, TagTargetKind kind, string targetId, CancellationToken token)
    {
        var exists = kind switch
        {
            TagTargetKind.Task => await session.Tasks.GetAsync(targetId, token) is not null,
            TagTargetKind.Subtask => await session.Subtasks.GetAsync(targetId, token) is not null,
            _ => await session.Pad.GetAsync(targetId, token) is not null,
        };
        if (!exists)
        {
            throw AppException.NotFound(kind switch
            {
                TagTargetKind.Task => "That task no longer exists. Refresh and try again.",
                TagTargetKind.Subtask => "That subtask no longer exists. Refresh and try again.",
                _ => "That pad note no longer exists. Refresh and try again.",
            });
        }
    }

    private static async Task<IReadOnlyList<string>> ListTargetTagIdsAsync(IDataSession session, TagTargetKind kind, string targetId, CancellationToken token) =>
        kind switch
        {
            TagTargetKind.Task => await session.Tags.ListTaskTagIdsAsync(targetId, token),
            TagTargetKind.Subtask => await session.Tags.ListSubtaskTagIdsAsync(targetId, token),
            _ => await session.Tags.ListPadTagIdsAsync(targetId, token),
        };

    private static void RequireTargetKind(TagTargetKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw AppException.Validation("targetKind", "Tags can be added to tasks, subtasks and pad notes.");
        }
    }

    private static T Validate<T>(Func<T> rule, string field)
    {
        try
        {
            return rule();
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation(field, error.Message);
        }
    }

    private static async Task<TagRecord> RequireTagAsync(IDataSession session, string tagId, CancellationToken token) =>
        await session.Tags.GetAsync(tagId, token)
        ?? throw AppException.NotFound("That tag no longer exists. Refresh and try again.");

    private static void RequireCurrentVersion(TagRecord tag, long expectedVersion)
    {
        if (tag.Version != expectedVersion)
        {
            throw AppException.VersionConflict(Conflict);
        }
    }

    private static async Task<TagRecord> ApplyUpdateAsync(IDataSession session, TagRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Tags.UpdateAsync(updated, expectedVersion, token) ?? throw AppException.VersionConflict(Conflict);

    private static async Task<ProfileRecord> RequireProfileAsync(IDataSession session, CancellationToken token) =>
        await session.Profiles.GetPrimaryAsync(token)
        ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");

    private async Task<T> InWriteAsync<T>(
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body,
        CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);
}
