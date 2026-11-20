using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

public sealed class SyncEvictionTests
{
    private static Task ExecuteAsync(DataHost host, string sql, params (string Name, object Value)[] parameters) =>
        host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });

    /// <summary>A shared space holding one task that also sits in the person's My Day, with unsent changes.</summary>
    private static async Task<(string Space, string Shared, string Personal)> SeedAsync(DataHost host)
    {
        await EnableSyncAsync(host);
        var personal = await CaptureTaskAsync(host, "Stays personal");
        var shared = await CaptureTaskAsync(host, "Goes with the space");
        var space = Guid.NewGuid().ToString("D");
        await ExecuteAsync(host, """
            UPDATE SyncApplyState SET Applying = 1 WHERE Id = 1;
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc) VALUES ($space, $owner, 2, 'Team', 'now', 'now');
            INSERT INTO SpaceMembers (Id, SpaceId, MemberId, MemberKind, DisplayName, Role, JoinedAtUtc) VALUES ($member, $space, $owner, 1, 'Owner', 1, 'now');
            UPDATE Tasks SET SpaceId = $space WHERE Id = $task;
            INSERT INTO SyncStreams (StreamId, Kind, SpaceId, Role, Cursor, State) VALUES ($space, 'space', $space, 2, 4, 'active');
            INSERT INTO DayPlans (Id, ProfileId, LocalDate, InitializedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
                SELECT $plan, Id, '2026-11-20', 'now', 1, 'now', 'now' FROM Profiles LIMIT 1;
            INSERT INTO DayPlanItems (Id, DayPlanId, Kind, TaskId, Position, Origin, AddedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($item, $plan, 0, $task, 0, 0, 'now', 1, 'now', 'now');
            UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;
            """,
            ("$space", space), ("$owner", Guid.NewGuid().ToString("D")), ("$member", Guid.NewGuid().ToString("D")),
            ("$task", shared.Id), ("$plan", Guid.NewGuid().ToString("D")), ("$item", Guid.NewGuid().ToString("D")));
        return (space, shared.Id, personal.Id);
    }

    [Test]
    public async Task EvictPendingAsync_RemovesTheSpaceButKeepsPersonalDataAndOverlays()
    {
        using var host = await DataHost.StartAsync();
        var (space, shared, personal) = await SeedAsync(host);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncOutbox WHERE Kind='task' AND EntityKey='{shared}';")).IsGreaterThan(0L);
        var outboxBefore = await CountAsync(host, "SELECT count(*) FROM SyncOutbox;");
        await ExecuteAsync(host, "UPDATE SyncStreams SET State='evicting' WHERE StreamId=$id;", ("$id", space));

        var results = await new SyncEviction(host.Store, host.Clock).EvictPendingAsync();

        await Assert.That(results.Count).IsEqualTo(1);
        // The task's own changes and its history's changes: everything that would have routed to the space.
        var unsent = results[0].DiscardedChanges;
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM SyncOutbox;")).IsEqualTo(outboxBefore - unsent);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncOutbox WHERE Kind='task_history';")).IsEqualTo(
            await CountAsync(host, $"SELECT count(*) FROM SyncOutbox o JOIN TaskHistory h ON h.Id = o.EntityKey WHERE h.TaskId='{personal}';"));
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Spaces WHERE Id='{space}';")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SpaceMembers WHERE SpaceId='{space}';")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Tasks WHERE Id='{shared}';")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncClocks WHERE Kind='task' AND EntityKey='{shared}';")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncOutbox WHERE EntityKey='{shared}';")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncStreams WHERE StreamId='{space}';")).IsEqualTo(0L);
        // Personal work and the overlay stay: the server tombstones overlays for every device of the account.
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Tasks WHERE Id='{personal}';")).IsEqualTo(1L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM DayPlanItems WHERE TaskId='{shared}';")).IsEqualTo(1L);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncOutbox WHERE EntityKey='{personal}';")).IsGreaterThan(0L);
        await Assert.That(await TextAsync(host, "SELECT Message FROM SyncAttention WHERE Category='sync.evicted';"))
            .IsEqualTo($"You no longer have access to Team. {unsent} unsent {(unsent == 1 ? "change was" : "changes were")} discarded.");
        // Nothing the eviction deleted was captured as a local change.
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM SyncPending WHERE EntityKey='{shared}';")).IsEqualTo(0L);
    }

    [Test]
    public async Task EvictPendingAsync_AfterACrashLeftTheStreamEvicting_FinishesAtStartup()
    {
        using var host = await DataHost.StartAsync();
        var (space, shared, _) = await SeedAsync(host);
        // A crash after discovery marked the stream but before the eviction committed leaves only the mark.
        await ExecuteAsync(host, "UPDATE SyncStreams SET State='evicting' WHERE StreamId=$id;", ("$id", space));
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Tasks WHERE Id='{shared}';")).IsEqualTo(1L);

        var eviction = new SyncEviction(host.Store, host.Clock);
        await eviction.EvictPendingAsync();
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Tasks WHERE Id='{shared}';")).IsEqualTo(0L);
        await Assert.That((await eviction.EvictPendingAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task EvictAsync_ActiveStream_IsRefused()
    {
        using var host = await DataHost.StartAsync();
        var (space, shared, _) = await SeedAsync(host);
        await Assert.That(async () => await new SyncEviction(host.Store, host.Clock).EvictAsync(space))
            .Throws<Chaos.Application.Abstractions.AppException>();
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Tasks WHERE Id='{shared}';")).IsEqualTo(1L);
    }
}
