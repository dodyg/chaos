using System.Security.Cryptography;
using Chaos.Server.Auth;
using Chaos.Server.Data;
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

    public static string PartKey(Guid accountId, string sha256, int index) => $"parts/{accountId:N}/{sha256}/{index}";

    public static string AccountPrefix(Guid accountId) => $"blobs/{accountId:N}/";

    public static string AccountPartsPrefix(Guid accountId) => $"parts/{accountId:N}/";

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public Task<BlobRow?> FindAsync(Guid accountId, string sha256, CancellationToken ct) =>
        db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.AccountId == accountId && b.Sha256 == sha256, ct);

    /// <summary>Starts an upload, or resumes one, or says the blob is already there. Starting is idempotent.</summary>
    public async Task<BlobOutcome<UploadStatus>> StartAsync(Guid accountId, Guid deviceId, string sha256, long size, CancellationToken ct)
    {
        if (!IsHash(sha256))
        {
            return BlobOutcome<UploadStatus>.Fail(BlobFailure.InvalidHash);
        }

        if (size < 0 || size > Options.MaxBlobBytes)
        {
            return BlobOutcome<UploadStatus>.Fail(BlobFailure.TooLarge);
        }

        var existing = await FindAsync(accountId, sha256, ct);
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
        var upload = await db.BlobUploads.FirstOrDefaultAsync(u => u.AccountId == accountId && u.Sha256 == sha256, ct);
        if (upload is not null && upload.ByteSize != size)
        {
            // The same hash cannot have two sizes; an earlier start with a wrong size is discarded.
            await DiscardUploadAsync(accountId, sha256, ct);
            upload = null;
        }

        if (upload is null)
        {
            db.BlobUploads.Add(new BlobUploadRow { AccountId = accountId, Sha256 = sha256, ByteSize = size, PartCount = partCount, CreatedAt = now, UpdatedAt = now, DeviceId = deviceId });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two first calls raced; the other one started it.
                db.ChangeTracker.Clear();
            }
        }

        var received = await db.BlobParts.AsNoTracking().Where(p => p.AccountId == accountId && p.Sha256 == sha256).OrderBy(p => p.PartIndex).Select(p => p.PartIndex).ToListAsync(ct);
        return BlobOutcome<UploadStatus>.Ok(new UploadStatus(null, Options.PartSize, partCount, received));
    }

    /// <summary>Stores one part. Sending a part again replaces it. The part must be exactly the size the layout calls for.</summary>
    public async Task<BlobFailure> PutPartAsync(Guid accountId, string sha256, int index, Stream body, CancellationToken ct)
    {
        if (!IsHash(sha256))
        {
            return BlobFailure.InvalidHash;
        }

        var upload = await db.BlobUploads.AsNoTracking().FirstOrDefaultAsync(u => u.AccountId == accountId && u.Sha256 == sha256, ct);
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

        await store.PutAsync(PartKey(accountId, sha256, index), new MemoryStream(buffer, 0, expected, writable: false), ct);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BlobParts" ("AccountId", "Sha256", "PartIndex", "ByteSize") VALUES ({accountId}, {sha256}, {index}, {expected})
            ON CONFLICT ("AccountId", "Sha256", "PartIndex") DO UPDATE SET "ByteSize" = EXCLUDED."ByteSize"
            """, ct);
        await db.BlobUploads.Where(u => u.AccountId == accountId && u.Sha256 == sha256).ExecuteUpdateAsync(s => s.SetProperty(u => u.UpdatedAt, now), ct);
        return BlobFailure.None;
    }

    /// <summary>
    /// Joins the parts, checks the SHA-256, scans, and (only when clean) encrypts and stores. A mismatch discards the upload. A blocked
    /// file leaves a <c>Blocked</c> row and no bytes, so every device can learn of it without downloading anything.
    /// </summary>
    public async Task<BlobOutcome<BlobRow>> CompleteAsync(Guid accountId, Guid deviceId, string sha256, CancellationToken ct)
    {
        if (!IsHash(sha256))
        {
            return BlobOutcome<BlobRow>.Fail(BlobFailure.InvalidHash);
        }

        var finished = await FindAsync(accountId, sha256, ct);
        if (finished is not null)
        {
            return BlobOutcome<BlobRow>.Ok(finished);
        }

        var upload = await db.BlobUploads.AsNoTracking().FirstOrDefaultAsync(u => u.AccountId == accountId && u.Sha256 == sha256, ct);
        if (upload is null)
        {
            return BlobOutcome<BlobRow>.Fail(BlobFailure.NoUpload);
        }

        var received = await db.BlobParts.AsNoTracking().Where(p => p.AccountId == accountId && p.Sha256 == sha256).CountAsync(ct);
        if (received != upload.PartCount)
        {
            return BlobOutcome<BlobRow>.Fail(BlobFailure.Incomplete);
        }

        ScanResult scan;
        string actualHash;
        await using (var joined = new JoinedParts(store, accountId, sha256, upload.PartCount))
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
            await DiscardUploadAsync(accountId, sha256, ct);
            return BlobOutcome<BlobRow>.Fail(BlobFailure.HashMismatch);
        }

        var now = clock.GetUtcNow();
        BlobRow row;
        if (!scan.Clean)
        {
            row = new BlobRow { AccountId = accountId, Sha256 = sha256, ByteSize = upload.ByteSize, State = BlobStates.Blocked, Detail = scan.Signature, StorageKey = string.Empty, WrappedKey = [], CreatedAt = now };
            log.LogWarning("A blob was blocked by the malware check ({Signature}) for account {AccountId}", scan.Signature, accountId);
        }
        else
        {
            var dataKey = BlobCrypto.NewDataKey();
            // Every completion encrypts with its own data key into its own object, so two completions of the same blob
            // (a client retry) can never overwrite each other's bytes; the one whose row lands is the one that stays readable.
            var key = ObjectKey(accountId, sha256);
            var temp = Path.Combine(Path.GetTempPath(), "chaos-blob-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var sealedFile = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
                {
                    await using (var plain = new JoinedParts(store, accountId, sha256, upload.PartCount))
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

            // The quota is checked and the bytes counted in one statement, so two uploads cannot both squeeze under it.
            var counted = await db.Accounts.Where(a => a.Id == accountId && a.StorageBytes + upload.ByteSize <= limits.Value.AccountQuotaBytes)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.StorageBytes, a => a.StorageBytes + upload.ByteSize), ct);
            if (counted == 0)
            {
                await store.DeleteAsync(key, ct);
                await AuditQuotaAsync(accountId, deviceId, ct);
                return BlobOutcome<BlobRow>.Fail(BlobFailure.Quota);
            }

            row = new BlobRow { AccountId = accountId, Sha256 = sha256, ByteSize = upload.ByteSize, State = BlobStates.Clean, StorageKey = key, WrappedKey = keys.Wrap(dataKey), CreatedAt = now };
        }

        db.Blobs.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel completion stored it first. Take back what this one counted and drop its own object.
            db.ChangeTracker.Clear();
            if (row.State == BlobStates.Clean)
            {
                await store.DeleteAsync(row.StorageKey, ct);
                await db.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.StorageBytes, a => a.StorageBytes - upload.ByteSize), ct);
            }

            return BlobOutcome<BlobRow>.Ok((await FindAsync(accountId, sha256, ct))!);
        }

        await DiscardUploadAsync(accountId, sha256, ct);
        return BlobOutcome<BlobRow>.Ok(row);
    }

    /// <summary>Writes the plaintext range of a clean blob. <paramref name="endInclusive"/> is clamped by the caller.</summary>
    public async Task WriteRangeAsync(BlobRow blob, long start, long endInclusive, Stream destination, CancellationToken ct)
    {
        var dataKey = keys.Unwrap(blob.WrappedKey);
        await BlobCrypto.DecryptRangeAsync(offset => store.OpenReadAsync(blob.StorageKey, offset, ct), dataKey, blob.ByteSize, start, endInclusive, destination, ct);
    }

    private async Task DiscardUploadAsync(Guid accountId, string sha256, CancellationToken ct)
    {
        await db.BlobUploads.Where(u => u.AccountId == accountId && u.Sha256 == sha256).ExecuteDeleteAsync(ct);
        await store.DeletePrefixAsync($"parts/{accountId:N}/{sha256}/", ct);
    }

    private async Task AuditQuotaAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        db.AuditEvents.Add(new AuditEvent { AccountId = accountId, Kind = AuditKinds.Quota, At = clock.GetUtcNow(), DeviceId = deviceId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes every object the account has: its blobs and any parts of unfinished uploads. Called when the account is deleted (PLAN2 §8.6).</summary>
    public async Task DeleteAccountObjectsAsync(Guid accountId, CancellationToken ct)
    {
        await store.DeletePrefixAsync(AccountPrefix(accountId), ct);
        await store.DeletePrefixAsync(AccountPartsPrefix(accountId), ct);
    }

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
            .Select(b => new { b.AccountId, b.Sha256, b.StorageKey, b.ByteSize, b.State }).ToListAsync(ct);
        foreach (var blob in expired)
        {
            // The row goes first: a failure in between leaves an orphaned object, which is harmless, not a row pointing at nothing.
            var removed = await db.Blobs.Where(b => b.AccountId == blob.AccountId && b.Sha256 == blob.Sha256).ExecuteDeleteAsync(ct);
            if (removed == 1 && blob.State == BlobStates.Clean)
            {
                await db.Accounts.Where(a => a.Id == blob.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.StorageBytes, a => a.StorageBytes - blob.ByteSize), ct);
            }

            if (removed == 1 && blob.StorageKey.Length > 0)
            {
                await store.DeleteAsync(blob.StorageKey, ct);
            }
        }

        var stale = now - Options.UploadExpiry;
        var uploads = await db.BlobUploads.AsNoTracking().Where(u => u.UpdatedAt <= stale).Select(u => new { u.AccountId, u.Sha256 }).ToListAsync(ct);
        foreach (var upload in uploads)
        {
            await DiscardUploadAsync(upload.AccountId, upload.Sha256, ct);
        }

        return (expired.Count, uploads.Count);
    }

    /// <summary>True for a blob some live (not purged, not deleted) attachment of the account names.</summary>
    private const string ReferencedSql = """
        EXISTS (
            SELECT 1 FROM "SyncEntities" e
            WHERE e."StreamId" = b."AccountId" AND e."Kind" = 'attachment' AND e."PurgedAt" IS NULL
              AND e."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND e."Groups" -> 'file' -> 'values' ->> 'Sha256' = b."Sha256")
        """;

    /// <summary>The parts of one upload read as a single stream, one part open at a time.</summary>
    private sealed class JoinedParts(IBlobStore store, Guid accountId, string sha256, int partCount) : Stream
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

                    _current = await store.OpenReadAsync(PartKey(accountId, sha256, _next++), 0, cancellationToken)
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
