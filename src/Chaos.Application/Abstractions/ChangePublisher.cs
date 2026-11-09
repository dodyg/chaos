namespace Chaos.Application.Abstractions;

/// <summary>One committed change, published with IDs and versions (PLAN §4/§13).</summary>
public sealed record ChangeNotice(string EntityKind, string EntityId, long Version);

/// <summary>
/// Change events fire only after commit (callers publish after a successful ExecuteAsync);
/// a failed transaction produces neither a partial history record nor UI success.
/// </summary>
public interface IChangePublisher
{
    void Publish(ChangeNotice notice);
}

/// <summary>Collects notices during a use case, publishes them after commit.</summary>
public sealed class ChangeCollector(IChangePublisher publisher) : IChangePublisher
{
    private readonly List<ChangeNotice> _pending = [];

    public void Publish(ChangeNotice notice) => _pending.Add(notice);

    public void Flush()
    {
        foreach (var notice in _pending)
        {
            publisher.Publish(notice);
        }

        _pending.Clear();
    }
}
