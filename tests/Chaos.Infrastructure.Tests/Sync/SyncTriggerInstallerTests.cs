using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// Change capture (PLAN2 §7.3): startup installs a trigger triple per locally writable synced table, edits pend
/// their changed field groups, deletes pend a purge, and a catalog change (or a file without
/// the triggers, like a restored archive) recreates them — an unchanged catalog does no work.
/// </summary>
public sealed class SyncTriggerInstallerTests
{
    [Test]
    public async Task Startup_installs_a_trigger_triple_for_every_locally_writable_synced_table()
    {
        using var host = await DataHost.StartAsync();

        var expected = SyncCatalog.Tables.Count(table => table.Mode != SyncMode.LocalOnly && SyncKinds.Find(table.Kind)!.Route.Kind != SyncRouteKind.ServerOnly) * 3;
        var installed = await CountAsync(host, "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'TR_Sync_%';");
        var hash = await CountAsync(host, $"SELECT count(*) FROM SyncState WHERE TriggerHash = '{SyncTriggerInstaller.CatalogHash()}';");

        await Assert.That(installed).IsEqualTo((long)expected);
        await Assert.That(hash).IsEqualTo(1L);
    }

    [Test]
    public async Task An_edit_pends_its_changed_groups_and_a_delete_pends_a_purge()
    {
        using var host = await DataHost.StartAsync();
        await SyncTestHelpers.EnableSyncAsync(host);
        var task = await CaptureTaskAsync(host);

        // The flush consumes SyncPending at commit, so the groups the triggers pended are
        // observed through the outbox change each write produced. A capture pends every
        // group; a details edit pends exactly the groups it wrote.
        var afterCapture = (await SyncTestHelpers.ChangesAsync(host, "task", task.Id))[^1];
        var expectedGroups = SyncCatalog.Find("Tasks")!.Groups.Select(group => group.Name).Order(StringComparer.Ordinal).ToList();
        await Assert.That(afterCapture.Op).IsEqualTo("upsert");
        await Assert.That(afterCapture.Groups.Order(StringComparer.Ordinal)).IsEquivalentTo(expectedGroups);

        await host.Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, task.Title, task.Notes,
            new DateOnly(2026, 11, 20), null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        var planned = (await SyncTestHelpers.ChangesAsync(host, "task", task.Id))[^1];
        await Assert.That(planned.Op).IsEqualTo("upsert");
        await Assert.That(planned.Groups).IsEquivalentTo(new[] { "planned" });

        // A soft delete travels as the deleted group; a purge (the real hard-delete path) as
        // __purge, captured by the delete trigger on the physical row removal.
        var current = (await host.Tasks.GetAsync(task.Id))!;
        await host.Tasks.DeleteAsync(new(Op(), task.Id, current.Version));
        var deleted = (await SyncTestHelpers.ChangesAsync(host, "task", task.Id))[^1];
        await Assert.That(deleted.Op).IsEqualTo("upsert");
        await Assert.That(deleted.Groups).IsEquivalentTo(new[] { "deleted" });

        await host.Purge.PurgeAsync(task.Id, CancellationToken.None);
        var purged = (await SyncTestHelpers.ChangesAsync(host, "task", task.Id))[^1];
        await Assert.That(purged.Op).IsEqualTo("purge");
    }

    [Test]
    public async Task A_stale_hash_or_missing_trigger_is_recreated_on_the_next_start()
    {
        var root = TestPaths.NewDataRoot();
        var first = await DataHost.StartAsync(root);
        first.Dispose();

        // Someone (or a restored file) lost the triggers and the hash no longer matches.
        using (var connection = OpenRoot(root))
        {
            using var stale = connection.CreateCommand();
            stale.CommandText = "UPDATE SyncState SET TriggerHash = 'stale' WHERE Id = 1;";
            stale.ExecuteNonQuery();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER TR_Sync_Tasks_Update;";
            drop.ExecuteNonQuery();
        }

        using var second = await DataHost.StartAsync(root);
        var trigger = await CountAsync(second, "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'TR_Sync_Tasks_Update';");
        var hash = await CountAsync(second, $"SELECT count(*) FROM SyncState WHERE TriggerHash = '{SyncTriggerInstaller.CatalogHash()}';");
        await Assert.That(trigger).IsEqualTo(1L);
        await Assert.That(hash).IsEqualTo(1L);
    }

    [Test]
    public async Task An_unchanged_catalog_does_no_work()
    {
        using var host = await DataHost.StartAsync();
        using var connection = OpenRoot(host.Root);

        await Assert.That(SyncTriggerInstaller.Install(connection)).IsFalse();

        using var stale = connection.CreateCommand();
        stale.CommandText = "UPDATE SyncState SET TriggerHash = 'stale' WHERE Id = 1;";
        stale.ExecuteNonQuery();
        await Assert.That(SyncTriggerInstaller.Install(connection)).IsTrue();
    }

    private static SqliteConnection OpenRoot(string root)
    {
        var databasePath = Path.Combine(root, "data", "chaos.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
