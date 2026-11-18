using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Auth;

/// <summary>Allows each IP address a fixed number of sign-in form posts per hour (PLAN2 §8.5). In memory: one server process.</summary>
public sealed class SignInThrottle(TimeProvider clock, IOptions<SignInOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new();

    public bool TryAcquire(string? address)
    {
        var key = address ?? "unknown";
        var now = clock.GetUtcNow();
        var cutoff = now.AddHours(-1);
        var queue = _attempts.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= options.Value.MaxAttemptsPerIpPerHour)
            {
                return false;
            }

            queue.Enqueue(now);
        }

        if (_attempts.Count > 10_000)
        {
            Prune(cutoff);
        }

        return true;
    }

    private void Prune(DateTimeOffset cutoff)
    {
        foreach (var (key, queue) in _attempts)
        {
            lock (queue)
            {
                if (queue.Count == 0 || queue.All(t => t <= cutoff))
                {
                    _attempts.TryRemove(key, out _);
                }
            }
        }
    }
}
