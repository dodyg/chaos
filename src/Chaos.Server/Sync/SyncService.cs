using System.Globalization;
using System.Text;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

/// <summary>One change of a push after parsing: the change, or why it could not be read.</summary>
public sealed record IncomingChange(string ChangeId, SyncChange? Change, string? Rejection);

public sealed record PushRejection(string ChangeId, string Reason);

public sealed record PushOutcome(IReadOnlyList<string> Accepted, IReadOnlyList<PushRejection> Rejected, string? ServerHlc, long LastSeq);

internal sealed record PreparedPush(IReadOnlyList<IncomingChange> Changes, string? Refusal = null);

/// <summary>One page of the change log, already rendered as the wire JSON.</summary>
public sealed record PullPage(string Json);

/// <summary>
/// The sync protocol on the service (PLAN2 §7.12, §8.3): push merges each change into the account's
/// entities with the same <see cref="Merge"/> the desktop uses, assigns sequence numbers under a lock on
/// the account row, and logs only the groups that won; pull and snapshot read them back. Every query is
/// filtered by the account taken from the token. Nothing here logs content.
/// </summary>
/// <summary>The account was deleted (or never existed) while a push for it was in flight.</summary>
public sealed class AccountUnavailableException() : InvalidOperationException("The account is gone.");

public sealed class SyncService(ChaosDbContext db, TimeProvider clock, IOptions<SyncOptions> options, SyncBroadcaster broadcaster, SyncKindCatalog kinds)
{
    /// <summary>Reads a push body into changes; one unreadable change is rejected on its own, a body that is not a push is an error.</summary>
    public static IReadOnlyList<IncomingChange> ParsePush(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("changes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The push has no list of changes.");
        }

        var changes = new List<IncomingChange>();
        foreach (var element in array.EnumerateArray())
        {
            var id = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("changeId", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            try
            {
                changes.Add(new IncomingChange(id, SyncJson.ParseChange(element), null));
            }
            catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException)
            {
                changes.Add(new IncomingChange(id, null, SyncRejection.Invalid));
            }
        }

