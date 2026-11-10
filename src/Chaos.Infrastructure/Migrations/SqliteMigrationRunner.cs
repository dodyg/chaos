using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Migrations;

/// <summary>
/// Numbered embedded SQL migrations (PLAN §5): no runtime auto-create; every applied script's
/// checksum is recorded; altering a previously applied script is refused without mutation;
/// each pending script runs in one transaction. Failures leave the database intact.
/// </summary>
public sealed class SqliteMigrationRunner
{
    /// <summary>Highest schema version this build knows how to open.</summary>
    public const int MaxKnownSchemaVersion = 13;

    /// <param name="targetVersion">Stops after this version; null applies every pending migration. A JSON archive
    /// restore builds its staging database at the archive's version, loads the rows, then migrates the rest (PLAN §14).</param>
    public static async Task<long> MigrateAsync(SqliteConnection connection, CancellationToken ct, long? targetVersion = null)
    {
        EnsureMigrationsTable(connection);

        var applied = ReadAppliedMigrations(connection);
        var currentVersion = applied.Count == 0 ? 0 : applied.Keys.Max();

        if (currentVersion > MaxKnownSchemaVersion)
        {
            throw new AppException(
                AppErrorKind.StorageUnavailable,
                "This database was created by a newer version of Chaos and cannot be opened here. " +
                "Install the newer version, or restore a backup made with this version. " +
                $"Database schema version {currentVersion}; this app supports up to {MaxKnownSchemaVersion}.");
        }

        foreach (var migration in LoadEmbeddedMigrations())
        {
            ct.ThrowIfCancellationRequested();
            if (targetVersion is { } target && migration.Version > target)
            {
                break;
            }

            if (applied.TryGetValue(migration.Version, out var appliedChecksum))
            {
                if (!migration.Matches(appliedChecksum))
                {
                    throw new AppException(
                        AppErrorKind.StorageUnavailable,
                        $"Migration {migration.Version:000} in this app differs from the one applied to this database. " +
                        "The database is left unchanged; open it with the app version that created it or restore a backup.");
                }

                continue;
            }

            Apply(connection, migration);
            currentVersion = migration.Version;
        }

        return currentVersion;
    }

    private static void Apply(SqliteConnection connection, EmbeddedMigration migration)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var statement in SplitStatements(migration.Sql))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO SchemaMigrations (Version, Checksum, AppliedAtUtc) VALUES ($v, $c, $a);";
            insert.Parameters.AddWithValue("$v", migration.Version);
            insert.Parameters.AddWithValue("$c", migration.Checksum);
            insert.Parameters.AddWithValue("$a", StorageFormats.Instant(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void EnsureMigrationsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version     INTEGER NOT NULL PRIMARY KEY,
                Checksum    TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static Dictionary<long, string> ReadAppliedMigrations(SqliteConnection connection)
    {
        var applied = new Dictionary<long, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Checksum FROM SchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            applied.Add(reader.GetInt64(0), reader.GetString(1));
        }

        return applied;
    }

    public static async Task<long> ReadCurrentSchemaVersionUnsafeAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Inspection without mutation (PLAN §5 bootstrap): the SchemaMigrations table may not exist yet.
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SchemaMigrations';";
        var tableExists = Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) > 0;

        if (!tableExists)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public static IReadOnlyList<EmbeddedMigration> LoadEmbeddedMigrations()
    {
        var assembly = typeof(SqliteMigrationRunner).Assembly;
        var migrations = new List<EmbeddedMigration>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("Chaos.Infrastructure.Migrations.", StringComparison.Ordinal) || !name.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(name);
            // Strip the assembly-prefixed namespace path: keep only the last dotted segment ("001_core").
            var lastDot = fileName.LastIndexOf('.');
            var stem = lastDot >= 0 ? fileName[(lastDot + 1)..] : fileName;
            if (stem.Length < 5 || !long.TryParse(stem.AsSpan(0, 3), out var version) || stem[3] != '_')
            {
                throw new InvalidOperationException($"Migration resource '{name}' must be named NNN_description.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Migration resource '{name}' could not be read.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();

            migrations.Add(EmbeddedMigration.From(version, sql));
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }

    /// <summary>Splits a script into statements on semicolons outside line comments.</summary>
    public static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();

        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue; // comment-only line
            }

            var withoutLineComment = StripTrailingLineComment(line);
            current.Append(withoutLineComment).Append('\n');

            if (withoutLineComment.TrimEnd().EndsWith(';', StringComparison.Ordinal))
            {
                var statement = current.ToString().Trim();
                if (statement.Length > 0)
                {
                    statements.Add(statement);
                }

                current.Clear();
            }
        }

        var remainder = current.ToString().Trim();
        if (remainder.Length > 0)
        {
            statements.Add(remainder);
        }

        return statements;
    }

    private static string StripTrailingLineComment(string line)
    {
        var index = line.IndexOf("--", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }
}

/// <summary>
/// One embedded migration script. <see cref="Checksum"/> identifies its content with line
/// endings normalized to LF, so a checkout that converts the file to CRLF (git autocrlf on
/// Windows) is still the same migration. Databases that recorded the raw hash of either the
/// LF or the CRLF form are accepted too; any other difference is real tampering or drift.
/// </summary>
public sealed record EmbeddedMigration(long Version, string Sql, string Checksum, IReadOnlyList<string> AcceptedChecksums)
{
    public static EmbeddedMigration From(long version, string sql)
    {
        var lf = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var canonical = Hash(lf);
        return new EmbeddedMigration(version, sql, canonical, [canonical, Hash(lf.Replace("\n", "\r\n", StringComparison.Ordinal))]);
    }

    public bool Matches(string appliedChecksum) => AcceptedChecksums.Contains(appliedChecksum, StringComparer.Ordinal);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
