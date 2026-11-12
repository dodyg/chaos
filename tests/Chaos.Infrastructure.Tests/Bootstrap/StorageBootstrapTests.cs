using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Bootstrap;

/// <summary>
/// Work package 1 storage behavior on real file-backed SQLite (PLAN §16): bootstrap
/// migration + first-run seeding, data-root lock, store CRUD with optimistic concurrency
/// and rollback, SQLite error mapping, and backup create/validate/catalog.
/// </summary>
public sealed class StorageBootstrapTests
{
    [Test]
    [Arguments(13, "GitHubConnections")]
    [Arguments(14, "GitHubSyncCheckpoints")]
    [Arguments(15, "GitHubCommentCheckpoints")]
    [Arguments(16, "GitHub_TaskPlacementMoved")]
    [Arguments(17, "GitHubIssueWrites")]
    public async Task StartAsync_OlderSchema_BacksUpBeforeGitHubUpgrade(int oldVersion, string newTable)
    {
        var root = TestPaths.NewDataRoot();
        var options = StorageOptions.FromDataRoot(root);
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await Chaos.Infrastructure.Migrations.SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, oldVersion);
        }
        using var handle = await StartAsync(root);
        await Assert.That(handle.SchemaVersion).IsEqualTo(19L);
        var manifest = new BackupManager(handle.Store.Source, options.BackupsDirectory, "0.1.0", new FixedTimeProvider(Now)).Latest();
        await Assert.That(manifest).IsNotNull();
        await Assert.That(manifest!.Reason).IsEqualTo(BackupReason.PreMigration);
        await Assert.That(manifest.SchemaVersion).IsEqualTo((long)oldVersion);
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.BackupsDirectory, manifest.FileName), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await backup.OpenAsync();
        using var probe = backup.CreateCommand();
        probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE name=$table;";
        probe.Parameters.AddWithValue("$table", newTable);
        await Assert.That(Convert.ToInt64(await probe.ExecuteScalarAsync())).IsEqualTo(0L);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static async Task<ChaosStorageHandle> StartAsync(string dataRoot)
    {
        return await StorageBootstrap.StartAsync(
            StorageOptions.FromDataRoot(dataRoot), "0.1.0",
            new SequentialIdGenerator(), new FixedTimeProvider(Now), CancellationToken.None);
    }

    private static long CountRows(string databasePath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task<(string ProfileId, string SpaceId, string InboxId)> SeededIdsAsync(IApplicationStore store)
    {
        return await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token)
                ?? throw new InvalidOperationException("bootstrap did not seed a profile");
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
                ?? throw new InvalidOperationException("bootstrap did not seed a personal space");
            var inbox = await session.Containers.GetInboxAsync(space.Id, token)
                ?? throw new InvalidOperationException("bootstrap did not seed an Inbox");
            return (profile.Id, space.Id, inbox.Id);
        });
    }

    private static TaskRecord NewTask(string spaceId, string containerId, string title, long version = 1, long position = 0) => new()
    {
        Id = Guid.NewGuid().ToString(),
        SpaceId = spaceId,
        ContainerId = containerId,
        Title = title,
        Notes = "first notes",
        Status = TaskItemStatus.NotStarted,
        Priority = TaskPriority.Normal,
        CapacityCategory = CapacityCategory.Personal,
        Position = position,
        Version = version,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    [Test]
    public async Task StartAsync_FreshRoot_MigratesAndSeedsExactlyOnce()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());

        await Assert.That(handle.SchemaVersion).IsEqualTo(19L);
        await Assert.That(handle.PrimaryProfileId).IsNotEmpty();
        await Assert.That(CountRows(handle.DatabasePath, "Profiles")).IsEqualTo(1L);
        await Assert.That(CountRows(handle.DatabasePath, "Spaces")).IsEqualTo(1L);
        await Assert.That(CountRows(handle.DatabasePath, "Containers")).IsEqualTo(1L);
    }

    [Test]
    public async Task StartAsync_RepeatedStarts_KeepSingleSeed()
    {
        var dataRoot = TestPaths.NewDataRoot();
        string firstProfileId;
        using (var first = await StartAsync(dataRoot))
        {
            firstProfileId = first.PrimaryProfileId;
        }

        using (var second = await StartAsync(dataRoot))
        {
            await Assert.That(second.PrimaryProfileId).IsEqualTo(firstProfileId);
            await Assert.That(CountRows(second.DatabasePath, "Profiles")).IsEqualTo(1L);
            await Assert.That(CountRows(second.DatabasePath, "Spaces")).IsEqualTo(1L);
            await Assert.That(CountRows(second.DatabasePath, "Containers")).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task StartAsync_SecondProcessOnLockedRoot_IsRefused()
    {
        var dataRoot = TestPaths.NewDataRoot();
        using var lockHolder = DataRootLock.Acquire(dataRoot);

        AppException? caught = null;
        try
        {
            using var handle = await StartAsync(dataRoot);
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Kind).IsEqualTo(AppErrorKind.StorageUnavailable);
        await Assert.That(caught.Message).Contains("already running");
    }

    [Test]
    public async Task StartAsync_NewerSchemaVersion_IsRefused()
    {
        var dataRoot = TestPaths.NewDataRoot();
        var databasePath = Path.Combine(dataRoot, "data", "chaos.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE SchemaMigrations (Version INTEGER NOT NULL PRIMARY KEY, Checksum TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);
                INSERT INTO SchemaMigrations (Version, Checksum, AppliedAtUtc) VALUES (99, 'future', '2027-01-01T00:00:00.0000000Z');
                """;
            create.ExecuteNonQuery();
        }

        AppException? caught = null;
        try
        {
            using var handle = await StartAsync(dataRoot);
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("newer version of Chaos");
    }

    [Test]
    public async Task Store_TaskRoundTrip_PreservesEveryField()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var ids = await SeededIdsAsync(handle.Store);
        var added = NewTask(ids.SpaceId, ids.InboxId, "Round trip");

        await handle.Store.ExecuteAsync<object?>(async (session, token) =>
        {
            await session.Tasks.AddAsync(added, token);
            return null;
        });
        var fetched = await handle.Store.QueryAsync((session, token) => session.Tasks.GetAsync(added.Id, token));

        await Assert.That(fetched).IsEqualTo(added);
    }

    [Test]
    public async Task Store_UpdateAsync_WithMatchingVersion_UpdatesRow()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var ids = await SeededIdsAsync(handle.Store);
        var added = NewTask(ids.SpaceId, ids.InboxId, "Before edit");
        await handle.Store.ExecuteAsync<object?>(async (session, token) =>
        {
            await session.Tasks.AddAsync(added, token);
            return null;
        });
        var edited = added with { Title = "After edit", Version = 2, UpdatedAtUtc = Now.AddMinutes(5) };

        var updated = await handle.Store.ExecuteAsync((session, token) => session.Tasks.UpdateAsync(edited, expectedVersion: 1, token));

        await Assert.That(updated).IsEqualTo(edited);
        var fetched = await handle.Store.QueryAsync((session, token) => session.Tasks.GetAsync(added.Id, token));
        await Assert.That(fetched!.Title).IsEqualTo("After edit");
        await Assert.That(fetched.Version).IsEqualTo(2L);
    }

    [Test]
    public async Task Store_UpdateAsync_WithStaleVersion_ReturnsNullAndLeavesRow()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var ids = await SeededIdsAsync(handle.Store);
        var added = NewTask(ids.SpaceId, ids.InboxId, "Concurrent edit");
        await handle.Store.ExecuteAsync<object?>(async (session, token) =>
        {
            await session.Tasks.AddAsync(added, token);
            return null;
        });
        var stale = added with { Title = "Lost update", Version = 2 };

        var updated = await handle.Store.ExecuteAsync((session, token) => session.Tasks.UpdateAsync(stale, expectedVersion: 99, token));

        await Assert.That(updated).IsNull();
        var fetched = await handle.Store.QueryAsync((session, token) => session.Tasks.GetAsync(added.Id, token));
        await Assert.That(fetched!.Title).IsEqualTo("Concurrent edit");
        await Assert.That(fetched.Version).IsEqualTo(1L);
    }

    [Test]
    public async Task Store_GetNextPositionAsync_GrowsByGap()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var ids = await SeededIdsAsync(handle.Store);

        var first = await handle.Store.QueryAsync((session, token) => session.Tasks.GetNextPositionAsync(ids.InboxId, token));
        await Assert.That(first).IsEqualTo(0L);

        await handle.Store.ExecuteAsync<object?>(async (session, token) =>
        {
            await session.Tasks.AddAsync(NewTask(ids.SpaceId, ids.InboxId, "One", position: first), token);
            return null;
        });
        var second = await handle.Store.QueryAsync((session, token) => session.Tasks.GetNextPositionAsync(ids.InboxId, token));
        await Assert.That(second).IsEqualTo(1024L);
    }

    [Test]
    public async Task Store_FailedWrite_RollsBackWholeTransaction()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var ids = await SeededIdsAsync(handle.Store);

        AppException? caught = null;
        var doomed = NewTask(ids.SpaceId, ids.InboxId, "Doomed");
        try
        {
            await handle.Store.ExecuteAsync<object?>(async (session, token) =>
            {
                await session.Tasks.AddAsync(doomed, token);
                throw new InvalidOperationException("boom");
            });
        }
        catch (AppException error)
        {
            caught = error;
        }

        await Assert.That(caught).IsNotNull();
        var fetched = await handle.Store.QueryAsync((session, token) => session.Tasks.GetAsync(doomed.Id, token));
        await Assert.That(fetched).IsNull();
        await Assert.That(CountRows(handle.DatabasePath, "Tasks")).IsEqualTo(0L);
    }

    [Test]
    public async Task Store_MapsSqliteErrors_ToStableKinds()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());

        var constraint = await CatchAsync(() => handle.Store.ExecuteAsync<object?>((_, _) =>
            throw new SqliteException("constraint", 19)));
        await Assert.That(constraint!.Kind).IsEqualTo(AppErrorKind.Validation);

        var busy = await CatchAsync(() => handle.Store.ExecuteAsync<object?>((_, _) =>
            throw new SqliteException("busy", 5)));
        await Assert.That(busy!.Kind).IsEqualTo(AppErrorKind.StorageUnavailable);

        var full = await CatchAsync(() => handle.Store.ExecuteAsync<object?>((_, _) =>
            throw new SqliteException("full", 13)));
        await Assert.That(full!.Kind).IsEqualTo(AppErrorKind.StorageFull);

        var unexpected = await CatchAsync(() => handle.Store.ExecuteAsync<object?>((_, _) =>
            throw new InvalidOperationException("boom")));
        await Assert.That(unexpected!.Kind).IsEqualTo(AppErrorKind.StorageUnavailable);
    }

    [Test]
    public async Task Store_PassesAppExceptionThroughUnchanged()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var original = AppException.NotFound("no such row");

        var caught = await CatchAsync(() => handle.Store.ExecuteAsync<object?>((_, _) => throw original));

        await Assert.That(caught).IsSameReferenceAs(original);
    }

    [Test]
    public async Task BackupManager_CreatesValidBackupWithManifestAndCatalogsIt()
    {
        using var handle = await StartAsync(TestPaths.NewDataRoot());
        var backupsDirectory = Path.Combine(Path.GetTempPath(), "chaos-tests", "backups", Guid.NewGuid().ToString("N"));

        var manifest = await new BackupManager(handle.Store.Source, backupsDirectory, "0.1.0", new FixedTimeProvider(Now))
            .CreateAsync(BackupReason.PreMigration, CancellationToken.None);

        var path = Path.Combine(backupsDirectory, manifest.FileName);
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(File.Exists(path + BackupManager.ManifestSuffix)).IsTrue();
        await Assert.That(manifest.Reason).IsEqualTo(BackupReason.PreMigration);
        await Assert.That(manifest.ChecksumSha256).IsEqualTo(BackupManager.ComputeChecksum(path));
        await Assert.That(manifest.AppVersion).IsEqualTo("0.1.0");
        await Assert.That(manifest.SchemaVersion).IsEqualTo(handle.SchemaVersion);

        var catalogued = await handle.Store.QueryAsync((session, token) => session.Backups.FindByFileNameAsync(manifest.FileName, token));
        await Assert.That(catalogued!.ValidationStatus).IsEqualTo(BackupValidationStatus.Valid);
        await Assert.That(catalogued.ChecksumSha256).IsEqualTo(manifest.ChecksumSha256);
    }

    [Test]
    public async Task CheckDatabaseFile_NonDatabaseFile_ReportsInvalid()
    {
        var junkPath = Path.Combine(Path.GetTempPath(), "chaos-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(junkPath)!);
        await File.WriteAllTextAsync(junkPath, "definitely not a sqlite database");

        var check = BackupManager.CheckDatabaseFile(junkPath);

        await Assert.That(check.Valid).IsFalse();
    }

    private static async Task<AppException?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException error)
        {
            return error;
        }
    }
}