        return changes;
    }

    public async Task<PushOutcome> PushAsync(Guid accountId, Guid deviceId, int clientSchema, IReadOnlyList<IncomingChange> incoming, CancellationToken ct)
    {
        var result = await PushPreparedAsync(accountId, deviceId, clientSchema, (_, _) => Task.FromResult(new PreparedPush(incoming)), ct);
        return result.Outcome ?? throw new AccountUnavailableException();
    }

    /// <summary>Prepares server-generated changes under the same account lock used to merge them.</summary>
    internal async Task<(PushOutcome? Outcome, string? Refusal)> PushPreparedAsync(Guid accountId, Guid deviceId, int clientSchema,
        Func<Account, CancellationToken, Task<PreparedPush>> prepare, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync<(PushOutcome? Outcome, string? Refusal)>(async () =>
        {
            // A retry replays the whole unit: nothing tracked by the failed attempt may survive it.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // §8.3 step 1: serialize sequence assignment per account.
            var account = await db.Accounts
                .FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""")
                .FirstOrDefaultAsync(ct);
            if (account is null || account.DeletedAt is not null)
            {
                return (null, "account-unavailable");
            }

            Guid[] streamIds = [accountId];
            var streams = await db.SyncStreams
                .FromSqlInterpolated($"""SELECT * FROM "SyncStreams" WHERE "Id" = ANY({streamIds}) ORDER BY "Id" FOR UPDATE""")
                .ToDictionaryAsync(s => s.Id, ct);
            var stream = streams[accountId];
            var prepared = await prepare(account, ct);
            if (prepared.Refusal is not null)
            {
                return (null, prepared.Refusal);
            }

            var incoming = prepared.Changes;
            var accepted = new List<string>();
            var rejected = new List<PushRejection>();
            Hlc? highest = null;
            var now = clock.GetUtcNow();
            var tooOld = clientSchema < options.Value.MinClientSchema;

            var ids = incoming.Select(c => Guid.TryParse(c.ChangeId, out var id) ? id : (Guid?)null).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
            var logged = (await db.SyncChanges.AsNoTracking()
                .Where(c => c.AccountId == accountId && ids.Contains(c.ChangeId))
                .Select(c => c.ChangeId)
                .ToListAsync(ct)).ToHashSet();

            foreach (var entry in incoming)
            {
                if (entry.Change is not { } change || !Guid.TryParse(entry.ChangeId, out var changeId))
                {
                    rejected.Add(new PushRejection(entry.ChangeId, entry.Rejection ?? SyncRejection.Invalid));
                    continue;
                }

                if (tooOld)
                {
                    rejected.Add(new PushRejection(entry.ChangeId, SyncRejection.SchemaTooOld));
                    continue;
                }

                if (logged.Contains(changeId))
                {
                    accepted.Add(entry.ChangeId); // §8.3 step 2: a replay.
                    continue;
                }

                if (Validate(change, clientSchema, account) is { } reason)
                {
                    rejected.Add(new PushRejection(entry.ChangeId, reason));
                    continue;
                }

                var route = SyncRouter.Route(account, kinds.Find(change.Kind)!, change);
                if (route.Rejection is { } routeRejection)
                {
                    rejected.Add(new PushRejection(entry.ChangeId, routeRejection));
                    continue;
                }

                var result = await MergeAsync(streams[route.StreamId], change, changeId, deviceId, now, ct);
                if (result is { } refusal)
                {
                    rejected.Add(new PushRejection(entry.ChangeId, refusal));
                    continue;
                }

                logged.Add(changeId);
                accepted.Add(entry.ChangeId);
                foreach (var group in change.Groups.Values)
                {
                    if (highest is null || group.Hlc > highest)
                    {
                        highest = group.Hlc;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "SyncEntities" cache
                WHERE cache."StreamId" = {accountId} AND cache."Kind" = 'calendar_cache'
                  AND NOT EXISTS (SELECT 1 FROM "SyncEntities" source
                    WHERE source."StreamId" = cache."StreamId" AND source."Kind" = 'calendar_source'
                      AND source."EntityKey" = cache."EntityKey" AND source."PurgedAt" IS NULL
                      AND source."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
                      AND source."Groups" -> 'enabled' -> 'values' ->> 'Enabled' = '1')
                """, ct);
            await transaction.CommitAsync(ct);
            return (new PushOutcome(accepted, rejected, highest?.Encode(), stream.NextSeq - 1), null);
        });

        // §8.3 step 7: after the commit, tell this process's listeners and any other instance.
        if (result.Outcome is { Accepted.Count: > 0 } outcome)
        {
            // The push is committed: a cancelled request must not turn it into an error.
            await NotifyAsync(accountId, outcome.LastSeq, CancellationToken.None);
        }

        return result;
    }

    private async Task NotifyAsync(Guid accountId, long seq, CancellationToken ct)
    {
        broadcaster.Publish(accountId, seq);
        var channel = SyncNotificationListener.Channel;
        var payload = SyncNotificationListener.Payload(accountId, seq);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify({channel}, {payload})", ct);
    }

    /// <summary>The reason a change is refused before it touches the entity, or null when it is well-formed and the person's own.</summary>
    private string? Validate(SyncChange change, int clientSchema, Account account)
    {
        if (kinds.Find(change.Kind) is not { } kind)
        {
            return SyncRejection.UnknownKind;
        }

        if (change.EntityKey.Length is 0 or > SyncProtocol.MaxKeyLength || change.EntityKey.Split('|').Length != kind.KeyParts)
        {
            return SyncRejection.Invalid;
        }

        if (change.Op == SyncOp.Purge)
        {
            return change.Groups.Count == 0 ? null : SyncRejection.Invalid;
        }

        if (change.Groups.Count == 0)
        {
            return SyncRejection.Invalid;
        }

        foreach (var (name, group) in change.Groups)
        {
            if (kind.Group(name) is not { } info || info.Since > clientSchema)
            {
                return SyncRejection.Invalid;
            }

            foreach (var value in group.Values.Values.Values)
            {
                if (value is string text && Encoding.UTF8.GetByteCount(text) > SyncProtocol.MaxTextBytes)
                {
                    return SyncRejection.TooLarge;
                }
            }
        }

        return null;
    }

    /// <summary>Merges one validated change; returns a rejection reason, or null when it was accepted (whether or not any group won).</summary>
    private async Task<string?> MergeAsync(SyncStream stream, SyncChange change, Guid changeId, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var entity = await db.SyncEntities.FindAsync([stream.Id, change.Kind, change.EntityKey], ct);
        if (change.Op == SyncOp.Purge)
        {
            if (entity is { PurgedAt: not null })
            {
                return null; // Already purged: nothing to do.
            }

            var seq = stream.NextSeq++;
            if (entity is null)
            {
                entity = new SyncEntityRow { AccountId = stream.Id, Kind = change.Kind, EntityKey = change.EntityKey, Groups = "{}" };
                db.SyncEntities.Add(entity);
            }

            // The content goes; the row stays so that a late edit to this key is refused (§8.3 step 6).
            entity.Groups = "{}";
            entity.PurgedAt = now;
            entity.LastSeq = seq;
            entity.UpdatedAt = now;
            Log(stream.Id, seq, changeId, change, SyncOp.Purge, "{}", deviceId, now);
            return null;
        }

        if (entity is { PurgedAt: not null })
        {
            return SyncRejection.Purged;
        }

        var stored = entity is null ? null : SyncJson.ReadGroups(entity.Groups);
        var merge = Merge.Apply(stored, change);
        if (merge.Winners.Count == 0)
        {
            return null;
        }

        var next = stream.NextSeq++;
        var groupsJson = SyncJson.WriteGroups(merge.Merged);
        if (entity is null)
        {
            db.SyncEntities.Add(new SyncEntityRow { AccountId = stream.Id, Kind = change.Kind, EntityKey = change.EntityKey, Groups = groupsJson, LastSeq = next, UpdatedAt = now });
        }
        else
        {
            entity.Groups = groupsJson;
            entity.LastSeq = next;
            entity.UpdatedAt = now;
        }

        Log(stream.Id, next, changeId, change, SyncOp.Upsert, SyncJson.WriteGroups(merge.Winners), deviceId, now);
        return null;
    }

    private void Log(Guid accountId, long seq, Guid changeId, SyncChange change, SyncOp op, string groupsJson, Guid deviceId, DateTimeOffset now) =>
        db.SyncChanges.Add(new SyncChangeRow
        {
            AccountId = accountId,
            Seq = seq,
            ChangeId = changeId,
            Kind = change.Kind,
            EntityKey = change.EntityKey,
            Op = op == SyncOp.Purge ? "purge" : "upsert",
            Groups = groupsJson,
            OriginDevice = deviceId,
            CreatedAt = now,
        });

    // ---- Pull and snapshot ---------------------------------------------------------------------------

    /// <summary>
    /// Changes after a cursor, in sequence order, carrying only the groups that won and that the client's
    /// schema knows (§7.12 forward compatibility). A cursor below the pruned horizon needs the snapshot.
    /// </summary>
    public async Task<PullPage> PullAsync(Guid accountId, int clientSchema, long after, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, SyncProtocol.MaxPullChanges);
        var pruned = await db.SyncStreams.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.ChangesPrunedThroughSeq).FirstOrDefaultAsync(ct);
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            long next = after;
            var more = false;
            var snapshotRequired = after < pruned;
            if (!snapshotRequired)
            {
                var rows = await db.SyncChanges.AsNoTracking()
                    .Where(c => c.AccountId == accountId && c.Seq > after)
                    .OrderBy(c => c.Seq)
                    .Take(limit + 1)
                    .ToListAsync(ct);
                more = rows.Count > limit;
                foreach (var row in rows.Take(limit))
                {
                    next = row.Seq;
                    WriteRow(writer, row, clientSchema);
                }
            }

            writer.WriteEndArray();
            writer.WriteNumber("next", next);
            writer.WriteBoolean("more", more);
            writer.WriteBoolean("snapshotRequired", snapshotRequired);
            writer.WriteEndObject();
        }

        return new PullPage(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private void WriteRow(Utf8JsonWriter writer, SyncChangeRow row, int clientSchema)
    {
        var op = row.Op == "purge" ? SyncOp.Purge : SyncOp.Upsert;
        var groups = op == SyncOp.Purge ? new Dictionary<string, SyncGroupState>() : KnownGroups(row.Kind, SyncJson.ReadGroups(row.Groups), clientSchema);
        if (op == SyncOp.Upsert && groups.Count == 0)
        {
            return; // Everything this change carries is newer than the client understands.
        }

        SyncJson.WriteChange(writer, row.ChangeId.ToString("D"), row.Kind, row.EntityKey, op, groups, row.Seq);
    }

    private Dictionary<string, SyncGroupState> KnownGroups(string kind, Dictionary<string, SyncGroupState> groups, int clientSchema)
    {
        var info = kinds.Find(kind);
        return info is null
            ? []
            : groups.Where(pair => info.Group(pair.Key) is { } group && group.Since <= clientSchema).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>One page of the current state of one kind, in key order, for a device joining or catching up after the horizon.</summary>
    public async Task<PullPage?> SnapshotAsync(Guid accountId, int clientSchema, string kind, string? after, int limit, CancellationToken ct)
    {
        if (kinds.Find(kind) is null)
        {
            return null;
        }

        limit = Math.Clamp(limit, 1, SyncProtocol.MaxPullChanges);

        // Read before the entities: a change that lands meanwhile has a higher sequence than this cursor,
        // so the next pull brings it again, and applying it twice changes nothing.
        var nextSeq = await db.SyncStreams.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.NextSeq).FirstOrDefaultAsync(ct);
        var query = db.SyncEntities.AsNoTracking().Where(e => e.AccountId == accountId && e.Kind == kind && e.PurgedAt == null);
        if (!string.IsNullOrEmpty(after))
        {
            query = query.Where(e => e.EntityKey.CompareTo(after) > 0);
        }

        var rows = await query.OrderBy(e => e.EntityKey).Take(limit + 1).ToListAsync(ct);
        var more = rows.Count > limit;

        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            string? last = null;
            foreach (var row in rows.Take(limit))
            {
                last = row.EntityKey;
                var groups = KnownGroups(kind, SyncJson.ReadGroups(row.Groups), clientSchema);
                if (groups.Count > 0)
                {
                    SyncJson.WriteChange(writer, $"snapshot:{kind}:{row.EntityKey}:{row.LastSeq}", kind, row.EntityKey, SyncOp.Upsert, groups, row.LastSeq);
                }
            }

            writer.WriteEndArray();
            if (last is null)
            {
                writer.WriteNull("next");
            }
            else
            {
                writer.WriteString("next", last);
            }

            writer.WriteBoolean("more", more);
            writer.WriteNumber("cursor", nextSeq - 1);
            writer.WriteEndObject();
        }

        return new PullPage(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Drops change-log rows older than the horizon and records how far each account was pruned (§7.12). Returns the accounts whose horizon moved.</summary>
    public async Task<int> PruneAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - options.Value.ChangeLogHorizon;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH gone AS (
                DELETE FROM "SyncChanges" WHERE "CreatedAt" < {cutoff} RETURNING "StreamId", "Seq"
            ), top AS (
                SELECT "StreamId", max("Seq") AS "Seq" FROM gone GROUP BY "StreamId"
            )
            UPDATE "SyncStreams" SET "ChangesPrunedThroughSeq" = GREATEST("ChangesPrunedThroughSeq", top."Seq")
            FROM top WHERE "SyncStreams"."Id" = top."StreamId"
            """, ct);
    }
}
