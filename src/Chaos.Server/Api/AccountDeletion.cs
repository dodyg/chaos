using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Api;

/// <summary>Deletes an account and everything the service holds for it (PLAN2 §8.6).</summary>
public sealed class AccountDeletion(ChaosDbContext db, BlobService blobs, TimeProvider clock)
{
    /// <summary>False when the account no longer exists.</summary>
    public async Task<bool> DeleteAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var email = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.PrimaryEmail).FirstOrDefaultAsync(ct);
        if (email is null)
        {
            return false;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var subject = accountId.ToString("D");
            // Tokens first: they point at authorizations. The OpenIddict tables have no account column, only the subject.
            await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().Where(t => t.Subject == subject).ExecuteDeleteAsync(ct);
            await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().Where(a => a.Subject == subject).ExecuteDeleteAsync(ct);
            await db.SyncChanges.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);
            await db.SyncEntities.Where(e => e.AccountId == accountId).ExecuteDeleteAsync(ct);
            await db.EmailCodes.Where(c => c.Email == email).ExecuteDeleteAsync(ct);
            // Devices and logins go with the account row (cascade).
            await db.Accounts.Where(a => a.Id == accountId).ExecuteDeleteAsync(ct);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, Kind = AuditKinds.AccountDeleted, At = now, DeviceId = deviceId });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        // The rows are gone with the account (cascade); now the encrypted objects and any unfinished upload parts (PLAN2 §8.6).
        await blobs.DeleteAccountObjectsAsync(accountId, ct);
        return true;
    }
}
