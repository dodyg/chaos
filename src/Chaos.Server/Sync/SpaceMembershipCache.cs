using System.Collections.Concurrent;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

public sealed record SpaceMembershipVersion(long Version, IReadOnlyDictionary<Guid, short> Roles);

/// <summary>In-process role snapshots, invalidated by the account membership version and PostgreSQL notifications.</summary>
public sealed class SpaceMembershipCache
{
    private readonly ConcurrentDictionary<Guid, SpaceMembershipVersion> _entries = new();

    public async Task<SpaceMembershipVersion> GetAsync(ChaosDbContext db, Guid accountId, CancellationToken ct)
    {
        var version = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.DeletedAt == null)
            .Select(a => (long?)a.MembershipVersion)
            .FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return new SpaceMembershipVersion(0, new Dictionary<Guid, short>());
        }

        if (_entries.TryGetValue(accountId, out var entry) && entry.Version == version)
        {
            return entry;
        }

        var roles = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.AccountId == accountId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null
                && db.SyncStreams.Any(s => s.Id == m.SpaceId && s.Kind == SyncStream.SpaceKind && s.DeletedAt == null))
            .ToDictionaryAsync(m => m.SpaceId, m => m.Role, ct);
        var loaded = new SpaceMembershipVersion(version.Value, roles);
        _entries[accountId] = loaded;
        return loaded;
    }

    public void Invalidate(Guid accountId) => _entries.TryRemove(accountId, out _);
}
