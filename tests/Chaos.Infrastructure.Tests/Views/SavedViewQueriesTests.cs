using Chaos.Application.Queries;
using Chaos.Application.Views;
using Chaos.Infrastructure.Tests.DataProtection;

namespace Chaos.Infrastructure.Tests.Views;

/// <summary>Running, counting and exporting saved views against real SQLite (PLAN2 P3).</summary>
public sealed class SavedViewQueriesTests
{
    private const string OpenDefinition = "{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"status\",\"op\":\"notIn\",\"value\":[\"Done\",\"Cancelled\"]}]}";

    [Test]
    public async Task Counts_PinnedViewCountEqualsItsRows_AndUnpinnedViewsHaveNoCount()
    {
        using var host = await DataHost.StartAsync();
        var views = new SavedViewCommands(host.Store, host.Ids, host.Clock);
        var queries = new SavedViewQueries(host.Store, new TaskQueries(host.Store, host.Clock), host.Clock);
        await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "One", null));
        await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Two", null));
        var pinned = (await views.SaveAsync(new(DataHost.Op(), null, 0, "Open", "list", null, OpenDefinition, null, Pinned: true, AlertsEnabled: false))).View;
        var loose = (await views.SaveAsync(new(DataHost.Op(), null, 0, "Loose", "list", null, OpenDefinition, null, Pinned: false, AlertsEnabled: false))).View;

        var counts = await queries.CountsAsync(null);
        var run = await queries.RunAsync(pinned.Id, null, 100, null);

        await Assert.That(counts[pinned.Id]).IsEqualTo(run.Page.TotalCount);
        await Assert.That(counts[pinned.Id]).IsEqualTo(2L);
        await Assert.That(counts.ContainsKey(loose.Id)).IsFalse();
    }

    [Test]
    public async Task BuildCsv_ExportsTheLayoutColumns_AndNeutralizesFormulaTitles()
    {
        using var host = await DataHost.StartAsync();
        var views = new SavedViewCommands(host.Store, host.Ids, host.Clock);
        var queries = new SavedViewQueries(host.Store, new TaskQueries(host.Store, host.Clock), host.Clock);
        await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "=HYPERLINK(\"http://example.test\",\"x\")", null));
        await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Plain, with comma", null));
        var layout = "{\"v\":1,\"kind\":\"table\",\"sort\":{\"field\":\"title\",\"dir\":\"asc\"},\"columns\":[\"title\",\"status\"]}";
        var view = (await views.SaveAsync(new(DataHost.Op(), null, 0, "Export me", "list", null, OpenDefinition, layout, false, false))).View;

        var (csv, fileName, rows) = await queries.BuildCsvAsync(view.Id, null);

        await Assert.That(rows).IsEqualTo(2);
        await Assert.That(fileName).StartsWith("Export me ");
        var expected = "Title,Status\r\n"
            + "\"'=HYPERLINK(\"\"http://example.test\"\",\"\"x\"\")\",NotStarted\r\n"
            + "\"Plain, with comma\",NotStarted\r\n";
        await Assert.That(csv).IsEqualTo(expected);
    }

    [Test]
    public async Task Run_DeletedView_IsNotFound()
    {
        using var host = await DataHost.StartAsync();
        var views = new SavedViewCommands(host.Store, host.Ids, host.Clock);
        var queries = new SavedViewQueries(host.Store, new TaskQueries(host.Store, host.Clock), host.Clock);
        var view = (await views.SaveAsync(new(DataHost.Op(), null, 0, "Gone", "list", null, OpenDefinition, null, true, false))).View;
        await views.DeleteAsync(DataHost.Op(), view.Id, view.Version);

        var error = await Assert.That(async () => await queries.RunAsync(view.Id, null, 100, null)).Throws<Chaos.Application.Abstractions.AppException>();

        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.NotFound);
    }
}
