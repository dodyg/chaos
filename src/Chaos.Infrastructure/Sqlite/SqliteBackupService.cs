using System.Security.Cryptography;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Minimal validated backup primitive (PLAN §5, WP1): VACUUM INTO produces a self-contained
/// copy, the copy is verified with integrity_check, and its SHA-256 checksum is recorded in
/// the BackupCatalog in the same step. Scheduled retention, restore and transfer arrive with
/// work package 12. SQL runs synchronously; the async surface is the application boundary.
/// </summary>
public sealed class SqliteBackupService(SqliteConnectionSource source)
{
    /// <summary>Creates, validates and catalogs one backup. Throws on any failure; a failed attempt leaves no catalog entry.</summary>
    public async Task<BackupCatalogRecord> CreateBackupAsync(
        string backupsDirectory,
        string appVersion,
        long schemaVersion,
        BackupReason reason,
        DateTimeOffset utcNow,
        CancellationToken ct)
    {
        Directory.CreateDirectory(backupsDirectory);
        var fileName = $"chaos-{utcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.db";
        var destinationPath = Path.Combine(backupsDirectory, fileName);

        await using (var connection = source.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", destinationPath);
            command.ExecuteNonQuery();
        }

        var validation = ValidateBackupFile(destinationPath);
        var record = new BackupCatalogRecord
        {
            Id = Guid.NewGuid().ToString(),
            FileName = fileName,
            SchemaVersion = schemaVersion,
            AppVersion = appVersion,
            CreatedAtUtc = utcNow,
            ChecksumSha256 = ComputeChecksum(destinationPath),
            ValidationStatus = validation,
            Reason = reason,
        };

        await using (var catalogConnection = source.Open())
        {
            await new SqliteBackupCatalogStore(catalogConnection).AddAsync(record, ct);
        }

        return record;
    }

    /// <summary>Opens a backup copy read-only and checks integrity; never mutates the file.</summary>
    public static BackupValidationStatus ValidateBackupFile(string backupPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            return check.ExecuteScalar() as string == "ok"
                ? BackupValidationStatus.Valid
                : BackupValidationStatus.Invalid;
        }
        catch (SqliteException)
        {
            return BackupValidationStatus.Invalid;
        }
    }

    internal static string ComputeChecksum(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
