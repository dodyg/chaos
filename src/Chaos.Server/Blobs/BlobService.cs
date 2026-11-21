using System.Security.Cryptography;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Blobs;

/// <summary>The answer to starting or resuming an upload: the part layout and the parts already held, or the finished blob's state.</summary>
public sealed record UploadStatus(string? BlobState, int PartSize, int PartCount, IReadOnlyList<int> Received);

public enum BlobFailure
{
    None,
    InvalidHash,
    TooLarge,
    Quota,
    NoUpload,
    BadPart,
    Incomplete,
    HashMismatch,
    ScannerUnavailable,
}

public sealed record BlobOutcome<T>(T? Value, BlobFailure Failure)
{
    public static BlobOutcome<T> Ok(T value) => new(value, BlobFailure.None);

    public static BlobOutcome<T> Fail(BlobFailure failure) => new(default, failure);

    public bool Succeeded => Failure == BlobFailure.None;
}

/// <summary>
/// Resumable, verified, scanned and encrypted blob storage for one account at a time (PLAN2 §9.2). The account always comes from the
/// token. Parts are kept until the upload completes or expires, so a dropped connection resumes from the parts the service holds.
/// </summary>
public sealed class BlobService(ChaosDbContext db, IBlobStore store, IMalwareScanner scanner, IKeyWrapper keys, IOptions<BlobOptions> blobOptions, IOptions<LimitsOptions> limits, TimeProvider clock, ILogger<BlobService> log)
{
    private BlobOptions Options => blobOptions.Value;

    public static string ObjectKey(Guid accountId, string sha256) => $"blobs/{accountId:N}/{sha256}/{Guid.NewGuid():N}";

    private static string PartsPrefix(Guid accountId, Guid streamId, string sha256) => streamId == accountId
        ? $"parts/{accountId:N}/{sha256}/"
        : $"parts/{accountId:N}/stream-{streamId:N}/{sha256}/";

    public static string PartKey(Guid accountId, Guid streamId, string sha256, int index) => $"{PartsPrefix(accountId, streamId, sha256)}{index}";

    public static string AccountPrefix(Guid accountId) => $"blobs/{accountId:N}/";

    public static string AccountPartsPrefix(Guid accountId) => $"parts/{accountId:N}/";

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public Task<BlobRow?> FindAsync(Guid accountId, string sha256, CancellationToken ct) =>
        FindInStreamAsync(accountId, sha256, ct);

