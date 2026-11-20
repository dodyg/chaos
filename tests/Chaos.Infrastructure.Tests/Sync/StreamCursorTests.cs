using Chaos.Infrastructure.Sqlite;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

public sealed class StreamCursorTests
{
    [Test]
    public async Task ApplyAsync_StreamCursor_AdvancesOnlyThatStreamAndNeverRegresses()
    {
        using var host = await DataHost.StartAsync();
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO SyncStreams (StreamId, Kind, Cursor, State) VALUES ('a','account',3,'active'),('b','space',7,'joining');";
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var applier = new SyncApplier(host.Store, host.Clock, host.Ids);
        await applier.ApplyAsync([], throughSequence: 12, streamId: "b");
        await applier.ApplyAsync([], throughSequence: 4, streamId: "b");
        await Assert.That(await CountAsync(host, "SELECT Cursor FROM SyncStreams WHERE StreamId='a';")).IsEqualTo(3L);
        await Assert.That(await CountAsync(host, "SELECT Cursor FROM SyncStreams WHERE StreamId='b';")).IsEqualTo(12L);
        await Assert.That(await CountAsync(host, "SELECT Cursor FROM SyncState WHERE Id=1;")).IsEqualTo(0L);
    }

    [Test]
    public async Task ApplyAsync_MissingStream_RollsBackTheWholeBatch()
    {
        using var host = await DataHost.StartAsync();
        var applier = new SyncApplier(host.Store, host.Clock, host.Ids);
        await Assert.That(async () => await applier.ApplyAsync(["{"], throughSequence: 3, streamId: "missing")).Throws<AppException>();
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM SyncAttention;")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, "SELECT Applying FROM SyncApplyState WHERE Id=1;")).IsEqualTo(0L);
    }
}
