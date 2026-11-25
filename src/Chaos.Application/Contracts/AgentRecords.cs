namespace Chaos.Application.Contracts;

/// <summary>
/// A local agent (PLAN3 12.3): the person's own assistant working through the local MCP surface in lists the person
/// chose. Its name and pause state travel in the account stream; its tokens stay on the device.
/// </summary>
public sealed record LocalAgentRecord : AggregateRecord
{
    public required string Name { get; init; }
    public DateTimeOffset? PausedAtUtc { get; init; }
}

/// <summary>One list a local agent may work in; a revoked grant is a tombstone.</summary>
public sealed record LocalAgentAccessRecord : AggregateRecord
{
    public required string AgentId { get; init; }
    public required string ContainerId { get; init; }
    public required DateTimeOffset GrantedAtUtc { get; init; }
}

/// <summary>A local agent's token as kept: its SHA-256 only. The token itself is shown once when made (12.1).</summary>
public sealed record LocalAgentTokenRecord
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public required string Name { get; init; }
    public required string TokenHash { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public enum ProposalState
{
    Pending = 0,
    Accepted = 1,
    Dismissed = 2,
}

/// <summary>A task an agent proposed from a pad note (12.6); nothing is created until a person accepts it.</summary>
public sealed record TaskProposalRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required string AgentId { get; init; }
    public string? SourcePadItemId { get; init; }
    public required string Title { get; init; }
    public string NotesMarkdown { get; init; } = string.Empty;
    public string? QuickAddText { get; init; }
    public ProposalState State { get; init; }
    public string? CreatedTaskId { get; init; }
    public DateTimeOffset? DecidedAtUtc { get; init; }
}
