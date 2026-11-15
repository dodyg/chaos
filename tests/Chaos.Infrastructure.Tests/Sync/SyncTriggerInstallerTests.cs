using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// Change capture (PLAN2 §7.3): startup installs a trigger triple per synced table, edits pend
/// their changed field groups, deletes pend a purge, and a catalog change (or a file without
/// the triggers, like a restored archive) recreates them — an unchanged catalog does no work.
/// </summary>
public sealed class SyncTriggerInstallerTests
{
    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<long> CountAsync(DataHost host, string sql) => await host.CountAsync(sql);

    private static async Task<TaskRecord> CaptureTaskAsync(DataHost host, string title = "Trigger thing") =>
        (await host.Tasks.CaptureAsync(new(Op(), string.Empty, title, null))).Task;

    [Test]
    public async Task Startup_installs_a_trigger_triple_for_every_synced_table()
    {
        using var host = await DataHost.StartAsync();

        var expected = SyncCatalog.Tables.Count(table => table.Mode != SyncMode.LocalOnly) * 3;
        var installed = await CountAsync(host, "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'TR_Sync_%';");
        var hash = await CountAsync(host, $"SELECT count(*) FROM SyncState WHERE TriggerHash = '{SyncTriggerInstaller.CatalogHash()}';");

        await Assert.That(installed).IsEqualTo((long)expected);
        await Assert.That(hash).IsEqualTo(1L);
    }

    [Test]
    public async Task An_edit_pends_its_changed_groups_and_a_delete_pends_a_purge()
    {
        using var host = await DataHost.StartAsync();
        var task = await CaptureTaskAsync(host);

        // A capture pends the groups it wrote; the planned date changes only with its command.
        var afterCapture = await CountAsync(host, $"SELECT count(*) FROM SyncPending WHERE Kind = 'task' AND EntityKey = '{task.Id}';");
        await Assert.That(afterCapture > 0).IsTrue();

        await host.Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, task.Title, task.Notes,
            new DateOnly(2026, 11, 20), null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        var planned = await CountAsync(host, $"SELECT count(*) FROM SyncPending WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND GroupName = 'planned';");
        await Assert.That(planned).IsEqualTo(1L);

        // A soft delete travels as the deleted group; a purge (the real hard-delete path) as
        // __purge, captured by the delete trigger on the physical row removal.
        var current = (await host.Tasks.GetAsync(task.Id))!;
        await host.Tasks.DeleteAsync(new(Op(), task.Id, current.Version));
        var deleted = await CountAsync(host, $"SELECT count(*) FROM SyncPending WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND GroupName = 'deleted';");
        await Assert.That(deleted).IsEqualTo(1L);

        await host.Purge.PurgeAsync(task.Id, CancellationToken.None);
        var purge = await CountAsync(host, $"SELECT count(*) FROM SyncPending WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND GroupName = '__purge';");
        await Assert.That(purge).IsEqualTo(1L);
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
