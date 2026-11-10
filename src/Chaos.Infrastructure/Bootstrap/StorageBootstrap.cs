using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Bootstrap;

/// <summary>Data-root paths (PLAN §5): database under data/, backups under backups/. Tests and development pass an explicit root.</summary>
public sealed record StorageOptions
{
    public required string DataRoot { get; init; }

    public string DatabasePath => Path.Combine(DataRoot, "data", "chaos.db");

    public string BackupsDirectory => Path.Combine(DataRoot, "backups");

    public static StorageOptions FromDataRoot(string dataRoot) => new() { DataRoot = dataRoot };
}

/// <summary>One app session against one data root; disposing closes storage and releases the data-root lock.</summary>
public sealed class ChaosStorageHandle(DataRootLock lockHandle) : IDisposable
{
    public required string DatabasePath { get; init; }

    public required long SchemaVersion { get; init; }

    public required string PrimaryProfileId { get; init; }

    public required SqliteApplicationStore Store { get; init; }

    public required StorageOptions Options { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>What startup did about an interrupted restore or reset, shown once in Settings; null when nothing happened.</summary>
    public DataProtection.RecoveryNotice? RecoveryNotice { get; init; }

    private readonly DataRootLock _lockHandle = lockHandle;

    public void Dispose()
    {
        Store.Dispose();
        _lockHandle.Dispose();
    }
}

/// <summary>
/// Startup sequence (PLAN §5): acquire the data-root lock; set WAL once; inspect the schema
/// version without mutating; refuse a newer schema with a readable error; back up before
/// upgrades; run pending migrations transactionally; seed profile/Personal/Inbox idempotently
/// in one transaction. No step deletes or recreates a database after an error.
/// </summary>
public static class StorageBootstrap
{
    public static async Task<ChaosStorageHandle> StartAsync(
        StorageOptions options,
        string appVersion,
        IIdGenerator ids,
        TimeProvider clock,
        CancellationToken ct)
    {
        var lockHandle = DataRootLock.Acquire(options.DataRoot);
        try
        {
            // Layout (PLAN §5): the database lives under data/; create the folder before first open.
            Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);

            // A restore or reset interrupted by a crash is finished or undone before anything
            // opens the database (PLAN §14), so startup always sees one consistent file.
            var recovery = DataProtection.DatabaseReplacement.RecoverInterrupted(options);

            var source = new SqliteConnectionSource(options.DatabasePath);

            await using (var probe = source.Open())
            {
                // WAL is a persistent database property; setting it here covers fresh and
                // existing databases alike and needs no transaction.
                ExecutePragma(probe, "PRAGMA journal_mode=WAL;");

                var currentVersion = await SqliteMigrationRunner.ReadCurrentSchemaVersionUnsafeAsync(probe, ct);
                if (currentVersion > SqliteMigrationRunner.MaxKnownSchemaVersion)
                {
                    throw new AppException(
                        AppErrorKind.StorageUnavailable,
                        "This data folder was created by a newer version of Chaos and cannot be opened here. " +
                        "Install the newer version, or restore a backup made with this version.");
                }

                if (currentVersion > 0 && currentVersion < SqliteMigrationRunner.MaxKnownSchemaVersion)
                {
                    await new DataProtection.BackupManager(source, options.BackupsDirectory, appVersion, clock)
                        .CreateAsync(BackupReason.PreMigration, ct);
                }
            }

            long schemaVersion;
            await using (var migrationConnection = source.Open())
            {
                schemaVersion = await SqliteMigrationRunner.MigrateAsync(migrationConnection, ct);
                // Verifies FTS5 and (re)builds the search index when its normalization is stale,
                // including the first start after migration 003 (PLAN §5).
                await Search.SearchIndexWriter.EnsureCurrentAsync(migrationConnection, clock.GetUtcNow(), ct);
            }

            var store = new SqliteApplicationStore(source);
            try
            {
                var profileId = await SeedAsync(store, ids, clock, ct);
                return new ChaosStorageHandle(lockHandle)
                {
                    DatabasePath = options.DatabasePath,
                    SchemaVersion = schemaVersion,
                    PrimaryProfileId = profileId,
                    Store = store,
                    Options = options,
                    AppVersion = appVersion,
                    RecoveryNotice = recovery,
                };
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the local profile, its Personal space and the Inbox container exactly once,
    /// each lookup-first (PLAN §1 decisions 1/6). Safe to run on every start.
    /// </summary>
    public static async Task<string> SeedAsync(IApplicationStore store, IIdGenerator ids, TimeProvider clock, CancellationToken ct)
    {
        return await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();

            var profile = await session.Profiles.GetPrimaryAsync(token);
            if (profile is null)
            {
                profile = new ProfileRecord
                {
                    Id = ids.NewId().ToString(),
                    DisplayName = "Me",
                    ZoneId = TimeZoneInfo.Local.Id,
                    Locale = System.Globalization.CultureInfo.CurrentCulture.Name,
                    DateFormat = "MDY",
                    FirstWeekday = DayOfWeek.Sunday,
                    DefaultReminderTime = new TimeOnly(9, 0),
                    MorningTime = Chaos.Domain.Summaries.SummarySchedule.DefaultMorningTime,
                    SummaryTime = Chaos.Domain.Summaries.SummarySchedule.DefaultEveningTime,
                    WeeklySummaryDay = Chaos.Domain.Summaries.SummarySchedule.DefaultWeeklyDay(DayOfWeek.Sunday),
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Profiles.AddAsync(profile, token);
            }

            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            if (space is null)
            {
                space = new SpaceRecord
                {
                    Id = ids.NewId().ToString(),
                    OwnerProfileId = profile.Id,
                    Kind = SpaceKind.Personal,
                    Name = "Personal",
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Spaces.AddAsync(space, token);
            }

            var inbox = await session.Containers.GetInboxAsync(space.Id, token);
            if (inbox is null)
            {
                inbox = new ContainerRecord
                {
                    Id = ids.NewId().ToString(),
                    SpaceId = space.Id,
                    Kind = ContainerKind.Inbox,
                    Name = "Inbox",
                    IsInbox = true,
                    Position = 0,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Containers.AddAsync(inbox, token);
            }

            return profile.Id;
        }, ct);
    }

    private static void ExecutePragma(Microsoft.Data.Sqlite.SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
