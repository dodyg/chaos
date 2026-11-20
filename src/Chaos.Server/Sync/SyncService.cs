using System.Text;
using System.Text.Json;
using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

public sealed record IncomingChange(string ChangeId, SyncChange? Change, string? Rejection);
public sealed record PushRejection(string ChangeId, string Reason);
public sealed record PushOutcome(
    IReadOnlyList<string> Accepted,
    IReadOnlyList<PushRejection> Rejected,
    string? ServerHlc,
    long LastSeq,
    IReadOnlyDictionary<string, Guid>? Streams = null);

internal sealed record PreparedPush(IReadOnlyList<IncomingChange> Changes, string? Refusal = null);
public sealed record PullPage(string Json);
public sealed class AccountUnavailableException() : InvalidOperationException("The account is gone.");

/// <summary>Stream-aware merge service. Protocol v1 remains account-only; v2 enforces sharing roles.</summary>
public sealed class SyncService(
    ChaosDbContext db,
    TimeProvider clock,
    IOptions<SyncOptions> options,
    SyncBroadcaster broadcaster,
    SyncKindCatalog kinds,
    SpaceMembershipCache memberships)
{
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
            var id = element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("changeId", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
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

    public Task<PushOutcome> PushAsync(Guid accountId, Guid deviceId, int clientSchema, IReadOnlyList<IncomingChange> incoming, CancellationToken ct) =>
        PushAsync(accountId, deviceId, clientSchema, SyncProtocol.Version1, incoming, ct);

    public async Task<PushOutcome> PushAsync(Guid accountId, Guid deviceId, int clientSchema, int protocolVersion,
        IReadOnlyList<IncomingChange> incoming, CancellationToken ct)
    {
        var result = await PushPreparedAsync(accountId, deviceId, clientSchema, protocolVersion, (_, _) =>
            Task.FromResult(new PreparedPush(incoming)), ct);
        return result.Outcome ?? throw new AccountUnavailableException();
    }

    internal Task<(PushOutcome? Outcome, string? Refusal)> PushPreparedAsync(Guid accountId, Guid deviceId, int clientSchema,
        Func<Account, CancellationToken, Task<PreparedPush>> prepare, CancellationToken ct) =>
        PushPreparedAsync(accountId, deviceId, clientSchema, SyncProtocol.Version1, prepare, ct);

    internal async Task<(PushOutcome? Outcome, string? Refusal)> PushPreparedAsync(Guid accountId, Guid deviceId, int clientSchema,
        int protocolVersion, Func<Account, CancellationToken, Task<PreparedPush>> prepare, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync<(PushOutcome? Outcome, string? Refusal)>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var account = await db.Accounts
                .FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""")
                .FirstOrDefaultAsync(ct);
            if (account is null || account.DeletedAt is not null)
            {
                return (null, "account-unavailable");
            }

            var readable = await memberships.GetAsync(db, account.Id, ct);
            var streamIds = readable.Roles.Keys.Append(accountId).Distinct().ToArray();
            var streams = await db.SyncStreams
                .FromSqlInterpolated($"""SELECT * FROM "SyncStreams" WHERE "Id" = ANY({streamIds}) ORDER BY "Id" FOR UPDATE""")
                .ToDictionaryAsync(s => s.Id, ct);
            var prepared = await prepare(account, ct);
            if (prepared.Refusal is not null)
            {
                return (null, prepared.Refusal);
            }

            var accepted = new List<string>();
            var rejected = new List<PushRejection>();
            var acceptedStreams = new Dictionary<string, Guid>(StringComparer.Ordinal);
            Hlc? highest = null;
            var now = clock.GetUtcNow();
            var tooOld = clientSchema < options.Value.MinClientSchema;

            foreach (var entry in prepared.Changes.OrderBy(entry =>
                entry.Change is { } change
                    ? (change.Op == SyncOp.Purge ? 10_000 : 0)
                        + (change.Op == SyncOp.Purge
                            ? -(kinds.Find(change.Kind)?.ApplyOrder ?? 0)
                            : kinds.Find(change.Kind)?.ApplyOrder ?? int.MaxValue)
                    : int.MaxValue))
            {
                if (entry.Change is not { } change || !Guid.TryParse(entry.ChangeId, out var changeId))
                {
                    rejected.Add(new(entry.ChangeId, entry.Rejection ?? SyncRejection.Invalid));
                    continue;
                }

                if (tooOld)
                {
                    rejected.Add(new(entry.ChangeId, SyncRejection.SchemaTooOld));
                    continue;
                }

                var replayStream = await db.SyncChanges.AsNoTracking()
                    .Where(row => streamIds.Contains(row.AccountId) && row.ChangeId == changeId)
                    .Select(row => (Guid?)row.AccountId)
                    .FirstOrDefaultAsync(ct);
                if (replayStream is { } replayed)
                {
                    if (protocolVersion == SyncProtocol.Version1 && replayed != accountId)
                    {
                        rejected.Add(new(entry.ChangeId, SyncRejection.SchemaTooOld));
                    }
                    else
                    {
                        accepted.Add(entry.ChangeId);
                        acceptedStreams[entry.ChangeId] = replayed;
                    }
                    continue;
                }

                if (Validate(change, clientSchema) is { } validation)
                {
                    rejected.Add(new(entry.ChangeId, validation));
                    continue;
                }
                change = change with
                {
                    Groups = change.Groups.Where(pair => kinds.Find(change.Kind)!.Group(pair.Key)!.Since <= clientSchema)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                };
                if (change.Op != SyncOp.Purge && change.Groups.Count == 0)
                {
                    rejected.Add(new(entry.ChangeId, SyncRejection.Invalid));
                    continue;
                }

                var kind = kinds.Find(change.Kind)!;
                var route = await RouteAsync(account, kind, change, streamIds, ct);
                if (route.Rejection is { } routeRejection)
                {
                    rejected.Add(new(entry.ChangeId,
                        protocolVersion == SyncProtocol.Version1 && routeRejection == SyncRejection.NotMember
                            ? SyncRejection.SchemaTooOld
                            : routeRejection));
                    continue;
                }

                if (protocolVersion == SyncProtocol.Version1 && route.StreamId != accountId)
                {
                    rejected.Add(new(entry.ChangeId, SyncRejection.SchemaTooOld));
                    continue;
                }

                var stream = streams.GetValueOrDefault(route.StreamId);
                if (stream is null || stream.DeletedAt is not null)
                {
                    rejected.Add(new(entry.ChangeId, SyncRejection.NotMember));
                    continue;
                }

                var alreadyLogged = await db.SyncChanges.AsNoTracking()
                    .AnyAsync(c => c.AccountId == route.StreamId && c.ChangeId == changeId, ct);
                if (alreadyLogged)
                {
                    accepted.Add(entry.ChangeId);
                    acceptedStreams[entry.ChangeId] = route.StreamId;
                    continue;
                }

                var permission = await PermissionAsync(account, kind, change, route.StreamId, readable.Roles, ct);
                if (permission is not null)
                {
                    rejected.Add(new(entry.ChangeId, permission));
                    continue;
                }

                var refusal = await MergeAsync(stream, change, changeId, deviceId, account.Id, account.ProfileId, now, ct);
                if (refusal is not null)
                {
                    rejected.Add(new(entry.ChangeId, refusal));
                    continue;
                }

                accepted.Add(entry.ChangeId);
                acceptedStreams[entry.ChangeId] = route.StreamId;
                foreach (var group in change.Groups.Values)
                {
                    if (highest is null || group.Hlc > highest)
                    {
                        highest = group.Hlc;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            if (acceptedStreams.Values.Contains(accountId))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM "SyncEntities" cache
                    WHERE cache."StreamId" = {accountId} AND cache."Kind" = 'calendar_cache'
                      AND NOT EXISTS (SELECT 1 FROM "SyncEntities" source
                        WHERE source."StreamId" = cache."StreamId" AND source."Kind" = 'calendar_source'
                          AND source."EntityKey" = cache."EntityKey" AND source."PurgedAt" IS NULL
                          AND source."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
                          AND source."Groups" -> 'enabled' -> 'values' ->> 'Enabled' = '1')
                    """, ct);
            }

            await transaction.CommitAsync(ct);
            var outcome = new PushOutcome(accepted, rejected, highest?.Encode(),
                streams.GetValueOrDefault(accountId)?.NextSeq - 1 ?? 0, acceptedStreams);
            return (outcome, null);
        });

        if (result.Outcome is { Streams: not null } outcome)
        {
            var heads = outcome.Streams.Where(pair => outcome.Accepted.Contains(pair.Key))
                .GroupBy(pair => pair.Value)
                .Select(group => new { StreamId = group.Key });
            foreach (var head in heads)
            {
                var seq = await db.SyncStreams.AsNoTracking().Where(s => s.Id == head.StreamId)
                    .Select(s => s.NextSeq - 1).FirstAsync(CancellationToken.None);
                await NotifyAsync(head.StreamId, seq, CancellationToken.None);
            }
        }

        return result;
    }

    private string? Validate(SyncChange change, int clientSchema)
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
            if (kind.Group(name) is null)
            {
                return SyncRejection.Invalid;
            }

            if (group.Values.Values.Values.Any(value => value is string text
                && Encoding.UTF8.GetByteCount(text) > SyncProtocol.MaxTextBytes))
            {
                return SyncRejection.TooLarge;
            }
        }

        return null;
    }

    private async Task<(Guid StreamId, string? Rejection)> RouteAsync(Account account, SyncKindInfo kind, SyncChange change,
        Guid[] readableStreams, CancellationToken ct)
    {
        var existing = await db.SyncEntities.AsNoTracking()
            .Where(e => readableStreams.Contains(e.AccountId) && e.Kind == change.Kind && e.EntityKey == change.EntityKey
                && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.AccountId)
            .Distinct()
            .ToListAsync(ct);
        existing.AddRange(db.SyncEntities.Local
            .Where(e => readableStreams.Contains(e.AccountId) && e.Kind == change.Kind && e.EntityKey == change.EntityKey
                && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.AccountId));
        existing = existing.Distinct().ToList();
        if (existing.Count > 1)
        {
            return (account.Id, SyncRejection.Invalid);
        }
        if (existing.Count == 1)
        {
            if (kind.HasSpace && change.Groups.TryGetValue("create", out var incomingCreate)
                && incomingCreate.Values.Values.TryGetValue("SpaceId", out var incomingSpace))
            {
                var expected = existing[0] == account.Id
                    ? account.PersonalSpaceId?.ToString("D")
                    : existing[0].ToString("D");
                if (!string.Equals(incomingSpace?.ToString(), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return (existing[0], SyncRejection.NotOwner);
                }
            }
            return (existing[0], null);
        }

        var hidden = await db.SyncEntities.AsNoTracking()
            .Where(e => e.Kind == change.Kind && e.EntityKey == change.EntityKey
                && e.PurgedAt == null && e.MovedTo == null
                && db.SyncStreams.Any(s => s.Id == e.AccountId && s.Kind == SyncStream.SpaceKind))
            .Select(e => e.AccountId)
            .Distinct()
            .ToListAsync(ct);
        if (hidden.Count > 1)
        {
            return (account.Id, SyncRejection.Invalid);
        }
        if (hidden.Count == 1 && !readableStreams.Contains(hidden[0]))
        {
            return (account.Id, SyncRejection.NotMember);
        }

        return kind.Route.Kind switch
        {
            SyncRouteKind.Account => (account.Id, null),
            SyncRouteKind.OwnSpace => await OwnSpaceAsync(account, kind, change, readableStreams, ct),
            SyncRouteKind.Parent => await ParentAsync(account, kind, change, readableStreams, ct),
            SyncRouteKind.FollowsTag => await FollowsAsync("tag", TagKey(change), readableStreams, ct),
            SyncRouteKind.ServerOnly => (account.Id, SyncRejection.ServerOnly),
            _ => (account.Id, SyncRejection.Invalid),
        };
    }

    private async Task<(Guid StreamId, string? Rejection)> OwnSpaceAsync(Account account, SyncKindInfo kind, SyncChange change,
        Guid[] readableStreams, CancellationToken ct)
    {
        var key = kind.Route.KeyPart is { } keyPart
            ? change.EntityKey.Split('|').ElementAtOrDefault(keyPart)
            : Value(change, kind.Route.Group ?? "create", kind.Route.Column ?? "SpaceId")?.ToString();
        if (string.IsNullOrEmpty(key) && kind.Kind == "tag")
        {
            return (account.Id, null);
        }
        if (kind.Kind == "space")
        {
            key ??= change.EntityKey;
        }
        if (!Guid.TryParse(key, out var spaceId))
        {
            var global = await db.SyncEntities.AsNoTracking()
                .Where(e => e.Kind == kind.Kind && e.EntityKey == change.EntityKey
                    && e.PurgedAt == null && e.MovedTo == null
                    && db.SyncStreams.Any(s => s.Id == e.AccountId && s.Kind == SyncStream.SpaceKind))
                .Select(e => e.AccountId)
                .Distinct()
                .ToListAsync(ct);
            if (global.Any(id => !readableStreams.Contains(id)))
            {
                return (account.Id, SyncRejection.NotMember);
            }
            return (account.Id, SyncRejection.Invalid);
        }
        if (spaceId == account.PersonalSpaceId)
        {
            return (account.Id, null);
        }

        if (!readableStreams.Contains(spaceId)
            || !await db.SyncStreams.AsNoTracking().AnyAsync(s => s.Id == spaceId && s.Kind == SyncStream.SpaceKind && s.DeletedAt == null, ct))
        {
            return (account.Id, SyncRejection.NotMember);
        }

        return (spaceId, null);
    }

    private async Task<(Guid StreamId, string? Rejection)> ParentAsync(Account account, SyncKindInfo kind, SyncChange change,
        Guid[] readableStreams, CancellationToken ct)
    {
        var route = kind.Route;
        var parentKind = route.ParentKind;
        if (route.DiscriminatorColumn is { } discriminator
            && Value(change, route.Group ?? "create", discriminator) is { } target)
        {
            parentKind = target.ToString();
        }

        string? key = route.KeyPart is { } part
            ? change.EntityKey.Split('|').ElementAtOrDefault(part)
            : Value(change, route.Group ?? "create", route.Column ?? string.Empty)?.ToString();
        if (parentKind is null || string.IsNullOrEmpty(key))
        {
            return (account.Id, SyncRejection.ParentUnknown);
        }

        return await FollowsAsync(parentKind, key, readableStreams, ct);
    }

    private async Task<(Guid StreamId, string? Rejection)> FollowsAsync(string parentKind, string? key, Guid[] readableStreams,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key))
        {
            return (Guid.Empty, SyncRejection.ParentUnknown);
        }

        var locations = await db.SyncEntities.AsNoTracking()
            .Where(e => readableStreams.Contains(e.AccountId) && e.Kind == parentKind && e.EntityKey == key
                && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.AccountId)
            .Distinct()
            .ToListAsync(ct);
        locations.AddRange(db.SyncEntities.Local
            .Where(e => readableStreams.Contains(e.AccountId) && e.Kind == parentKind && e.EntityKey == key
                && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.AccountId));
        locations = locations.Distinct().ToList();
        return locations.Count switch
        {
            1 => (locations[0], null),
            0 => await db.SyncEntities.AsNoTracking().AnyAsync(e =>
                    e.Kind == parentKind && e.EntityKey == key && e.PurgedAt == null && e.MovedTo == null
                    && db.SyncStreams.Any(s => s.Id == e.AccountId && s.Kind == SyncStream.SpaceKind), ct)
                ? (Guid.Empty, SyncRejection.NotMember)
                : (Guid.Empty, SyncRejection.ParentUnknown),
            _ => (Guid.Empty, SyncRejection.Invalid),
        };
    }

    private static string? TagKey(SyncChange change) => change.EntityKey.Split('|').ElementAtOrDefault(1);
    private static object? Value(SyncChange change, string groupName, string column) =>
        change.Groups.GetValueOrDefault(groupName)?.Values.Values.GetValueOrDefault(column);

    private async Task<string?> PermissionAsync(Account account, SyncKindInfo kind, SyncChange change, Guid streamId,
        IReadOnlyDictionary<Guid, short> roles, CancellationToken ct)
    {
        if (streamId == account.Id)
        {
            return null;
        }
        if (!roles.TryGetValue(streamId, out var role))
        {
            return SyncRejection.NotMember;
        }
        if (change.Op == SyncOp.Purge)
        {
            return role == SpaceMember.OwnerRole ? null : SyncRejection.Forbidden;
        }
        if (change.Kind == "space" && change.Groups.ContainsKey("deleted"))
        {
            return SyncRejection.Forbidden;
        }

        foreach (var group in change.Groups.Keys)
        {
            var required = kind.Group(group)!.Write;
            var allowed = required switch
            {
                SyncWrite.Owner => role == SpaceMember.OwnerRole,
                SyncWrite.Editor => role is SpaceMember.OwnerRole or SpaceMember.EditorRole,
                SyncWrite.Commenter => role is SpaceMember.OwnerRole or SpaceMember.EditorRole or SpaceMember.ViewerRole,
                SyncWrite.Agent => role is SpaceMember.OwnerRole or SpaceMember.EditorRole or SpaceMember.AgentRole,
                SyncWrite.Author => (group == "deleted" && role == SpaceMember.OwnerRole)
                    || await IsAuthorAsync(account, streamId, change, ct),
                SyncWrite.Server => false,
                _ => false,
            };
            if (!allowed)
            {
                return required == SyncWrite.Server ? SyncRejection.ServerOnly : SyncRejection.Forbidden;
            }
        }

        if (kind.Kind == "diary_entry" && change.Groups.TryGetValue("create", out var create))
        {
            var author = create.Values.Values.GetValueOrDefault("AuthorProfileId")?.ToString();
            if (!Guid.TryParse(author, out var id) || id != account.ProfileId)
            {
                return SyncRejection.Forbidden;
            }
            var stored = await StoredAuthorAsync(streamId, change, ct);
            if (stored is not null && stored != id)
            {
                return SyncRejection.Forbidden;
            }
        }
        if (kind.Kind == "tag" && role == SpaceMember.EditorRole && await TagsOwnerOnlyAsync(streamId, ct))
        {
            return SyncRejection.Forbidden;
        }
        return null;
    }

    private async Task<bool> IsAuthorAsync(Account account, Guid streamId, SyncChange change, CancellationToken ct)
    {
        return await StoredAuthorAsync(streamId, change, ct) == account.ProfileId
            || (await StoredAuthorAsync(streamId, change, ct) is null
                && Value(change, "create", "AuthorProfileId")?.ToString() is { } incoming
                && Guid.TryParse(incoming, out var actor) && actor == account.ProfileId);
    }

    private async Task<Guid?> StoredAuthorAsync(Guid streamId, SyncChange change, CancellationToken ct)
    {
        var author = await db.SyncEntities.AsNoTracking()
            .Where(e => e.AccountId == streamId && e.Kind == change.Kind && e.EntityKey == change.EntityKey
                && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.Groups)
            .FirstOrDefaultAsync(ct);
        var stored = author is null
            ? null
            : SyncJson.ReadGroups(author).GetValueOrDefault("create")?.Values.Values
                .GetValueOrDefault("AuthorProfileId")?.ToString();
        return Guid.TryParse(stored, out var actor) ? actor : null;
    }

    private async Task<bool> TagsOwnerOnlyAsync(Guid streamId, CancellationToken ct)
    {
        var json = await db.SyncEntities.AsNoTracking()
            .Where(e => e.AccountId == streamId && e.Kind == "space" && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.Groups)
            .FirstOrDefaultAsync(ct);
        return json is not null
            && SyncJson.ReadGroups(json).GetValueOrDefault("settings")?.Values.Values
                .GetValueOrDefault("TagsOwnerOnly") is 1L;
    }

    private async Task<string?> MergeAsync(SyncStream stream, SyncChange change, Guid changeId, Guid deviceId,
        Guid accountId, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        var entity = await db.SyncEntities.FindAsync([stream.Id, change.Kind, change.EntityKey], ct);
        if (entity?.MovedTo is not null)
        {
            return SyncRejection.Moved;
        }
        if (change.Op == SyncOp.Purge)
        {
            var seq = stream.NextSeq++;
            if (entity is { PurgedAt: not null })
            {
                return null;
            }
            if (entity is null)
            {
                entity = new SyncEntityRow
                {
                    AccountId = stream.Id, Kind = change.Kind, EntityKey = change.EntityKey, Groups = "{}",
                };
                db.SyncEntities.Add(entity);
            }

            entity.Groups = "{}";
            entity.PurgedAt = now;
            entity.LastSeq = seq;
            entity.UpdatedAt = now;
            if (change.Kind == "attachment" && await db.AttachmentBlobBindings.FindAsync([stream.Id, change.EntityKey], ct) is { } bound)
            {
                db.AttachmentBlobBindings.Remove(bound);
            }

            Log(stream.Id, seq, changeId, change, "purge", "{}", deviceId, actorId, now);
            return null;
        }

        if (entity is { PurgedAt: not null })
        {
            return SyncRejection.Purged;
        }

        var merge = Merge.Apply(entity is null ? null : SyncJson.ReadGroups(entity.Groups), change);
        if (merge.Winners.Count == 0)
        {
            return null;
        }

        var next = stream.NextSeq++;
        var groupsJson = SyncJson.WriteGroups(merge.Merged);
        if (entity is null)
        {
            db.SyncEntities.Add(new SyncEntityRow
            {
                AccountId = stream.Id, Kind = change.Kind, EntityKey = change.EntityKey,
                Groups = groupsJson, LastSeq = next, UpdatedAt = now,
            });
        }
        else
        {
            entity.Groups = groupsJson;
            entity.LastSeq = next;
            entity.UpdatedAt = now;
        }

        if (change.Kind == "attachment" && stream.Id != accountId && merge.Winners.TryGetValue("file", out var file))
        {
            await BindAttachmentAsync(db, stream.Id, change.EntityKey, file, accountId, ct);
        }

        Log(stream.Id, next, changeId, change, "upsert", SyncJson.WriteGroups(merge.Winners),
            deviceId, actorId, now);
        return null;
    }

    /// <summary>
    /// Records that <paramref name="custodian"/>'s blob holds the bytes of an attachment in a space stream, because that
    /// account wrote its current <c>file</c> group. The account stream needs no binding: only the account's own devices write it.
    /// </summary>
    internal static async Task BindAttachmentAsync(ChaosDbContext db, Guid streamId, string attachmentId, SyncGroupState file,
        Guid custodian, CancellationToken ct)
    {
        var binding = await db.AttachmentBlobBindings.FindAsync([streamId, attachmentId], ct);
        var sha = file.Values.Values.GetValueOrDefault("Sha256") as string;
        if (sha is null || !BlobService.IsHash(sha))
        {
            if (binding is not null)
            {
                db.AttachmentBlobBindings.Remove(binding);
            }

            return;
        }

        if (binding is null)
        {
            db.AttachmentBlobBindings.Add(new AttachmentBlobBinding { StreamId = streamId, AttachmentId = attachmentId, Sha256 = sha, CustodianAccountId = custodian });
        }
        else
        {
            binding.Sha256 = sha;
            binding.CustodianAccountId = custodian;
        }
    }

    private void Log(Guid streamId, long seq, Guid changeId, SyncChange change, string op, string groupsJson,
        Guid deviceId, Guid? actorId, DateTimeOffset now) =>
        db.SyncChanges.Add(new SyncChangeRow
        {
            AccountId = streamId, Seq = seq, ChangeId = changeId, Kind = change.Kind, EntityKey = change.EntityKey,
            Op = op, Groups = groupsJson, OriginDevice = deviceId, ActorId = actorId, CreatedAt = now,
        });

    private async Task NotifyAsync(Guid streamId, long seq, CancellationToken ct)
    {
        var members = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == streamId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null && m.AccountId != null)
            .Select(m => m.AccountId!.Value)
            .ToListAsync(ct);
        if (members.Count == 0)
        {
            members.Add(streamId);
        }
        foreach (var accountId in members.Distinct())
        {
            broadcaster.PublishStream(accountId, streamId, seq);
        }

        var payload = SyncNotificationListener.Payload(streamId, seq);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify({SyncNotificationListener.Channel}, {payload})", ct);
    }

    public async Task<PullPage> PullAsync(Guid accountId, Guid streamId, int clientSchema, long after, int limit, CancellationToken ct)
    {
        await EnsureReadableAsync(accountId, streamId, ct);
        limit = Math.Clamp(limit, 1, SyncProtocol.MaxPullChanges);
        var pruned = await db.SyncStreams.AsNoTracking().Where(s => s.Id == streamId)
            .Select(s => s.ChangesPrunedThroughSeq).FirstAsync(ct);
        using var bytes = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("changes");
            writer.WriteStartArray();
            long next = after;
            var snapshotRequired = after < pruned;
            var more = false;
            if (!snapshotRequired)
            {
                var rows = await db.SyncChanges.AsNoTracking().Where(c => c.AccountId == streamId && c.Seq > after)
                    .OrderBy(c => c.Seq).Take(limit + 1).ToListAsync(ct);
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
        return new PullPage(Encoding.UTF8.GetString(bytes.ToArray()));
    }

    public Task<PullPage> PullAsync(Guid accountId, int clientSchema, long after, int limit, CancellationToken ct) =>
        PullAsync(accountId, accountId, clientSchema, after, limit, ct);

    public async Task<PullPage?> SnapshotAsync(Guid accountId, Guid streamId, int clientSchema, string kind, string? after,
        int limit, CancellationToken ct)
    {
        await EnsureReadableAsync(accountId, streamId, ct);
        if (kinds.Find(kind) is null)
        {
            return null;
        }

        limit = Math.Clamp(limit, 1, SyncProtocol.MaxPullChanges);
        var nextSeq = await db.SyncStreams.AsNoTracking().Where(s => s.Id == streamId).Select(s => s.NextSeq).FirstAsync(ct);
        var query = db.SyncEntities.AsNoTracking()
            .Where(e => e.AccountId == streamId && e.Kind == kind && e.PurgedAt == null && e.MovedTo == null);
        if (!string.IsNullOrEmpty(after))
        {
            query = query.Where(e => e.EntityKey.CompareTo(after) > 0);
        }
        var rows = await query.OrderBy(e => e.EntityKey).Take(limit + 1).ToListAsync(ct);
        var more = rows.Count > limit;
        using var bytes = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(bytes))
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
                    SyncJson.WriteChange(writer, $"snapshot:{kind}:{row.EntityKey}:{row.LastSeq}",
                        kind, row.EntityKey, SyncOp.Upsert, groups, row.LastSeq);
                }
            }
            writer.WriteEndArray();
            writer.WriteStringOrNull("next", last);
            writer.WriteBoolean("more", more);
            writer.WriteNumber("cursor", nextSeq - 1);
            writer.WriteEndObject();
        }
        return new PullPage(Encoding.UTF8.GetString(bytes.ToArray()));
    }

    public Task<PullPage?> SnapshotAsync(Guid accountId, int clientSchema, string kind, string? after, int limit, CancellationToken ct) =>
        SnapshotAsync(accountId, accountId, clientSchema, kind, after, limit, ct);

    private async Task EnsureReadableAsync(Guid accountId, Guid streamId, CancellationToken ct)
    {
        if (streamId == accountId)
        {
            return;
        }
        var membership = await memberships.GetAsync(db, accountId, ct);
        if (!membership.Roles.ContainsKey(streamId))
        {
            throw new UnauthorizedAccessException("The account cannot read this stream.");
        }
    }

    private void WriteRow(Utf8JsonWriter writer, SyncChangeRow row, int clientSchema)
    {
        if (row.Op == "moved")
        {
            writer.WriteStartObject();
            writer.WriteString("changeId", row.ChangeId.ToString("D"));
            writer.WriteString("kind", row.Kind);
            writer.WriteString("key", row.EntityKey);
            writer.WriteString("op", "moved");
            writer.WriteNumber("seq", row.Seq);
            using var moved = JsonDocument.Parse(row.Groups);
            writer.WriteString("to", moved.RootElement.GetProperty("to").GetGuid());
            writer.WriteEndObject();
            return;
        }

        var op = row.Op == "purge" ? SyncOp.Purge : SyncOp.Upsert;
        var groups = op == SyncOp.Purge ? new Dictionary<string, SyncGroupState>()
            : KnownGroups(row.Kind, SyncJson.ReadGroups(row.Groups), clientSchema);
        if (op == SyncOp.Upsert && groups.Count == 0)
        {
            return;
        }
        SyncJson.WriteChange(writer, row.ChangeId.ToString("D"), row.Kind, row.EntityKey, op, groups, row.Seq);
    }

    private Dictionary<string, SyncGroupState> KnownGroups(string kind, Dictionary<string, SyncGroupState> groups, int clientSchema)
    {
        var info = kinds.Find(kind);
        return info is null ? [] : groups
            .Where(pair => info.Group(pair.Key) is { } group && group.Since <= clientSchema)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<SyncStreamDto>> StreamsAsync(Guid accountId, CancellationToken ct)
    {
        var membership = await memberships.GetAsync(db, accountId, ct);
        var ids = membership.Roles.Keys.Append(accountId).ToArray();
        var rows = await db.SyncStreams.AsNoTracking().Where(s => ids.Contains(s.Id) && s.DeletedAt == null)
            .Select(s => new { s.Id, s.Kind, s.SpaceId, s.NextSeq, s.ChangesPrunedThroughSeq })
            .ToListAsync(ct);
        return rows.Select(row => new SyncStreamDto(row.Id, row.Kind, row.SpaceId,
            row.Id == accountId ? null : membership.Roles[row.Id], row.NextSeq - 1, row.ChangesPrunedThroughSeq)).ToList();
    }

    public async Task<long> PruneAsync(CancellationToken ct)
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

public sealed record SyncStreamDto(Guid Id, short Kind, Guid? SpaceId, short? Role, long Head, long Pruned);

file static class Utf8JsonWriterExtensions
{
    public static void WriteStringOrNull(this Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
