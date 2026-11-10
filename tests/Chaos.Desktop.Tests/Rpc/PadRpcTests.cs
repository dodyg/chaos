using Chaos.Application.Pad;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// PLAN §15 WP5 exit over RPC: rapid saves (some delivered late) followed by a close and
/// restart recover the latest acknowledged text, and a task made from a selection keeps
/// its backlink across the restart. Failures come back as data, never as exceptions.
/// </summary>
public sealed class PadRpcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class GuidIds : Chaos.Application.Abstractions.IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static (PadRpcService Pad, TaskRpcService Tasks) Wire(ChaosStorageHandle storage)
    {
        var ids = new GuidIds();
        var clock = new FixedTimeProvider(Now);
        return (new PadRpcService(new PadCommands(storage.Store, ids, clock)), new TaskRpcService(new TaskCommands(storage.Store, ids, clock)));
    }

    private static Task<ChaosStorageHandle> OpenAsync(string root) =>
        StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new GuidIds(), new FixedTimeProvider(Now), CancellationToken.None);

    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task RapidSavesCloseRestart_RecoverLatestAcknowledgedTextAndBacklink()
    {
        var root = TestPaths.NewDataRoot();
        string itemId;
        string taskId;
        const string final = "Groceries\n- oat milk 🥛\n- لبن";
        using (var storage = await OpenAsync(root))
        {
            var (pad, tasks) = Wire(storage);
            var created = await pad.CreateAsync(new(Op(), string.Empty), CancellationToken.None);
            itemId = created.Items[0].Id;

            // Saves 1..4 as the user types; 3 arrives after 4 (out of order) and is ignored.
            await pad.SaveAsync(new(itemId, "1", "G"), CancellationToken.None);
            await pad.SaveAsync(new(itemId, "2", "Groceries"), CancellationToken.None);
            var newest = await pad.SaveAsync(new(itemId, "4", final), CancellationToken.None);
            var late = await pad.SaveAsync(new(itemId, "3", "Groceries\n- oat"), CancellationToken.None);
            await Assert.That(newest.Applied).IsTrue();
            await Assert.That(late.Ok).IsTrue();
            await Assert.That(late.Applied).IsFalse();
            await Assert.That(late.Item!.DraftVersion).IsEqualTo("4");

            var start = final.IndexOf("oat milk 🥛", StringComparison.Ordinal);
            var made = await pad.MakeTaskAsync(new(Op(), itemId, "4", start, start + "oat milk 🥛".Length, "oat milk 🥛", "Buy oat milk", null, null, "2026-11-11", null, []), CancellationToken.None);
            await Assert.That(made.Ok).IsTrue();
            taskId = made.Task!.Id;

            var invalid = await pad.SaveAsync(new(itemId, "zero", "x"), CancellationToken.None);
            await Assert.That(invalid.Ok).IsFalse();
            await Assert.That(invalid.Error!.Kind).IsEqualTo("validation");
        }

        using (var reopened = await OpenAsync(root))
        {
            var (pad, tasks) = Wire(reopened);
            var list = await pad.ListAsync(new(false, null), CancellationToken.None);
            var item = list.Items.Single();
            await Assert.That(item.Markdown).IsEqualTo(final);
            await Assert.That(item.DraftVersion).IsEqualTo("4");
            await Assert.That(item.Tasks.Single().TaskId).IsEqualTo(taskId);

            var task = (await tasks.GetAsync(new(taskId), CancellationToken.None)).Task!;
            await Assert.That(task.Title).IsEqualTo("Buy oat milk");
            await Assert.That(task.PlannedDate).IsEqualTo("2026-11-11");
            var anchor = (await pad.LocateAsync(new(taskId), CancellationToken.None)).Anchor!;
            await Assert.That(anchor.State).IsEqualTo("Exact");
            await Assert.That(anchor.PadItemId).IsEqualTo(itemId);
            await Assert.That(final[anchor.Start!.Value..anchor.End!.Value]).IsEqualTo("oat milk 🥛");

            var found = await pad.SearchAsync(new("oat", 10), CancellationToken.None);
            await Assert.That(found.Items.Select(x => x.Id)).Contains(itemId);
        }
    }
}
