using Chaos.Application.Activity;

namespace Chaos.Infrastructure.Activity;

/// <summary>
/// The notes one write session collected from device-only writers. They live as long as the
/// session (one write transaction), so a rollback discards them with everything else.
/// </summary>
public sealed class ActivityNotes : IActivityRecorder
{
    private readonly List<(string Kind, string Key, ActivityOp Op, long? Version)> _items = [];
    private readonly List<RemoteTextChange> _remoteTextChanges = [];

    public IReadOnlyList<(string Kind, string Key, ActivityOp Op, long? Version)> Items => _items;
    public IReadOnlyList<RemoteTextChange> RemoteTextChanges => _remoteTextChanges;

    public void NoteRemoteText(RemoteTextChange change) => _remoteTextChanges.Add(change);

    public void Note(string kind, string entityKey, ActivityOp op, long? version = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(entityKey);
        _items.Add((kind, entityKey, op, version));
    }
}

/// <summary>A winning remote text change, captured before another change can overwrite its prior value.</summary>
public sealed record RemoteTextChange(string ChangeId, string Kind, string EntityId, string SpaceId, string ActorId, string? Before, string After)
{
    /// <summary>For a delegation or nudge change (Q13): the parties, and the task the notice opens.</summary>
    public string? FromMemberId { get; init; }
    public string? ToMemberId { get; init; }
    public string? TaskId { get; init; }
}
