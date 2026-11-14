using Chaos.Application.Pages;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>Pages over RPC: saves survive a restart, daily pages appear on first write, and failures come back as data.</summary>
public sealed class PageRpcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private sealed class GuidIds : Chaos.Application.Abstractions.IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static PageRpcService Wire(ChaosStorageHandle storage) =>
        new(new PageCommands(storage.Store, new GuidIds(), new FixedTimeProvider(Now)));

    private static Task<ChaosStorageHandle> OpenAsync(string root) =>
        StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new GuidIds(), new FixedTimeProvider(Now), CancellationToken.None);

    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task SaveCloseRestart_KeepsTitleTextAndDailyPage()
    {
        var root = TestPaths.NewDataRoot();
        string pageId;
        using (var storage = await OpenAsync(root))
        {
            var pages = Wire(storage);
            var created = await pages.CreateAsync(new(Op(), null, "Kitchen", ""), CancellationToken.None);
            pageId = created.Pages[0].Id;
            await pages.SaveAsync(new(pageId, "2", "Kitchen renovation", "---\nicon: 🛠️\n---\nTiles"), CancellationToken.None);
            var stale = await pages.SaveAsync(new(pageId, "1", "Old", "old"), CancellationToken.None);
            await Assert.That(stale.Applied).IsFalse();

            var none = await pages.GetDailyAsync(new("2026-11-14"), CancellationToken.None);
            await Assert.That(none.Ok).IsTrue();
            await Assert.That(none.Page).IsNull();
            await pages.CreateDailyAsync(new(Op(), "2026-11-14", "Today went well."), CancellationToken.None);
        }

        using (var reopened = await OpenAsync(root))
        {
            var pages = Wire(reopened);
            var page = (await pages.GetAsync(new(pageId), CancellationToken.None)).Page!;
            await Assert.That(page.Title).IsEqualTo("Kitchen renovation");
            await Assert.That(page.Icon).IsEqualTo("🛠️");
            await Assert.That(page.DraftVersion).IsEqualTo("2");
            var tree = await pages.ListAsync(new(), CancellationToken.None);
            await Assert.That(tree.Pages.Single().Icon).IsEqualTo("🛠️");
            var daily = await pages.ListDailyAsync(new(null, null), CancellationToken.None);
            await Assert.That(daily.Pages.Single().DailyDate).IsEqualTo("2026-11-14");
        }
    }

    [Test]
    public async Task Failures_ComeBackAsData()
    {
        using var storage = await OpenAsync(TestPaths.NewDataRoot());
        var pages = Wire(storage);
        var blank = await pages.CreateAsync(new(Op(), null, "  ", ""), CancellationToken.None);
        await Assert.That(blank.Ok).IsFalse();
        await Assert.That(blank.Error!.Fields.Single().FieldPath).IsEqualTo("title");

        var badDate = await pages.GetDailyAsync(new("14/11/2026"), CancellationToken.None);
        await Assert.That(badDate.Ok).IsFalse();
        var missing = await pages.GetAsync(new(Guid.NewGuid().ToString()), CancellationToken.None);
        await Assert.That(missing.Ok).IsFalse();
    }
}
