using System.Collections.Concurrent;

namespace Chaos.Server.Sync;

/// <summary>
/// Tells an account's open event streams that its sequence advanced (PLAN2 §8.3 step 7). In-process
/// subscribers hear it directly; other instances hear it through PostgreSQL <c>NOTIFY</c>
/// (<see cref="Channel"/>), which the push transaction also sends. The event stream itself is P15.
/// </summary>
public sealed class SyncBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Action<long>>> _subscribers = new();

    /// <summary>The PostgreSQL notification channel of one account.</summary>
    public static string Channel(Guid accountId) => $"chaos_seq_{accountId:N}";

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

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
