using Chaos.Infrastructure.Sync;
using Chaos.Sync;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>The service copies the catalog's kind and group list into Chaos.Sync (PLAN2 P13 item 1); they must not drift.</summary>
public sealed class SyncKindsTests
{
    [Test]
    public async Task SyncKinds_MatchTheClientCatalog()
    {
        var catalog = SyncCatalog.Tables.Where(table => table.Mode != SyncMode.LocalOnly).OrderBy(table => table.ApplyOrder).ToList();
        await Assert.That(SyncKinds.All.Select(kind => kind.Kind)).IsEquivalentTo(catalog.Select(table => table.Kind));
        foreach (var table in catalog)
        {
            var kind = SyncKinds.Find(table.Kind)!;
            await Assert.That(kind.KeyParts).IsEqualTo(table.Key.Count);
            await Assert.That(kind.ApplyOrder).IsEqualTo(table.ApplyOrder);
            await Assert.That(kind.Groups.Select(group => group.Name)).IsEquivalentTo(table.Groups.Select(group => group.Name));
            await Assert.That(kind.HasSpace).IsEqualTo(table.Groups.Any(group => group.Name == "create" && group.Columns.Contains("SpaceId")));
        }
    }
}
