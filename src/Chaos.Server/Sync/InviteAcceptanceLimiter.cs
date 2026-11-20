using System.Collections.Concurrent;
using Chaos.Server.Auth;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

/// <summary>Fixed-window invite accept throttles. Every token attempt is counted, including wrong tokens.</summary>
public sealed class InviteAcceptanceLimiter(IOptions<SharingOptions> options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public bool TryAcquire(Guid accountId, string? ip)
    {
        var now = clock.GetUtcNow();
        var perAccount = Account(accountId);
        var perIp = ip is null ? true : Count("ip:" + ip, options.Value.InviteAcceptsPerIpPerHour, now);
        return perAccount && perIp;
    }

    private bool Account(Guid id) => Count("account:" + id.ToString("N"), options.Value.InviteAcceptsPerAccountPerHour, clock.GetUtcNow());

    private bool Count(string key, int limit, DateTimeOffset now)
    {
        while (true)
        {
            var current = _windows.GetOrAdd(key, _ => new Window(now, 0));
            var reset = now - current.Start >= TimeSpan.FromHours(1);
            var updated = reset ? new Window(now, 1) : current with { Count = current.Count + 1 };
            if (reset && _windows.TryUpdate(key, updated, current))
            {
                return true;
            }
            if (!reset && _windows.TryUpdate(key, updated, current))
            {
                return updated.Count <= limit;
            }
        }
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
