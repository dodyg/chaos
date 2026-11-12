using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Migrations;

/// <summary>
/// Migration runner behavior on real file-backed databases (PLAN §16): idempotent
/// re-runs, checksum tamper refusal, newer-version refusal, statement splitting.
/// </summary>
public sealed class SqliteMigrationRunnerTests
{
    [Test]
    public async Task RecurrenceOccurrences_SecondActiveSlot_IsRejected()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            INSERT INTO Profiles (Id, DisplayName, ZoneId, Locale, DateFormat, FirstWeekday, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('profile', 'Person', 'UTC', 'en', 'YMD', 1, 'now', 'now');
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('space', 'profile', 1, 'Personal', 'now', 'now');
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, Position, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('container', 'space', 0, 'Inbox', 'inbox', 0, 'now', 'now');
            INSERT INTO RecurrenceSeries (Id, SpaceId, ContainerId, RuleJson, TemplateJson, Revision, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('series', 'space', 'container', '{}', '{}', 1, 1, 'now', 'now');
            INSERT INTO RecurrenceOccurrences (Id, SeriesId, NominalKey, ScheduleKey, EffectiveLocal, State, SeriesRevision, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('first', 'series', '2026-01-01', '2026-01-01', '2026-01-01', 0, 1, 1, 'now', 'now');
            """;
        await setup.ExecuteNonQueryAsync();
        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = """
            INSERT INTO RecurrenceOccurrences (Id, SeriesId, NominalKey, ScheduleKey, EffectiveLocal, State, SeriesRevision, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('second', 'series', '2026-01-02', '2026-01-02', '2026-01-02', 0, 1, 1, 'now', 'now');
            """;
        await Assert.That(async () => await duplicate.ExecuteNonQueryAsync()).Throws<SqliteException>();
    }

    private static SqliteConnection NewConnection(string dataRoot)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "chaos.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    [Test]
    public async Task MigrateAsync_FreshDatabase_CreatesSchemaAndRecordsChecksum()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());

        var version = await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);

        await Assert.That(version).IsEqualTo(15L);
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT count(*) FROM SchemaMigrations;";
        await Assert.That(Convert.ToInt64(await rows.ExecuteScalarAsync())).IsEqualTo(15L);
        using var checksum = connection.CreateCommand();
        checksum.CommandText = "SELECT Checksum FROM SchemaMigrations WHERE Version = 1;";
        var storedChecksum = await checksum.ExecuteScalarAsync() as string;
        await Assert.That(storedChecksum).IsNotNull();
        await Assert.That(storedChecksum!.Length).IsEqualTo(64);
        // Core tables exist (spot check across the 001 schema map).
        foreach (var table in new[] { "Profiles", "Spaces", "Containers", "Tasks", "TaskHistory", "OperationReceipts", "BackupCatalog", "TaskTemplates", "SearchDocuments", "SearchIndex", "SearchIndexState", "ScratchPadTags", "RecurrenceSeries", "RecurrenceOccurrences", "RecurrenceExceptions" })
        {
            using var probe = connection.CreateCommand();
            probe.CommandText = $"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';";
            await Assert.That(Convert.ToInt64(await probe.ExecuteScalarAsync())).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task MigrateAsync_SecondRunOnSameDatabase_IsIdempotent()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);

        var second = await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);

        await Assert.That(second).IsEqualTo(15L);
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT count(*) FROM SchemaMigrations;";
        await Assert.That(Convert.ToInt64(await rows.ExecuteScalarAsync())).IsEqualTo(15L);
    }

    [Test]
    public async Task MigrateAsync_TamperedAppliedChecksum_RefusesWithoutMutation()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText = "UPDATE SchemaMigrations SET Checksum = 'deadbeef' WHERE Version = 1;";
            await tamper.ExecuteNonQueryAsync();
        }

        AppException? caught = null;
        try
        {
            await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("001");
        // The database is left unchanged: checksum still the tampered one, single row.
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT Checksum FROM SchemaMigrations WHERE Version = 1;";
        await Assert.That(await rows.ExecuteScalarAsync() as string).IsEqualTo("deadbeef");
    }

    [Test]
    public async Task MigrateAsync_DatabaseFromNewerAppVersion_IsRefused()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE SchemaMigrations (Version INTEGER NOT NULL PRIMARY KEY, Checksum TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
                INSERT INTO SchemaMigrations (Version, Checksum, AppliedAtUtc) VALUES (99, 'future', '2027-01-01T00:00:00.0000000Z');
                """;
            await create.ExecuteNonQueryAsync();
        }

        AppException? caught = null;
        try
        {
            await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("99");
        // Nothing was applied on top of the newer schema.
        using var rows = connection.CreateCommand();
        rows.CommandText = "SELECT count(*) FROM SchemaMigrations;";
        await Assert.That(Convert.ToInt64(await rows.ExecuteScalarAsync())).IsEqualTo(1L);
    }

    [Test]
    public async Task ReadCurrentSchemaVersionUnsafeAsync_EmptyDatabase_ReturnsZeroWithoutMutating()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());

        var version = await SqliteMigrationRunner.ReadCurrentSchemaVersionUnsafeAsync(connection, CancellationToken.None);

        await Assert.That(version).IsEqualTo(0L);
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT count(*) FROM sqlite_master;";
        await Assert.That(Convert.ToInt64(await tables.ExecuteScalarAsync())).IsEqualTo(0L);
    }

    [Test]
    public async Task SplitStatements_SimpleScript_IgnoresCommentsAndSplitsOnSemicolons()
    {
        var statements = SqliteMigrationRunner.SplitStatements(
            "-- leading comment\nCREATE TABLE sample (Id TEXT NOT NULL);\n\nINSERT INTO sample (Id) VALUES ('a'); -- trailing comment\n");

        await Assert.That(statements.Count).IsEqualTo(2);
        await Assert.That(statements[0]).IsEqualTo("CREATE TABLE sample (Id TEXT NOT NULL);");
        await Assert.That(statements[1]).IsEqualTo("INSERT INTO sample (Id) VALUES ('a');");
    }

    [Test]
    public async Task LoadEmbeddedMigrations_ContainsCoreMigrationWithStableChecksum()
    {
        var migrations = SqliteMigrationRunner.LoadEmbeddedMigrations();

        await Assert.That(migrations.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(migrations[0].Version).IsEqualTo(1L);
        await Assert.That(migrations[0].Checksum.Length).IsEqualTo(64);
        await Assert.That(migrations.Select(migration => migration.Version).SequenceEqual(migrations.Select(migration => migration.Version).OrderBy(version => version))).IsTrue();
        // The recorded checksum is UTF-8 SHA-256 lowercase hex of the embedded SQL with LF line endings.
        await Assert.That(migrations[0].Checksum).IsEqualTo(Sha256(migrations[0].Sql.Replace(Crlf, Lf)));
    }

    private const string Lf = "\n";
    private const string Crlf = "\r\n";

    private static string Sha256(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Regression: git autocrlf rewrote an unchanged migration with CRLF endings, so a database
    /// created by an LF build was refused as "differs". Line endings alone never make a
    /// migration different, in either direction.
    /// </summary>
    [Test]
    public async Task MigrateAsync_AppliedChecksumOfEitherLineEnding_IsTheSameMigration()
    {
        using var connection = NewConnection(TestPaths.NewDataRoot());
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        var migration = SqliteMigrationRunner.LoadEmbeddedMigrations().Single(m => m.Version == 4);
        var lf = migration.Sql.Replace(Crlf, Lf);

        foreach (var recorded in new[] { Sha256(lf), Sha256(lf.Replace(Lf, Crlf)) })
        {
            using (var set = connection.CreateCommand())
            {
                set.CommandText = "UPDATE SchemaMigrations SET Checksum = $c WHERE Version = 4;";
                set.Parameters.AddWithValue("$c", recorded);
                await set.ExecuteNonQueryAsync();
            }

            await Assert.That(await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None)).IsEqualTo(15L);
        }

        // A real content change is still refused.
        using (var tamper = connection.CreateCommand())
        {
            tamper.CommandText = "UPDATE SchemaMigrations SET Checksum = $c WHERE Version = 4;";
            tamper.Parameters.AddWithValue("$c", Sha256(lf + "-- edited" + Lf));
            await tamper.ExecuteNonQueryAsync();
        }

        await Assert.That(async () => await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None)).Throws<Chaos.Application.Abstractions.AppException>();
    }
}
