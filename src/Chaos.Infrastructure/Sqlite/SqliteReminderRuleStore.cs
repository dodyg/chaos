using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteReminderRuleStore(SqliteConnection connection) : IReminderRuleStore
{
    public async Task<ReminderRuleRecord?> GetAsync(string id, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM ReminderRules WHERE Id=$value", id, ct)).FirstOrDefault();

    public Task<IReadOnlyList<ReminderRuleRecord>> ListByTaskAsync(string taskId, CancellationToken ct) =>
        ReadAsync("SELECT * FROM ReminderRules WHERE TaskId=$value AND DeletedAtUtc IS NULL ORDER BY Id", taskId, ct);

    public async Task AddAsync(ReminderRuleRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReminderRules (Id,ProfileId,TaskId,Anchor,OffsetMinutes,AbsoluteAtUtc,InsistentMinutes,Enabled,Version,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc)
            VALUES ($id,$profile,$task,$anchor,$offset,$absolute,$insistent,$enabled,$version,$created,$updated,$deleted)
            """;
        Bind(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReminderRuleRecord?> UpdateAsync(ReminderRuleRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ReminderRules SET Anchor=$anchor,OffsetMinutes=$offset,AbsoluteAtUtc=$absolute,InsistentMinutes=$insistent,
            Enabled=$enabled,Version=$version,UpdatedAtUtc=$updated,DeletedAtUtc=$deleted WHERE Id=$id AND Version=$expected
            """;
        Bind(command, row);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row : null;
    }

    private async Task<IReadOnlyList<ReminderRuleRecord>> ReadAsync(string sql, string value, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ReminderRuleRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(new ReminderRuleRecord
        {
            Id = reader.S("Id"), ProfileId = reader.S("ProfileId"), TaskId = reader.S("TaskId"), Anchor = (ReminderAnchor)reader.L("Anchor"),
            OffsetMinutes = reader.Ln("OffsetMinutes") is { } offset ? checked((int)offset) : null,
            AbsoluteAtUtc = reader.In("AbsoluteAtUtc"), InsistentMinutes = reader.Ln("InsistentMinutes") is { } interval ? checked((int)interval) : null,
            Enabled = reader.B("Enabled"), Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"),
            UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
        });
        return rows;
    }

    private static void Bind(SqliteCommand command, ReminderRuleRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$profile", row.ProfileId);
        command.Parameters.AddWithValue("$task", row.TaskId);
        command.Parameters.AddWithValue("$anchor", (int)row.Anchor);
        command.Parameters.AddWithValue("$offset", (object?)row.OffsetMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$absolute", row.AbsoluteAtUtc is { } absolute ? StorageFormats.Instant(absolute) : DBNull.Value);
        command.Parameters.AddWithValue("$insistent", (object?)row.InsistentMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", row.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", row.DeletedAtUtc is { } deleted ? StorageFormats.Instant(deleted) : DBNull.Value);
    }
}
