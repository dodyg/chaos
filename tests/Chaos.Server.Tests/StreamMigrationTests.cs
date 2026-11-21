using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Chaos.Server.Auth;
using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chaos.Server.Tests;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class StreamMigrationTests(PostgresServer postgres)
{
    [Test]
    public async Task Migrate_SeededS008_BackfillsSharedReferencesFromRecordedCustody()
    {
        await using var db = ServerDatabase.Open(await postgres.CreateDatabaseAsync());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("S008_StreamUploadSessions", StringComparison.Ordinal)));
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var space = Guid.NewGuid();
        var attachment = Guid.NewGuid().ToString("D");
        var hash = new string('a', 64);
        var privateHash = new string('b', 64);
        var groups = JsonSerializer.Serialize(new { file = new { values = new { Sha256 = hash } } });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Accounts" ("Id", "PrimaryEmail", "CreatedAt") VALUES
                ({owner}, 'blob-upgrade-owner@example.invalid', now()), ({other}, 'blob-upgrade-other@example.invalid', now());
            INSERT INTO "SyncStreams" ("Id", "Kind", "AccountId", "CreatedAt") VALUES ({owner}, 1, {owner}, now()), ({other}, 1, {other}, now());
            INSERT INTO "SyncStreams" ("Id", "Kind", "SpaceId", "CreatedAt") VALUES ({space}, 2, {space}, now());
            INSERT INTO "Blobs" ("AccountId", "Sha256", "ByteSize", "State", "StorageKey", "WrappedKey", "CreatedAt") VALUES
                ({owner}, {hash}, 4, 'Clean', 'original-key', decode('00', 'hex'), now()),
                ({other}, {privateHash}, 4, 'Clean', 'private-key', decode('01', 'hex'), now());
            INSERT INTO "SyncEntities" ("StreamId", "Kind", "EntityKey", "Groups", "LastSeq", "UpdatedAt")
                VALUES ({space}, 'attachment', {attachment}, CAST({groups} AS jsonb), 1, now());
            INSERT INTO "AttachmentBlobBindings" ("StreamId", "AttachmentId", "Sha256", "CustodianAccountId")
                VALUES ({space}, {attachment}, {hash}, {owner});
            """);

        await migrator.MigrateAsync();

        var blobs = await db.Blobs.AsNoTracking().ToListAsync();
        var personal = blobs.Single(b => b.StreamId == owner && b.Sha256 == hash);
        var shared = blobs.Single(b => b.StreamId == space && b.Sha256 == hash);
        await Assert.That(shared.ObjectId).IsEqualTo(personal.ObjectId);
        await Assert.That(shared.StorageKey).IsEqualTo(personal.StorageKey);
        await Assert.That(blobs.Any(b => b.StreamId == space && b.Sha256 == privateHash)).IsFalse();
        await Assert.That(await db.BlobObjects.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task Migrate_SeededS007_PreservesUnfinishedAccountUploadsAndParts()
    {
        await using var db = ServerDatabase.Open(await postgres.CreateDatabaseAsync());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("S007_AttachmentBlobBindings", StringComparison.Ordinal)));
        var account = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("oldparts");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(Path.GetTempPath(), "chaos-s008-" + Guid.NewGuid().ToString("N"));
        try
        {
        var options = Options.Create(new BlobOptions { Directory = directory, PartSize = 4 });
        var store = new FileBlobStore(options);
        await store.PutAsync($"parts/{account:N}/{hash}/0", new MemoryStream(bytes, 0, 4, writable: false), CancellationToken.None);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Accounts" ("Id", "PrimaryEmail", "CreatedAt") VALUES ({account}, 'upload-upgrade@example.invalid', now());
            INSERT INTO "SyncStreams" ("Id", "Kind", "AccountId", "CreatedAt") VALUES ({account}, 1, {account}, now());
            INSERT INTO "BlobUploads" ("AccountId", "Sha256", "ByteSize", "PartCount", "CreatedAt", "UpdatedAt")
            VALUES ({account}, {hash}, 8, 2, now(), now());
            INSERT INTO "BlobParts" ("AccountId", "Sha256", "PartIndex", "ByteSize") VALUES ({account}, {hash}, 0, 4);
            """);

        await migrator.MigrateAsync();

        var upload = await db.BlobUploads.SingleAsync();
        var part = await db.BlobParts.SingleAsync();
        await Assert.That(upload.StreamId).IsEqualTo(account);
        await Assert.That(upload.AccountId).IsEqualTo(account);
        await Assert.That(upload.PartCount).IsEqualTo(2);
        await Assert.That(part.StreamId).IsEqualTo(account);
        await Assert.That(part.PartIndex).IsEqualTo(0);
        await Assert.That(part.ByteSize).IsEqualTo(4);
        var service = new BlobService(db, store, new EicarScanner(), new FileKeyWrapper(Path.Combine(directory, "master.key"), createIfMissing: true),
            options, Options.Create(new LimitsOptions()), TimeProvider.System, NullLogger<BlobService>.Instance);
        await Assert.That(await service.PutPartAsync(account, account, hash, 1, new MemoryStream(bytes, 4, 4, writable: false), CancellationToken.None))
            .IsEqualTo(BlobFailure.None);
        var complete = await service.CompleteAsync(account, Guid.NewGuid(), account, hash, null, CancellationToken.None);
        await Assert.That(complete.Succeeded).IsTrue();
        using var output = new MemoryStream();
        await service.WriteRangeAsync(complete.Value!, 0, bytes.Length - 1, output, CancellationToken.None);
        await Assert.That(output.ToArray().AsSpan().SequenceEqual(bytes)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Migrate_SeededS003_PreservesCursorsAndContinuesSequences()
    {
        await using var db = ServerDatabase.Open(await postgres.CreateDatabaseAsync());
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("S003_Blobs", StringComparison.Ordinal)));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var device = Guid.NewGuid();
        var hlc = Hlc.Create(1_800_000_000_000, 0, device).Encode();
        var groups = JsonSerializer.Serialize(new { name = new { hlc, values = new { DisplayName = "before" } } });
        foreach (var (id, email, next, horizon) in new[] { (first, "one@example.invalid", 9L, 4L), (second, "two@example.invalid", 3L, 0L) })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Accounts" ("Id", "PrimaryEmail", "CreatedAt", "NextSeq", "ChangesPrunedThroughSeq")
                VALUES ({id}, {email}, now(), {next}, {horizon});
                INSERT INTO "SyncEntities" ("AccountId", "Kind", "EntityKey", "Groups", "LastSeq", "UpdatedAt")
                VALUES ({id}, 'tag', {id.ToString()}, CAST({groups} AS jsonb), {next - 1}, now());
                INSERT INTO "SyncChanges" ("AccountId", "Seq", "ChangeId", "Kind", "EntityKey", "Op", "Groups", "CreatedAt")
                VALUES ({id}, {next - 1}, {id}, 'tag', {id.ToString()}, 'upsert', CAST({groups} AS jsonb), now());
                INSERT INTO "Blobs" ("AccountId", "Sha256", "ByteSize", "State", "StorageKey", "WrappedKey", "CreatedAt")
                VALUES ({id}, {new string('a', 64)}, 1, 'Clean', 'example', decode('00', 'hex'), now());
                """);
        }

        await migrator.MigrateAsync();
        var service = new SyncService(db, TimeProvider.System, Options.Create(new SyncOptions()), new SyncBroadcaster(),
            new SyncKindCatalog(), new SpaceMembershipCache());
        var beforeHorizon = await service.PullAsync(first, 3, 3, 100, CancellationToken.None);
        await Assert.That(JsonDocument.Parse(beforeHorizon.Json).RootElement.GetProperty("snapshotRequired").GetBoolean()).IsTrue();
        foreach (var (id, cursor, expected) in new[] { (first, 4L, 8L), (second, 0L, 2L) })
        {
            using var page = JsonDocument.Parse((await service.PullAsync(id, 3, cursor, 100, CancellationToken.None)).Json);
            await Assert.That(page.RootElement.GetProperty("next").GetInt64()).IsEqualTo(expected);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { changes = new[] { new { changeId = Guid.NewGuid(), kind = "tag", key = Guid.NewGuid().ToString(), op = "upsert", groups = new { name = new { hlc, values = new { DisplayName = "after" } } } } } });
            var pushed = await service.PushAsync(id, device, 3, SyncService.ParsePush(payload), CancellationToken.None);
            await Assert.That(pushed.LastSeq).IsEqualTo(expected + 1);
            using var continued = JsonDocument.Parse((await service.PullAsync(id, 3, expected, 100, CancellationToken.None)).Json);
            await Assert.That(continued.RootElement.GetProperty("next").GetInt64()).IsEqualTo(expected + 1);
        }
        await Assert.That(await db.Blobs.CountAsync()).IsEqualTo(2);
        await Assert.That(await db.SyncEntities.CountAsync()).IsEqualTo(4);
    }
}
