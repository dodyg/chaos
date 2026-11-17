using Chaos.Application.Activity;

namespace Chaos.Infrastructure.Activity;

/// <summary>
/// The notes one write session collected from device-only writers. They live as long as the
/// session (one write transaction), so a rollback discards them with everything else.
/// </summary>
public sealed class ActivityNotes : IActivityRecorder
{
    private readonly List<(string Kind, string Key, ActivityOp Op, long? Version)> _items = [];

    public IReadOnlyList<(string Kind, string Key, ActivityOp Op, long? Version)> Items => _items;

    public void Note(string kind, string entityKey, ActivityOp op, long? version = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(entityKey);
        _items.Add((kind, entityKey, op, version));
    }
}
