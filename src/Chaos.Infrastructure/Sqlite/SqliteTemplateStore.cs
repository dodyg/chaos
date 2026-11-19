using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteTemplateStore(SqliteConnection connection) : ITemplateStore
{
    public async Task<IReadOnlyList<TemplateRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE SpaceId = $space AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<TemplateRecord>();
        while (await reader.ReadAsync(ct)) results.Add(ReadTemplate(reader));
        return results;
    }
    public async Task<TemplateRecord?> GetIncludingDeletedAsync(string templateId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", templateId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemplate(reader) : null;
    }
    public async Task<TemplateRecord?> GetAsync(string templateId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", templateId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemplate(reader) : null;
    }

    public async Task<TemplateRecord?> FindByNameAsync(string spaceId, string normalizedName, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE SpaceId = $space AND NormalizedName = $name AND DeletedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$name", normalizedName);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTemplate(reader) : null;
    }

    public async Task<IReadOnlyList<TemplateRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TaskTemplates WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY NormalizedName, Id;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var templates = new List<TemplateRecord>();
        while (await reader.ReadAsync(ct))
        {
            templates.Add(ReadTemplate(reader));
        }

        return templates;
    }

    public async Task AddAsync(TemplateRecord template, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskTemplates (Id, SpaceId, Name, NormalizedName, Payload, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $name, $normalizedName, $payload, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", template.Id);
        command.Parameters.AddWithValue("$space", template.SpaceId);
        command.Parameters.AddWithValue("$name", template.Name);
        command.Parameters.AddWithValue("$normalizedName", template.NormalizedName);
        command.Parameters.AddWithValue("$payload", template.PayloadJson);
        command.Parameters.AddWithValue("$version", template.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(template.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(template.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(template.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<TemplateRecord?> UpdateAsync(TemplateRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TaskTemplates SET
                Name = $name, NormalizedName = $normalizedName, Payload = $payload,
                Version = $newVersion, UpdatedAtUtc = $updated_at, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$name", updated.Name);
        command.Parameters.AddWithValue("$normalizedName", updated.NormalizedName);
        command.Parameters.AddWithValue("$payload", updated.PayloadJson);
        command.Parameters.AddWithValue("$newVersion", updated.Version);
        command.Parameters.AddWithValue("$updated_at", StorageFormats.Instant(updated.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(updated.DeletedAtUtc));
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);

        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows == 1 ? await GetIncludingDeletedAsync(updated.Id, ct) : null;
    }

    internal static TemplateRecord ReadTemplate(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        SpaceId = r.S("SpaceId"),
        Name = r.S("Name"),
        NormalizedName = r.S("NormalizedName"),
        PayloadJson = r.S("Payload"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };
}
