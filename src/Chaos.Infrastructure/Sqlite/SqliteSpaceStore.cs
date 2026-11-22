using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteSpaceStore(SqliteConnection connection) : ISpaceStore
{
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
        DefaultDeadlineReminderJson = r.Sn("DefaultDeadlineReminderJson"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
