using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteContainerStore(SqliteConnection connection) : IContainerStore
{
    public async Task<ContainerRecord?> GetAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadContainer(reader) : null;
    }

    public async Task<ContainerRecord?> GetInboxAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE SpaceId = $space AND IsInbox = 1 AND DeletedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadContainer(reader) : null;
    }

    public async Task<IReadOnlyList<ContainerRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var containers = new List<ContainerRecord>();
        while (await reader.ReadAsync(ct))
        {
            containers.Add(ReadContainer(reader));
        }

        return containers;
    }

    public async Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Containers WHERE SpaceId = $space AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$space", spaceId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<long> CountActiveTasksAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Tasks WHERE ContainerId = $container AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$container", containerId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<ContainerRecord?> UpdateAsync(ContainerRecord updated, long expectedVersion, CancellationToken ct)
    {
        // Optimistic concurrency (PLAN §5): the version check alone decides; a tombstoned
        // row stays addressable so restore-style flows can rewrite it.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Containers SET
                Name = $name, NormalizedName = $normalizedName, Position = $position,
                DeadlineDate = $deadline, ArchivedAtUtc = $archived, DefaultCategory = $defaultCategory,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$name", updated.Name);
        command.Parameters.AddWithValue("$normalizedName", NormalizeName(updated.Name));
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$deadline", SqliteProfileStore.OptionalDate(updated.DeadlineDate));
        command.Parameters.AddWithValue("$archived", SqliteProfileStore.OptionalInstant(updated.ArchivedAtUtc));
        command.Parameters.AddWithValue("$defaultCategory", updated.DefaultCategory is { } updatedCategory ? (long)updatedCategory : DBNull.Value);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    public async Task<ContainerRecord?> GetIncludingDeletedAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadContainer(reader) : null;
    }

    public async Task<IReadOnlyList<ContainerRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Containers WHERE SpaceId = $space AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ContainerRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadContainer(reader));
        return rows;
    }

    public async Task AddAsync(ContainerRecord container, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, IsInbox, Position,
                DeadlineDate, ArchivedAtUtc, DefaultCategory, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $kind, $name, $normalizedName, $isInbox, $position,
                $deadline, $archived, $defaultCategory, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", container.Id);
        command.Parameters.AddWithValue("$space", container.SpaceId);
        command.Parameters.AddWithValue("$kind", (long)container.Kind);
        command.Parameters.AddWithValue("$name", container.Name);
        command.Parameters.AddWithValue("$normalizedName", NormalizeName(container.Name));
        command.Parameters.AddWithValue("$isInbox", container.IsInbox ? 1L : 0L);
        command.Parameters.AddWithValue("$position", container.Position);
        command.Parameters.AddWithValue("$deadline", SqliteProfileStore.OptionalDate(container.DeadlineDate));
        command.Parameters.AddWithValue("$archived", SqliteProfileStore.OptionalInstant(container.ArchivedAtUtc));
        command.Parameters.AddWithValue("$defaultCategory", container.DefaultCategory is { } category ? (long)category : DBNull.Value);
        command.Parameters.AddWithValue("$version", container.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(container.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(container.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(container.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    internal static ContainerRecord ReadContainer(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        Kind = (ContainerKind)(int)r.L("Kind"),
        Name = r.S("Name"),
        IsInbox = r.B("IsInbox"),
        Position = r.L("Position"),
        DeadlineDate = r.Dn("DeadlineDate"),
        ArchivedAtUtc = r.In("ArchivedAtUtc"),
        DefaultCategory = r.Ln("DefaultCategory") is { } defaultCategory ? (CapacityCategory)(int)defaultCategory : null,
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
