namespace Chaos.Application.Contracts;

/// <summary>
/// A tag row (PLAN §5 Tags): owned by a profile, nested by adjacency through ParentTagId.
/// NormalizedName is the identity key among active siblings; DisplayName keeps the spelling.
/// </summary>
public sealed record TagRecord : AggregateRecord
{
    public required string OwnerProfileId { get; init; }
    public required string DisplayName { get; init; }
    public required string NormalizedName { get; init; }
    public string? ParentTagId { get; init; }
    public string? ColorKey { get; init; }
    public required long Position { get; init; }
}

/// <summary>One tag link to a task or a subtask (PLAN §5 TaskTags/SubtaskTags).</summary>
public sealed record TagLink(string TargetId, string TagId);