    private Task<BlobRow?> FindInStreamAsync(Guid streamId, string sha256, CancellationToken ct) =>
        db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.StreamId == streamId && b.Sha256 == sha256, ct);

    /// <summary>Whether this account can upload bytes for the selected stream.</summary>
    public async Task<bool> CanUploadToStreamAsync(Guid accountId, Guid streamId, string sha256, string? attachmentId, CancellationToken ct)
    {
        if (streamId == accountId)
        {
            return true;
        }

        var member = await db.SpaceMembers.AsNoTracking().FirstOrDefaultAsync(m => m.SpaceId == streamId && m.AccountId == accountId
            && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null
            && db.SyncStreams.Any(s => s.Id == streamId && s.Kind == SyncStream.SpaceKind && s.DeletedAt == null), ct);
        if (member is null)
        {
            return false;
        }
        if (member.Role is SpaceMember.OwnerRole or SpaceMember.EditorRole)
        {
            return true;
        }
        if (member.Role != SpaceMember.ViewerRole || string.IsNullOrEmpty(attachmentId))
        {
            return false;
        }

        var binding = await db.AttachmentBlobBindings.AsNoTracking().FirstOrDefaultAsync(b => b.StreamId == streamId
            && b.AttachmentId == attachmentId && b.CustodianAccountId == accountId && b.Sha256 == sha256, ct);
        var attachment = binding is null ? null : await db.SyncEntities.FindAsync([streamId, "attachment", attachmentId], ct);
        return attachment is { PurgedAt: null, MovedTo: null }
            && await DiaryAttachmentAuthorization.IsAuthorAsync(db, streamId, member.MemberId, SyncJson.ReadGroups(attachment.Groups), ct);
    }

    /// <summary>Finds a blob only in the requested readable stream, using the server-recorded attachment custody.</summary>
    public async Task<BlobRow?> FindReadableInStreamAsync(Guid accountId, Guid streamId, string sha256, CancellationToken ct)
    {
        if (streamId == accountId)
        {
            return await FindAsync(accountId, sha256, ct);
        }

        return await db.Blobs.FromSqlInterpolated($"""
            SELECT b.* FROM "Blobs" b WHERE b."StreamId" = {streamId} AND b."Sha256" = {sha256}
              AND EXISTS (SELECT 1 FROM "SpaceMembers" member JOIN "SyncStreams" stream ON stream."Id" = member."SpaceId"
                WHERE member."SpaceId" = b."StreamId" AND member."AccountId" = {accountId}
                  AND member."MemberKind" = 1 AND member."RemovedAt" IS NULL AND stream."Kind" = 2 AND stream."DeletedAt" IS NULL)
              AND EXISTS (SELECT 1 FROM "SyncEntities" e WHERE e."StreamId" = b."StreamId" AND e."Kind" = 'attachment'
                AND e."PurgedAt" IS NULL AND e."MovedTo" IS NULL
                AND e."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
                AND e."Groups" -> 'file' -> 'values' ->> 'Sha256' = b."Sha256")
            """).AsNoTracking().FirstOrDefaultAsync(ct);
    }

    /// <summary>Starts an upload, or resumes one, or says the blob is already there. Starting is idempotent.</summary>
    public async Task<BlobOutcome<UploadStatus>> StartAsync(Guid accountId, Guid deviceId, Guid streamId, string sha256, long size, CancellationToken ct)
    {
        if (!IsHash(sha256))
        {
            return BlobOutcome<UploadStatus>.Fail(BlobFailure.InvalidHash);
        }

        if (size < 0 || size > Options.MaxBlobBytes)
        {
            return BlobOutcome<UploadStatus>.Fail(BlobFailure.TooLarge);
        }

        var existing = await FindInStreamAsync(streamId, sha256, ct);
        if (existing is not null)
        {
            return BlobOutcome<UploadStatus>.Ok(new UploadStatus(existing.State, Options.PartSize, 0, []));
        }

        var stored = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.StorageBytes).FirstAsync(ct);
        if (stored + size > limits.Value.AccountQuotaBytes)
        {
            await AuditQuotaAsync(accountId, deviceId, ct);
            return BlobOutcome<UploadStatus>.Fail(BlobFailure.Quota);
        }

        var partCount = (int)((size + Options.PartSize - 1) / Options.PartSize);
        var now = clock.GetUtcNow();
        var upload = await db.BlobUploads.FirstOrDefaultAsync(u => u.StreamId == streamId && u.Sha256 == sha256, ct);
        if (upload is not null && upload.AccountId != accountId)
        {
            return BlobOutcome<UploadStatus>.Fail(BlobFailure.NoUpload);
        }
        if (upload is not null && upload.ByteSize != size)
        {
            // The same hash cannot have two sizes; an earlier start with a wrong size is discarded.
            await DiscardUploadAsync(accountId, streamId, sha256, ct);
            upload = null;
        }

        if (upload is null)
        {
            db.BlobUploads.Add(new BlobUploadRow { StreamId = streamId, AccountId = accountId, Sha256 = sha256, ByteSize = size, PartCount = partCount, CreatedAt = now, UpdatedAt = now, DeviceId = deviceId });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                upload = await db.BlobUploads.AsNoTracking().FirstOrDefaultAsync(u => u.StreamId == streamId && u.Sha256 == sha256, ct);
                if (upload is null)
                {
                    throw;
                }
                if (upload.AccountId != accountId || upload.ByteSize != size)
                {
                    return BlobOutcome<UploadStatus>.Fail(BlobFailure.NoUpload);
                }
            }
        }

        var received = await db.BlobParts.AsNoTracking().Where(p => p.StreamId == streamId && p.Sha256 == sha256).OrderBy(p => p.PartIndex).Select(p => p.PartIndex).ToListAsync(ct);
        return BlobOutcome<UploadStatus>.Ok(new UploadStatus(null, Options.PartSize, partCount, received));
    }

    /// <summary>Stores one part. Sending a part again replaces it. The part must be exactly the size the layout calls for.</summary>
    public async Task<BlobFailure> PutPartAsync(Guid accountId, Guid streamId, string sha256, int index, Stream body, CancellationToken ct)
    {
        if (!IsHash(sha256))
        {
            return BlobFailure.InvalidHash;
        }

        var upload = await db.BlobUploads.AsNoTracking().FirstOrDefaultAsync(u => u.StreamId == streamId && u.AccountId == accountId && u.Sha256 == sha256, ct);
        if (upload is null)
        {
            return BlobFailure.NoUpload;
        }

        if (index < 0 || index >= upload.PartCount)
        {
            return BlobFailure.BadPart;
        }

        var expected = (int)Math.Min(Options.PartSize, upload.ByteSize - ((long)index * Options.PartSize));
        var buffer = new byte[expected + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }

        if (total != expected)
        {
            return BlobFailure.BadPart;
        }

        await store.PutAsync(PartKey(accountId, streamId, sha256, index), new MemoryStream(buffer, 0, expected, writable: false), ct);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BlobParts" ("StreamId", "Sha256", "PartIndex", "ByteSize") VALUES ({streamId}, {sha256}, {index}, {expected})
            ON CONFLICT ("StreamId", "Sha256", "PartIndex") DO UPDATE SET "ByteSize" = EXCLUDED."ByteSize"
            """, ct);
        await db.BlobUploads.Where(u => u.StreamId == streamId && u.AccountId == accountId && u.Sha256 == sha256).ExecuteUpdateAsync(s => s.SetProperty(u => u.UpdatedAt, now), ct);
        return BlobFailure.None;
    }

    /// <summary>
    /// Joins the parts, checks the SHA-256, scans, and (only when clean) encrypts and stores. A mismatch discards the upload. A blocked
    /// file leaves a <c>Blocked</c> row and no bytes, so every device can learn of it without downloading anything.
    /// </summary>
    public async Task<BlobOutcome<BlobRow>> CompleteAsync(Guid accountId, Guid deviceId, Guid streamId, string sha256, string? attachmentId, CancellationToken ct)
    {
        if (!IsHash(sha256))
        {
            return BlobOutcome<BlobRow>.Fail(BlobFailure.InvalidHash);
        }

        var finished = await FindInStreamAsync(streamId, sha256, ct);
        if (finished is not null)
        {
            if (await db.BlobUploads.AsNoTracking().AnyAsync(u => u.StreamId == streamId && u.AccountId == accountId && u.Sha256 == sha256, ct))
            {
                await DiscardUploadAsync(accountId, streamId, sha256, ct);
            }
            return BlobOutcome<BlobRow>.Ok(finished);
        }

        var upload = await db.BlobUploads.AsNoTracking().FirstOrDefaultAsync(u => u.StreamId == streamId && u.AccountId == accountId && u.Sha256 == sha256, ct);
        if (upload is null)
        {
            return BlobOutcome<BlobRow>.Fail(BlobFailure.NoUpload);
        }

        var received = await db.BlobParts.AsNoTracking().Where(p => p.StreamId == streamId && p.Sha256 == sha256).CountAsync(ct);
        if (received != upload.PartCount)
        {
            return BlobOutcome<BlobRow>.Fail(BlobFailure.Incomplete);
        }

        ScanResult scan;
        string actualHash;
        await using (var joined = new JoinedParts(store, accountId, streamId, sha256, upload.PartCount))
        using (var hashing = new HashingReadStream(joined))
        {
            try
            {
                scan = await scanner.ScanAsync(hashing, ct);
                await hashing.DrainAsync(ct);
            }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException)
            {
                // Never clean by default: the parts stay, and the device tries again.
                log.LogWarning(error, "The malware scanner was unavailable for an upload");
                return BlobOutcome<BlobRow>.Fail(BlobFailure.ScannerUnavailable);
            }

            actualHash = hashing.Hash();
        }

        if (!string.Equals(actualHash, sha256, StringComparison.Ordinal))
        {
            await DiscardUploadAsync(accountId, streamId, sha256, ct);
            return BlobOutcome<BlobRow>.Fail(BlobFailure.HashMismatch);
        }

        var now = clock.GetUtcNow();
        BlobRow row;
        var objectId = Guid.NewGuid();
        if (!scan.Clean)
        {
            row = new BlobRow { StreamId = streamId, AccountId = accountId, ObjectId = objectId, Sha256 = sha256, ByteSize = upload.ByteSize, State = BlobStates.Blocked, Detail = scan.Signature, StorageKey = string.Empty, WrappedKey = [], CreatedAt = now };
            log.LogWarning("A blob was blocked by the malware check ({Signature}) for account {AccountId}", scan.Signature, accountId);
        }
        else
        {
            var dataKey = BlobCrypto.NewDataKey();
            // Every completion encrypts with its own data key into its own object, so two completions of the same blob
            // (a client retry) can never overwrite each other's bytes; the one whose row lands is the one that stays readable.
            var key = ObjectKey(accountId, sha256);
            // Persist a delayed cleanup before writing the object. Publishing its references removes this entry
            // in the same transaction; a crash before publication leaves an object the sweep can reclaim.
            db.BlobDeletions.Add(new BlobDeletionRow { Id = objectId, StorageKey = key, NotBefore = now + Options.UploadExpiry });
            await db.SaveChangesAsync(ct);
            var temp = Path.Combine(Path.GetTempPath(), "chaos-blob-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var sealedFile = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
                {
                    await using (var plain = new JoinedParts(store, accountId, streamId, sha256, upload.PartCount))
                    {
                        await BlobCrypto.EncryptAsync(plain, sealedFile, dataKey, ct);
                    }

                    sealedFile.Seek(0, SeekOrigin.Begin);
                    await store.PutAsync(key, sealedFile, ct);
                }
            }
            finally
            {
                File.Delete(temp);
            }

            row = new BlobRow { StreamId = streamId, AccountId = accountId, ObjectId = objectId, Sha256 = sha256, ByteSize = upload.ByteSize, State = BlobStates.Clean, StorageKey = key, WrappedKey = keys.Wrap(dataKey), CreatedAt = now };
        }

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var result = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var lockedStream = await db.SyncStreams.FromSqlInterpolated($"SELECT * FROM \"SyncStreams\" WHERE \"Id\" = {streamId} FOR UPDATE").SingleOrDefaultAsync(ct);
                if (lockedStream is null || lockedStream.DeletedAt is not null
                    || !await CanUploadToStreamAsync(accountId, streamId, sha256, attachmentId, ct)) return BlobFailure.NoUpload;
                if (await db.Blobs.AsNoTracking().AnyAsync(b => b.StreamId == streamId && b.Sha256 == sha256 && b.ObjectId == objectId, ct))
                {
                    return BlobFailure.None; // A retried commit already stored this object.
                }
                if (row.State == BlobStates.Clean)
                {
                    // Charge the object in the same transaction as its references.
                    var counted = await db.Accounts.Where(a => a.Id == accountId && a.StorageBytes + upload.ByteSize <= limits.Value.AccountQuotaBytes)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.StorageBytes, a => a.StorageBytes + upload.ByteSize), ct);
                    if (counted == 0) return BlobFailure.Quota;
                }
                db.BlobObjects.Add(new BlobObjectRow { Id = objectId, AccountId = accountId, ByteSize = row.ByteSize, State = row.State, StorageKey = row.StorageKey });
                db.Blobs.Add(row);
                if (row.StorageKey.Length > 0) await db.BlobDeletions.Where(d => d.Id == objectId).ExecuteDeleteAsync(ct);
                if (streamId == accountId)
                {
                    var bindings = await db.AttachmentBlobBindings.AsNoTracking()
                        .Where(b => b.CustodianAccountId == accountId && b.Sha256 == sha256).ToListAsync(ct);
                    foreach (var binding in bindings)
                    {
                        if (!await CanUploadToStreamAsync(accountId, binding.StreamId, sha256, binding.AttachmentId, ct)) continue;
                        var attachment = await db.SyncEntities.FindAsync([binding.StreamId, "attachment", binding.AttachmentId], ct);
                        if (attachment is null || attachment.PurgedAt is not null || attachment.MovedTo is not null) continue;
                        var groups = SyncJson.ReadGroups(attachment.Groups);
                        if (groups.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null
                            || groups.GetValueOrDefault("file")?.Values.Values.GetValueOrDefault("Sha256")?.ToString() != sha256) continue;
                        await SyncService.CopyBlobReferenceAsync(db, accountId, binding.StreamId, sha256, ct);
                    }
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return BlobFailure.None;
            });
            if (result != BlobFailure.None)
            {
                if (row.StorageKey.Length > 0) await TryDeletePendingObjectAsync(objectId, row.StorageKey, ct);
                if (result == BlobFailure.Quota) await AuditQuotaAsync(accountId, deviceId, ct);
                return BlobOutcome<BlobRow>.Fail(result);
            }
        }
        catch (DbUpdateException)
        {
            // A parallel completion stored it first. Its transaction won; this object's quota charge rolled back.
            db.ChangeTracker.Clear();
            var winner = await FindInStreamAsync(streamId, sha256, ct);
            if (row.State == BlobStates.Clean && winner?.ObjectId != objectId)
            {
                await TryDeletePendingObjectAsync(objectId, row.StorageKey, ct);
            }
            if (winner is null)
            {
                throw;
            }
            await DiscardUploadAsync(accountId, streamId, sha256, ct);
            return BlobOutcome<BlobRow>.Ok(winner);
        }

        await DiscardUploadAsync(accountId, streamId, sha256, ct);
        return BlobOutcome<BlobRow>.Ok(row);
    }

    /// <summary>Writes the plaintext range of a clean blob. <paramref name="endInclusive"/> is clamped by the caller.</summary>
    public async Task WriteRangeAsync(BlobRow blob, long start, long endInclusive, Stream destination, CancellationToken ct)
    {
        var dataKey = keys.Unwrap(blob.WrappedKey);
        await BlobCrypto.DecryptRangeAsync(offset => store.OpenReadAsync(blob.StorageKey, offset, ct), dataKey, blob.ByteSize, start, endInclusive, destination, ct);
    }

    private async Task DiscardUploadAsync(Guid accountId, Guid streamId, string sha256, CancellationToken ct)
    {
        await db.BlobUploads.Where(u => u.AccountId == accountId && u.StreamId == streamId && u.Sha256 == sha256).ExecuteDeleteAsync(ct);
        await store.DeletePrefixAsync(PartsPrefix(accountId, streamId, sha256), ct);
    }

    private async Task AuditQuotaAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        db.AuditEvents.Add(new AuditEvent { AccountId = accountId, Kind = AuditKinds.Quota, At = clock.GetUtcNow(), DeviceId = deviceId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes unfinished parts and collects objects no surviving stream references after account deletion.</summary>
    public async Task DeleteAccountObjectsAsync(Guid accountId, CancellationToken ct)
    {
        await store.DeletePrefixAsync(AccountPartsPrefix(accountId), ct);
        var candidates = await db.BlobObjects.AsNoTracking().Where(o => o.AccountId == accountId).Select(o => o.Id).ToListAsync(ct);
        await PurgeUnreferencedObjectsAsync(candidates, ct);
    }

    /// <summary>Removes parts whose upload rows were removed along with a stream.</summary>
    public Task DeleteUploadPartsAsync(Guid accountId, Guid streamId, string sha256, CancellationToken ct) =>
        store.DeletePrefixAsync(PartsPrefix(accountId, streamId, sha256), ct);

    /// <summary>
    /// One pass of housekeeping: marks blobs no live attachment references, deletes the ones unreferenced past the grace period (PLAN2 §9.2),
    /// and discards uploads left unfinished. Returns how many blobs and uploads were removed.
    /// </summary>
    public async Task<(int Blobs, int Uploads)> SweepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Blobs\" b SET \"UnreferencedSince\" = NULL WHERE b.\"UnreferencedSince\" IS NOT NULL AND " + ReferencedSql, ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Blobs\" b SET \"UnreferencedSince\" = {0} WHERE b.\"UnreferencedSince\" IS NULL AND NOT " + ReferencedSql, [now], ct);

        var cutoff = now - Options.UnreferencedGrace;
        var expired = await db.Blobs.AsNoTracking().Where(b => b.UnreferencedSince != null && b.UnreferencedSince <= cutoff)
            .Select(b => new { b.StreamId, b.Sha256, b.ObjectId }).ToListAsync(ct);
        var removedBlobs = 0;
        foreach (var blob in expired)
        {
            removedBlobs += await DeleteStaleReferenceAsync(blob.StreamId, blob.Sha256, blob.ObjectId, cutoff, ct);
        }

        var orphaned = await db.BlobObjects.AsNoTracking().Where(o => !db.Blobs.Any(b => b.ObjectId == o.Id)).Select(o => o.Id).ToListAsync(ct);
        await PurgeUnreferencedObjectsAsync(orphaned, ct);
        await FlushObjectDeletionsAsync(ct);

        var stale = now - Options.UploadExpiry;
        var uploads = await db.BlobUploads.AsNoTracking().Where(u => u.UpdatedAt <= stale).Select(u => new { u.AccountId, u.StreamId, u.Sha256 }).ToListAsync(ct);
        foreach (var upload in uploads)
        {
            await DiscardUploadAsync(upload.AccountId, upload.StreamId, upload.Sha256, ct);
        }

        return (removedBlobs, uploads.Count);
    }

    /// <summary>Queues objects for deletion only after the final stream reference has gone.</summary>
    public async Task<int> PurgeUnreferencedObjectsAsync(IReadOnlyCollection<Guid> candidates, CancellationToken ct)
    {
        var removed = 0;
        foreach (var id in candidates.Distinct())
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var queued = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var obj = await db.BlobObjects.FromSqlInterpolated($"SELECT * FROM \"BlobObjects\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
                if (obj is null || await db.Blobs.AnyAsync(b => b.ObjectId == id, ct)) return false;
                if (obj.State == BlobStates.Clean)
                {
                    await db.Accounts.Where(a => a.Id == obj.AccountId)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.StorageBytes, a => a.StorageBytes - obj.ByteSize), ct);
                }
                if (obj.StorageKey.Length > 0) db.BlobDeletions.Add(new BlobDeletionRow { Id = obj.Id, StorageKey = obj.StorageKey, NotBefore = clock.GetUtcNow() });
                db.BlobObjects.Remove(obj);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            });
            if (queued) removed++;
        }
        await FlushObjectDeletionsAsync(ct);
        return removed;
    }

    /// <summary>Removes stale stream references to objects from a purged space before collecting those objects.</summary>
    public async Task RemoveUnreferencedRowsAsync(IReadOnlyCollection<Guid> candidates, CancellationToken ct)
    {
        var references = await db.Blobs.AsNoTracking().Where(b => candidates.Contains(b.ObjectId))
            .Select(b => new { b.StreamId, b.Sha256, b.ObjectId }).ToListAsync(ct);
        foreach (var reference in references)
        {
            await DeleteStaleReferenceAsync(reference.StreamId, reference.Sha256, reference.ObjectId, null, ct);
        }
    }

    private Task<int> DeleteStaleReferenceAsync(Guid streamId, string sha256, Guid objectId, DateTimeOffset? cutoff, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var stream = await db.SyncStreams.FromSqlInterpolated($"SELECT * FROM \"SyncStreams\" WHERE \"Id\" = {streamId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (stream is null) return 0;
            var sql = "DELETE FROM \"Blobs\" b WHERE b.\"StreamId\" = {0} AND b.\"Sha256\" = {1} AND b.\"ObjectId\" = {2} AND "
                + (cutoff is null ? "" : "b.\"UnreferencedSince\" <= {3} AND ") + "NOT " + ReferencedSql;
            object[] parameters = cutoff is null ? [streamId, sha256, objectId] : [streamId, sha256, objectId, cutoff.Value];
            var removed = await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
            await transaction.CommitAsync(ct);
            return removed;
        });

    private async Task FlushObjectDeletionsAsync(CancellationToken ct)
    {
        var pending = await db.BlobDeletions.AsNoTracking().Where(d => d.NotBefore <= clock.GetUtcNow()).ToListAsync(ct);
        foreach (var item in pending)
        {
            await store.DeleteAsync(item.StorageKey, ct);
            await db.BlobDeletions.Where(d => d.Id == item.Id).ExecuteDeleteAsync(ct);
        }
    }

    private async Task TryDeletePendingObjectAsync(Guid id, string storageKey, CancellationToken ct)
    {
        try
        {
            await store.DeleteAsync(storageKey, ct);
            await db.BlobDeletions.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The durable entry remains for a later sweep; no content or object path is logged.
            log.LogWarning("Deferred cleanup for blob object {ObjectId}", id);
        }
    }

    /// <summary>
    /// True for a blob that a live attachment needs: one in the owning account's own stream, or one in a space stream
    /// bound to this account's blob.
    /// </summary>
    private const string ReferencedSql = """
        (EXISTS (
            SELECT 1 FROM "SyncEntities" e
            WHERE e."StreamId" = b."StreamId" AND e."Kind" = 'attachment' AND e."PurgedAt" IS NULL AND e."MovedTo" IS NULL
              AND e."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND e."Groups" -> 'file' -> 'values' ->> 'Sha256' = b."Sha256"))
        """;

    /// <summary>The parts of one upload read as a single stream, one part open at a time.</summary>
    private sealed class JoinedParts(IBlobStore store, Guid accountId, Guid streamId, string sha256, int partCount) : Stream
    {
        private int _next;
        private Stream? _current;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (_next >= partCount)
                    {
                        return 0;
                    }

                    _current = await store.OpenReadAsync(PartKey(accountId, streamId, sha256, _next++), 0, cancellationToken)
                        ?? throw new IOException("An uploaded part is missing from the store.");
                }

                var read = await _current.ReadAsync(buffer, cancellationToken);
                if (read > 0)
                {
                    return read;
                }

                await _current.DisposeAsync();
                _current = null;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (_current is not null)
            {
                await _current.DisposeAsync();
            }

            await base.DisposeAsync();
        }
    }

    /// <summary>Hashes whatever is read through it, so the scan and the SHA-256 check share one pass.</summary>
    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _result;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <summary>Reads what a scanner left unread, so the hash covers the whole file.</summary>
        public async Task DrainAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            while (await ReadAsync(buffer, ct) > 0)
            {
            }
        }

        public string Hash() => _result ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
