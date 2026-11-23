using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tags;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tags;

public sealed record CreateTagCommand(string OperationId, string Path, string? ParentTagId, string? ColorKey, string? SpaceId = null);
public sealed record RenameTagCommand(string OperationId, string TagId, long ExpectedVersion, string Name);
public sealed record SetTagColorCommand(string OperationId, string TagId, long ExpectedVersion, string? ColorKey);
public sealed record MoveTagCommand(string OperationId, string TagId, long ExpectedVersion, string? ParentTagId);
public sealed record MergeTagCommand(string OperationId, string SourceTagId, long SourceVersion, string TargetTagId, long TargetVersion);
public sealed record DeleteTagCommand(string OperationId, string TagId, long ExpectedVersion);
public sealed record RestoreTagCommand(string OperationId, string TagId, long ExpectedVersion);
public sealed record ShareListTagsCommand(string OperationId, string ContainerId);
public sealed record ShareListTagsResult(int TagsShared, int LinksShared, bool Replayed);

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
