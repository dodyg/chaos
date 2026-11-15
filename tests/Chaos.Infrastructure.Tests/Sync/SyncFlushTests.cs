using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Sync;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// The flush (PLAN2 §7.3): runs inside the write transaction just before commit. Disabled
/// sync only clears the pending rows; enabled sync writes one outbox change per entity with
/// exactly the changed groups, fresh clocks in SyncClocks, LastHlc on SyncState, and a purge
/// for entities removed in the same transaction. A rolled-back write produces no outbox row.
/// </summary>
public sealed class SyncFlushTests
{
    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<TaskRecord> CaptureTaskAsync(DataHost host, string title = "Flushed thing") =>
        (await host.Tasks.CaptureAsync(new(Op(), string.Empty, title, null))).Task;

    private static Task EnableSync(DataHost host) => SyncTestHelpers.EnableSyncAsync(host);

    private static async Task<long> CountAsync(DataHost host, string sql) => await host.CountAsync(sql);

    private static async Task<JsonElement> OutboxPayloadAsync(DataHost host, string kind, string? op = null)
    {
        var filter = op is null ? "" : $" AND Op = '{op}'";
        var payload = await SyncTestHelpers.TextAsync(host, $"SELECT PayloadJson FROM SyncOutbox WHERE Kind = '{kind}'{filter} ORDER BY Seq LIMIT 1;");
        return JsonDocument.Parse(payload).RootElement;
    }

    private static async Task<IReadOnlyList<string>> OutboxGroupsAsync(DataHost host, string kind)
    {
        var payload = await OutboxPayloadAsync(host, kind);
        return [.. payload.GetProperty("groups").EnumerateObject().Select(property => property.Name)];
    }

    [Test]
    public async Task Disabled_sync_leaves_the_pending_rows_cleared_after_commit()
    {
        using var host = await DataHost.StartAsync();
        await CaptureTaskAsync(host);

        var pending = await CountAsync(host, "SELECT count(*) FROM SyncPending;");
        var outbox = await CountAsync(host, "SELECT count(*) FROM SyncOutbox;");
        await Assert.That(pending).IsEqualTo(0L);
        await Assert.That(outbox).IsEqualTo(0L);
    }

    [Test]
    public async Task A_rolled_back_write_produces_no_outbox_row_and_no_pending()
    {
        using var host = await DataHost.StartAsync();
        await EnableSync(host);
        var task = await CaptureTaskAsync(host);
        var outboxBefore = await CountAsync(host, "SELECT count(*) FROM SyncOutbox;");

        // A failed optimistic-concurrency write rolls the whole transaction back; nothing was
        // captured and nothing reached the outbox.
        await Assert.That(async () => await host.Tasks.UpdateDetailsAsync(new(
            Op(), task.Id, task.Version + 100, task.Title, task.Notes, null, null, null, null, null, null,
            TaskPriority.Normal, null, CapacityCategory.Personal))).Throws<AppException>();

        var outbox = await CountAsync(host, "SELECT count(*) FROM SyncOutbox;");
        var tasks = await CountAsync(host, "SELECT count(*) FROM SyncPending WHERE Kind = 'task';");
        await Assert.That(outbox).IsEqualTo(outboxBefore);
        await Assert.That(tasks).IsEqualTo(0L);
    }

    [Test]
    public async Task Enabled_sync_writes_one_outbox_change_per_entity_with_the_changed_groups()
    {
        using var host = await DataHost.StartAsync();
        await EnableSync(host);
        var task = await CaptureTaskAsync(host);

        var outboxRows = await CountAsync(host, $"SELECT count(*) FROM SyncOutbox WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND Op = 'upsert';");
        await Assert.That(outboxRows).IsEqualTo(1L);

        // The insert pends every group of the catalog entry, and the payload carries exactly those.
        var expected = SyncCatalog.Find("Tasks")!.Groups.Select(group => group.Name).Order(StringComparer.Ordinal).ToList();
        var actual = (await OutboxGroupsAsync(host, "task")).Order(StringComparer.Ordinal).ToList();
        await Assert.That(actual).IsEquivalentTo(expected);

        // Each group's values carry the row's current values and the change's clock.
        var payload = await OutboxPayloadAsync(host, "task");
        var title = payload.GetProperty("groups").GetProperty("title").GetProperty("values").GetProperty("Title").GetString();
        await Assert.That(title).IsEqualTo("Flushed thing");
        var clockText = payload.GetProperty("groups").GetProperty("title").GetProperty("hlc").GetString()!;
        await Assert.That(Hlc.TryParse(clockText, out _)).IsTrue();

        // SyncClocks holds the same clock per group of this entity. The transaction also flushed
        // the task's history entry, which ticked the clock once more: LastHlc is at least ours.
        var clocks = await CountAsync(host, $"SELECT count(*) FROM SyncClocks WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND Hlc = '{clockText}';");
        var latest = Hlc.Parse(await SyncTestHelpers.TextAsync(host, "SELECT LastHlc FROM SyncState WHERE Id = 1;"));
        await Assert.That(clocks).IsEqualTo((long)expected.Count);
        await Assert.That(latest >= Hlc.Parse(clockText)).IsTrue();

        // The pending rows were consumed, and the history entry the capture recorded has its
        // own change (keyed by the history row's id).
        var pending = await CountAsync(host, "SELECT count(*) FROM SyncPending;");
        var history = await CountAsync(host, "SELECT count(*) FROM SyncOutbox WHERE Kind = 'task_history';");
        await Assert.That(history >= 1L).IsTrue();
        await Assert.That(pending).IsEqualTo(0L);
    }

    [Test]
    public async Task Successive_writes_tick_the_clock_forward()
    {
        using var host = await DataHost.StartAsync();
        await EnableSync(host);
        await CaptureTaskAsync(host, "first");
        var first = await SyncTestHelpers.TextAsync(host, "SELECT LastHlc FROM SyncState WHERE Id = 1;");
        await CaptureTaskAsync(host, "second");
        var second = await SyncTestHelpers.TextAsync(host, "SELECT LastHlc FROM SyncState WHERE Id = 1;");

        await Assert.That(Hlc.Parse(second) > Hlc.Parse(first)).IsTrue();
    }

    [Test]
    public async Task A_purge_in_the_same_transaction_travels_as_a_purge_and_resets_the_clocks()
    {
        using var host = await DataHost.StartAsync();
        await EnableSync(host);
        var task = await CaptureTaskAsync(host);
        var current = (await host.Tasks.GetAsync(task.Id))!;
        await host.Tasks.DeleteAsync(new(Op(), task.Id, current.Version));
        await host.Purge.PurgeAsync(task.Id, CancellationToken.None);

        var rows = await CountAsync(host, $"SELECT count(*) FROM SyncOutbox WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND Op = 'purge';");
        var clocks = await CountAsync(host, $"SELECT count(*) FROM SyncClocks WHERE Kind = 'task' AND EntityKey = '{task.Id}';");
        await Assert.That(rows).IsEqualTo(1L);
        await Assert.That(clocks).IsEqualTo(0L);

        var payload = await OutboxPayloadAsync(host, "task", op: "purge");
        await Assert.That(payload.GetProperty("op").GetString()).IsEqualTo("purge");
    }
}
