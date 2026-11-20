using System.Collections.Concurrent;

namespace Chaos.Server.Sync;

/// <summary>
/// Tells an account's open event streams that its sequence advanced (PLAN2 §8.3 step 7). In-process
/// subscribers hear it directly; other instances hear it through PostgreSQL <c>NOTIFY</c>
/// (<see cref="SyncNotificationListener"/>), which the push transaction also sends. The event stream itself is P15.
/// </summary>
public sealed class SyncBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Action<long>>> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Action<Guid, long>>> _streamSubscribers = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Action<long>>> _membershipSubscribers = new();

    /// <summary>The accounts that have at least one open event stream in this process.</summary>
    public IReadOnlyList<Guid> SubscribedAccounts() =>
        [.. _subscribers.Keys.Concat(_streamSubscribers.Keys).Concat(_membershipSubscribers.Keys).Distinct()];

    public IDisposable Subscribe(Guid accountId, Action<long> onSequence)
    {
        var token = Guid.NewGuid();
        var account = _subscribers.GetOrAdd(accountId, _ => new());
        account[token] = onSequence;
        return new Subscription(() =>
        {
            account.TryRemove(token, out _);
            if (account.IsEmpty)
            {
                _subscribers.TryRemove(accountId, out _);
            }
        });
    }

    public IDisposable SubscribeStreams(Guid accountId, Action<Guid, long> onSequence)
    {
        var token = Guid.NewGuid();
        var account = _streamSubscribers.GetOrAdd(accountId, _ => new());
        account[token] = onSequence;
        return new Subscription(() =>
        {
            account.TryRemove(token, out _);
            if (account.IsEmpty)
            {
                _streamSubscribers.TryRemove(accountId, out _);
            }
        });
    }

    public IDisposable SubscribeMembership(Guid accountId, Action<long> onMembership)
    {
        var token = Guid.NewGuid();
        var account = _membershipSubscribers.GetOrAdd(accountId, _ => new());
        account[token] = onMembership;
        return new Subscription(() =>
        {
            account.TryRemove(token, out _);
            if (account.IsEmpty)
            {
                _membershipSubscribers.TryRemove(accountId, out _);
            }
        });
    }

    public void Publish(Guid accountId, long seq)
    {
        if (!_subscribers.TryGetValue(accountId, out var account))
        {
            return;
        }

        foreach (var callback in account.Values)
        {
            callback(seq);
        }
    }

    public void PublishStream(Guid accountId, Guid streamId, long seq)
    {
        if (streamId == accountId)
        {
            Publish(accountId, seq);
        }

        if (_streamSubscribers.TryGetValue(accountId, out var account))
        {
            foreach (var callback in account.Values)
            {
                callback(streamId, seq);
            }
        }
    }

    public void PublishMembership(Guid accountId, long version)
    {
        if (_membershipSubscribers.TryGetValue(accountId, out var account))
        {
            foreach (var callback in account.Values)
            {
                callback(version);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
