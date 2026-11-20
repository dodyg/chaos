using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

public sealed class SyncStreamRegistryTests
{
    [Test]
    public async Task ReconcileAsync_MembershipChanges_PreservesCursorsAndMarksMissingForEviction()
    {
        using var host = await DataHost.StartAsync();
        var account = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE SyncState SET AccountId=$account, Cursor=6 WHERE Id=1; INSERT INTO SyncStreams (StreamId,Kind,SpaceId,Role,Cursor,State) VALUES ($space,'space',$space,1,19,'active');";
            command.Parameters.AddWithValue("$account", account.ToString("D"));
            command.Parameters.AddWithValue("$space", first.ToString("D"));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var registry = new SyncStreamRegistry(host.Store);
        await registry.ReconcileAsync(account, [new(account, "account", null, null), new(first, "space", first, 2), new(second, "space", second, 3)]);
        await Assert.That(await CountAsync(host, $"SELECT Cursor FROM SyncStreams WHERE StreamId='{account:D}';")).IsEqualTo(6L);
        await Assert.That(await CountAsync(host, $"SELECT Cursor FROM SyncStreams WHERE StreamId='{first:D}';")).IsEqualTo(19L);
        await Assert.That(await CountAsync(host, $"SELECT Role FROM SyncStreams WHERE StreamId='{first:D}';")).IsEqualTo(2L);
        await Assert.That(await TextAsync(host, $"SELECT State FROM SyncStreams WHERE StreamId='{second:D}';")).IsEqualTo("joining");
        await registry.ReconcileAsync(account, [new(account, "account", null, null), new(second, "space", second, 3)]);
        await Assert.That(await TextAsync(host, $"SELECT State FROM SyncStreams WHERE StreamId='{first:D}';")).IsEqualTo("evicting");
        await registry.ReconcileAsync(account, [new(account, "account", null, null), new(first, "space", first, 1), new(second, "space", second, 3)]);
        await Assert.That(await TextAsync(host, $"SELECT State FROM SyncStreams WHERE StreamId='{first:D}';")).IsEqualTo("evicting");
    }

    [Test]
    public async Task ReconcileAsync_InvalidList_DoesNotChangeExistingState()
    {
        using var host = await DataHost.StartAsync();
        var account = Guid.NewGuid();
        var registry = new SyncStreamRegistry(host.Store);
        await Assert.That(async () => await registry.ReconcileAsync(account, [])).Throws<AppException>();
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM SyncStreams;")).IsEqualTo(0L);
    }

    [Test]
    public async Task ReconcileAsync_AccountMismatch_DoesNotChangeExistingState()
    {
        using var host = await DataHost.StartAsync();
        var account = Guid.NewGuid();
        var registry = new SyncStreamRegistry(host.Store);
        await Assert.That(async () => await registry.ReconcileAsync(account, [new(account, "account", null, null)])).Throws<AppException>();
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM SyncStreams;")).IsEqualTo(0L);
    }
}
