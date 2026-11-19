using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteSectionStore(SqliteConnection connection) : ISectionStore
{
    public async Task<SectionRecord?> GetAsync(string sectionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", sectionId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSection(reader) : null;
    }

    public async Task<IReadOnlyList<SectionRecord>> ListByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE ContainerId = $container AND DeletedAtUtc IS NULL ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var sections = new List<SectionRecord>();
        while (await reader.ReadAsync(ct))
        {
            sections.Add(ReadSection(reader));
        }

        return sections;
    }

    public async Task AddAsync(SectionRecord section, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sections (Id, ContainerId, Title, Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $container, $title, $position, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", section.Id);
        command.Parameters.AddWithValue("$container", section.ContainerId);
        command.Parameters.AddWithValue("$title", section.Title);
        command.Parameters.AddWithValue("$position", section.Position);
        command.Parameters.AddWithValue("$version", section.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(section.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(section.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(section.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SectionRecord?> UpdateAsync(SectionRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sections SET
                Title = $title, Position = $position,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$title", updated.Title);
        command.Parameters.AddWithValue("$position", updated.Position);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    public async Task<long> GetNextPositionAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Sections WHERE ContainerId = $container AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$container", containerId);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value);
    }

    public async Task<SectionRecord?> GetIncludingDeletedAsync(string sectionId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", sectionId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSection(reader) : null;
    }

    public async Task<IReadOnlyList<SectionRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Sections WHERE ContainerId = $container AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$container", containerId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SectionRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadSection(reader));
        return rows;
    }

    internal static SectionRecord ReadSection(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        ContainerId = r.S("ContainerId"),
        Title = r.S("Title"),
        Position = r.L("Position"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
