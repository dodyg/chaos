using System.Text;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Tests;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class StreamMigrationTests(PostgresServer postgres)
{
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
