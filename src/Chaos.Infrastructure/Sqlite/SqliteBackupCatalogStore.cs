using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteBackupCatalogStore(SqliteConnection connection) : IBackupCatalogStore
{
    public async Task AddAsync(BackupCatalogRecord entry, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BackupCatalog (Id, FileName, SchemaVersion, AppVersion, CreatedAtUtc, ChecksumSha256, ValidationStatus, Reason)
            VALUES ($id, $file, $schemaVersion, $appVersion, $created, $checksum, $status, $reason);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$file", entry.FileName);
        command.Parameters.AddWithValue("$schemaVersion", entry.SchemaVersion);
        command.Parameters.AddWithValue("$appVersion", entry.AppVersion);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(entry.CreatedAtUtc));
        command.Parameters.AddWithValue("$checksum", entry.ChecksumSha256);
        command.Parameters.AddWithValue("$status", FormatStatus(entry.ValidationStatus));
        command.Parameters.AddWithValue("$reason", FormatReason(entry.Reason));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<BackupCatalogRecord?> FindByFileNameAsync(string fileName, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM BackupCatalog WHERE FileName = $file;";
        command.Parameters.AddWithValue("$file", fileName);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    internal static BackupCatalogRecord ReadEntry(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        FileName = r.S("FileName"),
        SchemaVersion = r.L("SchemaVersion"),
        AppVersion = r.S("AppVersion"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        ChecksumSha256 = r.S("ChecksumSha256"),
        ValidationStatus = ParseStatus(r.S("ValidationStatus")),
        Reason = ParseReason(r.S("Reason")),
    };

    // Stored tokens match the CHECK constraints in migration 001 (lowercase kebab-case).
    internal static string FormatStatus(BackupValidationStatus status) => status switch
    {
        BackupValidationStatus.Valid => "valid",
        BackupValidationStatus.Unvalidated => "unvalidated",
        BackupValidationStatus.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    internal static BackupValidationStatus ParseStatus(string text) => text switch
    {
        "valid" => BackupValidationStatus.Valid,
        "unvalidated" => BackupValidationStatus.Unvalidated,
        "invalid" => BackupValidationStatus.Invalid,
        _ => throw new InvalidOperationException($"BackupCatalog.ValidationStatus holds an unknown token '{text}'."),
    };

    internal static string FormatReason(BackupReason reason) => reason switch
    {
        BackupReason.PreMigration => "pre-migration",
        BackupReason.PreImport => "pre-import",
        BackupReason.PreRestore => "pre-restore",
        BackupReason.Manual => "manual",
        BackupReason.Scheduled => "scheduled",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    internal static BackupReason ParseReason(string text) => text switch
    {
        "pre-migration" => BackupReason.PreMigration,
        "pre-import" => BackupReason.PreImport,
        "pre-restore" => BackupReason.PreRestore,
        "manual" => BackupReason.Manual,
        "scheduled" => BackupReason.Scheduled,
        _ => throw new InvalidOperationException($"BackupCatalog.Reason holds an unknown token '{text}'."),
    };
}
