using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Api;

public sealed record AccountDeletionResult(bool Deleted, IReadOnlyList<string> TransferFirst);

/// <summary>Deletes private account data while retaining a content-free former-member row in shared spaces.</summary>
public sealed class AccountDeletion(
    ChaosDbContext db,
    BlobService blobs,
    TimeProvider clock,
    ServerChangeWriter changes,
    SyncBroadcaster broadcaster)
{
    public async Task<AccountDeletionResult> DeleteAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow();
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => new { a.PrimaryEmail, a.ProfileId }).FirstOrDefaultAsync(ct);
        if (account is null)
        {
            return new(false, []);
        }

        var memberStreams = await db.SpaceMembers.Where(m => m.AccountId == accountId && m.RemovedAt == null)
            .Select(m => m.SpaceId).Distinct().ToListAsync(ct);
        var transferFirst = new List<string>();
        foreach (var spaceId in memberStreams)
        {
            var live = await db.SpaceMembers.Where(m => m.SpaceId == spaceId && m.RemovedAt == null).ToListAsync(ct);
            var departing = live.Single(m => m.AccountId == accountId);
            var others = live.Where(m => m.AccountId != accountId).ToList();
            if (departing.Role == SpaceMember.OwnerRole && others.Count > 0
                && !others.Any(m => m.Role == SpaceMember.OwnerRole))
            {
                transferFirst.Add(SpaceName(spaceId));
            }
        }
        if (transferFirst.Count > 0)
        {
            return new(false, transferFirst.Distinct(StringComparer.Ordinal).Order().ToList());
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var transactionBlock = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var locked = await db.Accounts
                .FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            if (locked is null)
            {
                return new List<string>();
            }

            var allMembers = await db.SpaceMembers
                .FromSqlInterpolated($"""SELECT * FROM "SpaceMembers" WHERE "AccountId" = {accountId} ORDER BY "SpaceId", "MemberId" FOR UPDATE""")
                .ToListAsync(ct);
            foreach (var ownedSpace in allMembers.Where(m => m.RemovedAt == null && m.Role == SpaceMember.OwnerRole))
            {
                var peers = await db.SpaceMembers.Where(m => m.SpaceId == ownedSpace.SpaceId && m.RemovedAt == null
                    && m.AccountId != accountId).ToListAsync(ct);
                if (peers.Count > 0 && !peers.Any(p => p.Role == SpaceMember.OwnerRole))
                {
                    return new List<string> { SpaceName(ownedSpace.SpaceId) };
                }
            }

            var fanOut = new HashSet<(Guid SpaceId, long Seq)>();
            foreach (var member in allMembers)
            {
                var peers = member.RemovedAt is null
                    ? await db.SpaceMembers.Where(m => m.SpaceId == member.SpaceId && m.RemovedAt == null
                        && m.AccountId != accountId).ToListAsync(ct)
                    : [];
                if (member.RemovedAt is null && peers.Count == 0)
                {
                    await db.SyncStreams.Where(s => s.Id == member.SpaceId).ExecuteDeleteAsync(ct);
                    continue;
                }

                member.RemovedAt ??= now;
                member.DisplayName = "Former member";
                member.GitHubLogin = null;
                member.AccountId = null;
                var stream = (await db.SyncStreams.Where(s => s.Id == member.SpaceId).ToListAsync(ct)).Single();
                var key = NameBasedId.Create($"spacemember:{member.SpaceId:D}:{member.MemberId:D}").ToString("D");
                var hlc = await changes.NextHlcAsync(member.SpaceId, "space_member", key, ct);
                await changes.AppendAsync(member.SpaceId, member.MemberId,
                    SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space_member", key,
                        new Dictionary<string, SyncGroupState>
                        {
                            ["profile"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?>
                            {
                                ["DisplayName"] = member.DisplayName, ["GitHubLogin"] = member.GitHubLogin,
                            })),
                            ["state"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?>
                            {
                                ["RemovedAtUtc"] = member.RemovedAt.Value.UtcDateTime.ToString("o"),
                            })),
                        }), ct);
                fanOut.Add((member.SpaceId, stream.NextSeq - 1));
            }

            var subject = accountId.ToString("D");
            await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
                .Where(t => t.Subject == subject).ExecuteDeleteAsync(ct);
            await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>()
                .Where(a => a.Subject == subject).ExecuteDeleteAsync(ct);
            await db.SyncStreams.Where(s => s.Id == accountId).ExecuteDeleteAsync(ct);
            await db.EmailCodes.Where(c => c.Email == account.PrimaryEmail).ExecuteDeleteAsync(ct);
            db.Accounts.Remove(locked);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, Kind = AuditKinds.AccountDeleted, At = now, DeviceId = deviceId });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            foreach (var (spaceId, seq) in fanOut)
            {
                var accounts = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == spaceId && m.RemovedAt == null
                    && m.AccountId != null).Select(m => m.AccountId!.Value).ToListAsync(ct);
                foreach (var memberAccount in accounts)
                {
                    broadcaster.PublishStream(memberAccount, spaceId, seq);
                }
                var payload = SyncNotificationListener.Payload(spaceId, seq);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_notify({SyncNotificationListener.Channel}, {payload})", CancellationToken.None);
            }
            return new List<string>();
        });

        if (transactionBlock.Count > 0)
        {
            return new(false, transactionBlock.Distinct(StringComparer.Ordinal).Order().ToList());
        }
        await blobs.DeleteAccountObjectsAsync(accountId, ct);
        return new(true, []);
    }

    private string SpaceName(Guid spaceId)
    {
        var groups = db.SyncEntities.AsNoTracking().Where(e => e.AccountId == spaceId && e.Kind == "space")
            .Select(e => e.Groups).FirstOrDefault();
        return groups is null ? "Shared space" : SyncJson.ReadGroups(groups).GetValueOrDefault("name")?
            .Values.Values.GetValueOrDefault("Name")?.ToString() ?? "Shared space";
    }
}
