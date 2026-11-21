using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

/// <summary>Writes server-owned sync changes with a stable server device clock and actor identity.</summary>
public sealed class ServerChangeWriter(ChaosDbContext db, TimeProvider clock, SyncKindCatalog kinds)
{
    public static readonly Guid DeviceId = new("00000000-0000-0000-0000-000000000007");

    public async Task TombstoneAssignmentsAsync(Guid spaceId, Guid memberId, Guid actorId, DateTimeOffset now, CancellationToken ct)
    {
        var suffix = "|" + memberId.ToString("D");
        var rows = await db.SyncEntities.Where(e => e.AccountId == spaceId && e.Kind == "task_assignee"
            && e.PurgedAt == null && e.MovedTo == null && e.EntityKey.EndsWith(suffix)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var groups = SyncJson.ReadGroups(row.Groups);
            if (groups.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null)
                continue;
            var hlc = await NextHlcAsync(spaceId, row.Kind, row.EntityKey, ct);
            await AppendAsync(spaceId, actorId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), row.Kind, row.EntityKey,
                new Dictionary<string, SyncGroupState> { ["deleted"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["DeletedAtUtc"] = now.UtcDateTime.ToString("o") })) }), ct);
        }
    }

    /// <summary>Creates a fresh server clock newer than every stored group of the entity.</summary>
    public async Task<Hlc> NextHlcAsync(Guid streamId, string kind, string key, CancellationToken ct)
    {
        var entity = await db.SyncEntities.FindAsync([streamId, kind, key], ct);
        var hybrid = new HybridClock(DeviceId, clock);
        if (entity is not null)
        {
            var clocks = SyncJson.ReadGroups(entity.Groups).Values.Select(group => group.Hlc).ToList();
            if (clocks.Count > 0)
            {
                hybrid.Receive(clocks.Max());
            }
        }

        return hybrid.TickLocal();
    }

    public async Task AppendAsync(Guid streamId, Guid actorId, SyncChange change, CancellationToken ct)
    {
        if (kinds.Find(change.Kind) is null)
        {
            throw new InvalidOperationException($"The server cannot write unknown sync kind {change.Kind}.");
        }

        var stream = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(db.SyncStreams.Where(s => s.Id == streamId), ct)).Single();
        var now = clock.GetUtcNow();
        var entity = await db.SyncEntities.FindAsync([streamId, change.Kind, change.EntityKey], ct);
        var stored = entity is null ? null : SyncJson.ReadGroups(entity.Groups);
        var merge = Merge.Apply(stored, change);
        if (merge.Winners.Count == 0)
        {
            return;
        }

        var seq = stream.NextSeq++;
        var groups = SyncJson.WriteGroups(merge.Merged);
        if (entity is null)
        {
            db.SyncEntities.Add(new SyncEntityRow
            {
                AccountId = streamId, Kind = change.Kind, EntityKey = change.EntityKey,
                Groups = groups, LastSeq = seq, UpdatedAt = now,
            });
        }
        else
        {
            entity.Groups = groups;
            entity.PurgedAt = null;
            entity.MovedTo = null;
            entity.LastSeq = seq;
            entity.UpdatedAt = now;
        }

        db.SyncChanges.Add(new SyncChangeRow
        {
            AccountId = streamId, Seq = seq, ChangeId = Guid.Parse(change.ChangeId),
            Kind = change.Kind, EntityKey = change.EntityKey, Op = "upsert",
            Groups = SyncJson.WriteGroups(merge.Winners), OriginDevice = null, ActorId = actorId,
            PreviousGroups = merge.Replaced.Count == 0 ? null
                : System.Text.Json.JsonSerializer.Serialize(merge.Replaced),
            CreatedAt = now,
        });
    }

    public async Task AppendDeletedOverlayAsync(Guid accountId, Guid actorId, string kind, string key, string group,
        string column, Hlc hlc, CancellationToken ct)
    {
        var values = new SyncGroupValues(new Dictionary<string, object?> { [column] = clock.GetUtcNow().UtcDateTime.ToString("o") });
        var change = SyncChange.Upsert(Guid.NewGuid().ToString("D"), kind, key,
            new Dictionary<string, SyncGroupState> { [group] = new(hlc, values) });
        await AppendAsync(accountId, actorId, change, ct);
    }

    public async Task AppendPurgedOverlayAsync(Guid accountId, Guid actorId, string kind, string key, CancellationToken ct)
    {
        var stream = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(db.SyncStreams.Where(s => s.Id == accountId), ct)).Single();
        var entity = await db.SyncEntities.FindAsync([accountId, kind, key], ct);
        var now = clock.GetUtcNow();
        var seq = stream.NextSeq++;
        if (entity is not null)
        {
            entity.Groups = "{}";
            entity.PurgedAt = now;
            entity.LastSeq = seq;
            entity.UpdatedAt = now;
        }
        db.SyncChanges.Add(new SyncChangeRow
        {
            AccountId = accountId, Seq = seq, ChangeId = Guid.NewGuid(), Kind = kind, EntityKey = key,
            Op = "purge", Groups = "{}", OriginDevice = null, ActorId = actorId, CreatedAt = now,
        });
    }
}
