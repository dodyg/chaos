using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>Records joins and departures in <c>SyncStreams</c> and runs the sync engine for <see cref="Chaos.Application.Spaces.SpaceCommands"/>.</summary>
public sealed class SyncSpaceStreams(SqliteApplicationStore store, SyncEngine engine) : ISpaceStreams
{
    public Task JoinAsync(Guid spaceId, int role, CancellationToken ct)
    {
        if (spaceId == Guid.Empty || role is < 1 or > 3)
        {
            throw AppException.Validation("spaceId", "The service returned an invalid space.");
        }

        return store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            // A space still being evicted keeps that state; the next discovery joins it again after the eviction finishes.
            command.CommandText = """
                INSERT INTO SyncStreams (StreamId, Kind, SpaceId, Role, Cursor, State) VALUES ($id, 'space', $id, $role, 0, 'joining')
                ON CONFLICT(StreamId) DO UPDATE SET Role = excluded.Role WHERE SyncStreams.State <> 'evicting';
                """;
            command.Parameters.AddWithValue("$id", spaceId.ToString("D"));
            command.Parameters.AddWithValue("$role", role);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
    }

    public async Task LeaveAsync(Guid spaceId, CancellationToken ct)
    {
        await store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE SyncStreams SET State = 'evicting' WHERE StreamId = $id AND Kind = 'space';";
            command.Parameters.AddWithValue("$id", spaceId.ToString("D"));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
        engine.RequestDiscovery();
    }

    public async Task<string?> SyncAsync(CancellationToken ct) => (await engine.SyncNowAsync(ct)).Problem;
}
