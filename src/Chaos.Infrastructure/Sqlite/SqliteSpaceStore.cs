using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteSpaceStore(SqliteConnection connection) : ISpaceStore
{
    public async Task SetTagsOwnerOnlyAsync(string spaceId, bool ownerOnly, DateTimeOffset now, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Spaces SET TagsOwnerOnly=$value, Version=Version+1, UpdatedAtUtc=$now WHERE Id=$id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$value", ownerOnly ? 1L : 0L);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
        command.Parameters.AddWithValue("$id", spaceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetAgentSettingsAsync(string spaceId, bool? agentsAllowed, int? claimMinutes, bool? allowAgentsToClose, DateTimeOffset now, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Spaces SET AgentsAllowed=COALESCE($allowed, AgentsAllowed), ClaimMinutes=COALESCE($minutes, ClaimMinutes),
                AllowAgentsToClose=COALESCE($close, AllowAgentsToClose), Version=Version+1, UpdatedAtUtc=$now
            WHERE Id=$id AND DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$allowed", agentsAllowed is { } allowed ? (object)(allowed ? 1L : 0L) : DBNull.Value);
        command.Parameters.AddWithValue("$minutes", claimMinutes is { } minutes ? (object)(long)minutes : DBNull.Value);
        command.Parameters.AddWithValue("$close", allowAgentsToClose is { } close ? (object)(close ? 1L : 0L) : DBNull.Value);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
        command.Parameters.AddWithValue("$id", spaceId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SpaceStateRecord>> ListStatesAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SpaceStates WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SpaceStateRecord>();
        while (await reader.ReadAsync(ct))
            result.Add(ReadState(reader));
        return result;
    }

    public async Task<SpaceStateRecord?> GetStateAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SpaceStates WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadState(reader) : null;
    }

    private static SpaceStateRecord ReadState(SqliteDataReader reader) => new() { Id = reader.S("Id"), SpaceId = reader.S("SpaceId"), Name = reader.S("Name"), NormalizedName = reader.S("NormalizedName"), BaseStatus = (TaskItemStatus)reader.L("BaseStatus"), Position = reader.L("Position"), Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc") };

    public async Task<SpaceStateRecord?> UpdateStateAsync(SpaceStateRecord state, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SpaceStates SET Name=$name, NormalizedName=$normalized, BaseStatus=$base, Position=$position, Version=$version, UpdatedAtUtc=$updated, DeletedAtUtc=$deleted WHERE Id=$id AND Version=$expected;";
        command.Parameters.AddWithValue("$id", state.Id);
        command.Parameters.AddWithValue("$name", state.Name);
        command.Parameters.AddWithValue("$normalized", state.NormalizedName);
        command.Parameters.AddWithValue("$base", (long)state.BaseStatus);
        command.Parameters.AddWithValue("$position", state.Position);
        command.Parameters.AddWithValue("$version", state.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(state.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(state.DeletedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? await GetStateAsync(state.Id, ct) : null;
    }

    public async Task<IReadOnlyList<TaskRecord>> ListTasksInStateAsync(string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Tasks WHERE CustomStateId=$id ORDER BY Id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<TaskRecord>();
        while (await reader.ReadAsync(ct)) result.Add(SqliteTaskStore.ReadTask(reader));
        return result;
    }

    public async Task AddStateAsync(SpaceStateRecord state, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO SpaceStates (Id, SpaceId, Name, NormalizedName, BaseStatus, Position, Version, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $space, $name, $normalized, $base, $position, $version, $created, $updated);";
        command.Parameters.AddWithValue("$id", state.Id);
        command.Parameters.AddWithValue("$space", state.SpaceId);
        command.Parameters.AddWithValue("$name", state.Name);
        command.Parameters.AddWithValue("$normalized", state.NormalizedName);
        command.Parameters.AddWithValue("$base", (long)state.BaseStatus);
        command.Parameters.AddWithValue("$position", state.Position);
        command.Parameters.AddWithValue("$version", state.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(state.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(state.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SpaceRecord?> GetAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Spaces WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSpace(reader) : null;
    }

    public async Task<SpaceRecord?> GetPersonalByOwnerAsync(string ownerProfileId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Spaces WHERE OwnerProfileId = $owner AND Kind = 1 AND DeletedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$owner", ownerProfileId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSpace(reader) : null;
    }

    public async Task AddAsync(SpaceRecord space, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $owner, $kind, $name, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", space.Id);
        command.Parameters.AddWithValue("$owner", space.OwnerProfileId);
        command.Parameters.AddWithValue("$kind", (long)space.Kind);
        command.Parameters.AddWithValue("$name", space.Name);
        command.Parameters.AddWithValue("$version", space.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(space.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(space.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(space.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SpaceRecord>> ListAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Spaces WHERE DeletedAtUtc IS NULL ORDER BY Kind, Name COLLATE NOCASE, Id;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var spaces = new List<SpaceRecord>();
        while (await reader.ReadAsync(ct))
        {
            spaces.Add(ReadSpace(reader));
        }

        return spaces;
    }

    internal static SpaceRecord ReadSpace(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        OwnerProfileId = r.S("OwnerProfileId"),
        Kind = (SpaceKind)(int)r.L("Kind"),
        Name = r.S("Name"),
        TagsOwnerOnly = r.L("TagsOwnerOnly") != 0,
        AgentsAllowed = r.L("AgentsAllowed") != 0,
        ClaimMinutes = (int)r.L("ClaimMinutes"),
        AllowAgentsToClose = r.L("AllowAgentsToClose") != 0,
        DefaultDeadlineReminderJson = r.Sn("DefaultDeadlineReminderJson"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
