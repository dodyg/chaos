using Chaos.Application.Abstractions;
using Chaos.Application.Transfer;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>ImportBatches/ImportedItems (migration 012) on the caller's connection and transaction.</summary>
public sealed class SqliteImportStore(SqliteConnection connection) : IImportStore
{
    public async Task<string?> FindTaskForFingerprintAsync(string itemFingerprint, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TaskId FROM ImportedItems WHERE ItemFingerprint = $fp;";
        command.Parameters.AddWithValue("$fp", itemFingerprint);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task AddBatchAsync(ImportBatchRecord batch, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ImportBatches (Id, ProfileId, SourceKind, SourceName, FileFingerprint, ImportedAtUtc, CreatedCount, SkippedCount, BackupFileName)
            VALUES ($id, $profile, $kind, $name, $fp, $at, $created, $skipped, $backup);
            """;
        command.Parameters.AddWithValue("$id", batch.Id);
        command.Parameters.AddWithValue("$profile", batch.ProfileId);
        command.Parameters.AddWithValue("$kind", (long)batch.SourceKind);
        command.Parameters.AddWithValue("$name", batch.SourceName);
        command.Parameters.AddWithValue("$fp", batch.FileFingerprint);
        command.Parameters.AddWithValue("$at", StorageFormats.Instant(batch.ImportedAtUtc));
        command.Parameters.AddWithValue("$created", (long)batch.CreatedCount);
        command.Parameters.AddWithValue("$skipped", (long)batch.SkippedCount);
        command.Parameters.AddWithValue("$backup", (object?)batch.BackupFileName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MapItemAsync(string itemFingerprint, string batchId, string taskId, string sourceKey, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ImportedItems (ItemFingerprint, BatchId, TaskId, SourceKey) VALUES ($fp, $batch, $task, $key)
            ON CONFLICT (ItemFingerprint) DO UPDATE SET BatchId = excluded.BatchId, TaskId = excluded.TaskId, SourceKey = excluded.SourceKey;
            """;
        command.Parameters.AddWithValue("$fp", itemFingerprint);
        command.Parameters.AddWithValue("$batch", batchId);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$key", sourceKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ImportBatchRecord>> ListBatchesAsync(int limit, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ImportBatches ORDER BY ImportedAtUtc DESC, Id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", (long)limit);
        var rows = new List<ImportBatchRecord>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ImportBatchRecord(
                reader.GetString(reader.GetOrdinal("Id")),
                reader.GetString(reader.GetOrdinal("ProfileId")),
                (ImportSourceKind)reader.GetInt32(reader.GetOrdinal("SourceKind")),
                reader.GetString(reader.GetOrdinal("SourceName")),
                reader.GetString(reader.GetOrdinal("FileFingerprint")),
                StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("ImportedAtUtc")))!.Value,
                reader.GetInt32(reader.GetOrdinal("CreatedCount")),
                reader.GetInt32(reader.GetOrdinal("SkippedCount")),
                reader.IsDBNull(reader.GetOrdinal("BackupFileName")) ? null : reader.GetString(reader.GetOrdinal("BackupFileName"))));
        }

        return rows;
    }
}
