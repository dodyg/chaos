using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Standups (migration 047).</summary>
public sealed class SqliteSpaceUpdateStore(SqliteConnection connection) : ISpaceUpdateStore
{
    public Task<SpaceUpdateRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) =>
        ReadOneAsync("SELECT * FROM SpaceUpdates WHERE Id=$id", id, ct);

    public Task<IReadOnlyList<SpaceUpdateRecord>> ListAsync(string spaceId, CancellationToken ct) =>
        ReadAsync("SELECT * FROM SpaceUpdates WHERE SpaceId=$id AND DeletedAtUtc IS NULL ORDER BY CreatedAtUtc DESC, Id DESC", spaceId, ct);

    public async Task AddAsync(SpaceUpdateRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SpaceUpdates (Id,SpaceId,AuthorMemberId,ForDate,RawMarkdown,EditedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$space,$author,$for,$markdown,$edited,$version,$created,$updated,$deleted)
            """;
        Bind(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SpaceUpdateRecord?> UpdateAsync(SpaceUpdateRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SpaceUpdates SET RawMarkdown=$markdown,EditedAtUtc=$edited,Version=$version,UpdatedAtUtc=$updated,DeletedAtUtc=$deleted
            WHERE Id=$id AND Version=$expected
            """;
        Bind(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    private async Task<SpaceUpdateRecord?> ReadOneAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return (await ReadRowsAsync(command, ct)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<SpaceUpdateRecord>> ReadAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return await ReadRowsAsync(command, ct);
    }

    private static async Task<IReadOnlyList<SpaceUpdateRecord>> ReadRowsAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SpaceUpdateRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SpaceUpdateRecord
            {
                Id = reader.S("Id"), SpaceId = reader.S("SpaceId"), AuthorMemberId = reader.S("AuthorMemberId"),
                ForDate = StorageFormats.ParseDate(reader.S("ForDate")) ?? throw new InvalidOperationException("Column 'ForDate' holds an invalid date."),
                RawMarkdown = reader.S("RawMarkdown"), EditedAtUtc = reader.In("EditedAtUtc"),
                Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
            });
        }

        return rows;
    }

    private static void Bind(SqliteCommand command, SpaceUpdateRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$space", row.SpaceId);
        command.Parameters.AddWithValue("$author", row.AuthorMemberId);
        command.Parameters.AddWithValue("$for", StorageFormats.Date(row.ForDate));
        command.Parameters.AddWithValue("$markdown", row.RawMarkdown);
        command.Parameters.AddWithValue("$edited", SqliteProfileStore.OptionalInstant(row.EditedAtUtc));
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(row.DeletedAtUtc));
    }
}
