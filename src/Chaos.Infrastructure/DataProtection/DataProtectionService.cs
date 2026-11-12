using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Search;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.DataProtection;

public sealed record RestoreOutcome(string RestoredFrom, string SafetyBackupFileName, long SchemaVersion, DataCounts Counts);

public sealed record ResetOutcome(bool BackupsDeleted, int BackupFilesDeleted);

/// <summary>
/// Replaces or clears the live database while the app runs (PLAN §14). Each operation first
/// makes a validated safety backup, then, with background work paused through
/// <see cref="Quiesce"/> and every connection closed by the store, swaps database files with a
/// recovery marker, opens and migrates the new file, rebuilds the search index and clears
/// machine-specific notification schedules so the reminder engine schedules them again.
/// </summary>
public sealed class DataProtectionService(SqliteApplicationStore store, StorageOptions options, BackupManager backups, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>
    /// Runs the given work with background services paused (the desktop host stops the
    /// reminder engine and takes back OS notification schedules). Defaults to running it as is.
    /// </summary>
    public Func<Func<Task>, CancellationToken, Task> Quiesce { get; set; } = (work, _) => work();

    public BackupManager Backups => backups;

    public StorageOptions Options => options;

    private readonly SemaphoreSlim _operations = new(1, 1);

    /// <summary>
    /// Runs one data-protection operation at a time: backups, restores, resets, imports and
    /// purges never overlap, so a scheduled backup cannot open the file while a restore moves it.
    /// </summary>
    public async Task<T> GuardAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try
        {
            return await operation();
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>Makes a validated backup now (manual, or the safety backup before an import).</summary>
    public Task<BackupManifest> BackupNowAsync(BackupReason reason, CancellationToken ct) =>
        GuardAsync(() => backups.CreateAsync(reason, ct), ct);

    /// <summary>The daily backup: made only when the last one is a day old and something changed since. Returns it, or null.</summary>
    public Task<BackupManifest?> RunScheduledBackupIfDueAsync(CancellationToken ct) =>
        GuardAsync<BackupManifest?>(async () => await backups.ScheduledBackupDueAsync(ct) ? await backups.CreateAsync(BackupReason.Scheduled, ct) : null, ct);

    /// <summary>
    /// Restores a backup from the backups folder. <paramref name="expectedChecksum"/> is the
    /// checksum shown in the preview, so the file restored is exactly the one confirmed.
    /// </summary>
    public Task<RestoreOutcome> RestoreBackupAsync(string fileName, string expectedChecksum, CancellationToken ct) =>
        GuardAsync(() => RestoreBackupCoreAsync(fileName, expectedChecksum, ct), ct);

    /// <summary>Replaces all local data with a Chaos JSON archive (restore mode), after a safety backup.</summary>
    public Task<RestoreOutcome> RestoreArchiveAsync(Transfer.ArchiveContent archive, string sourceName, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var staged = DatabaseReplacement.StagedPath(options);
            await Transfer.JsonArchive.BuildDatabaseAsync(archive, staged, clock.GetUtcNow(), ct);
            return await ReplaceWithStagedAsync(staged, "restore from an archive", sourceName, ct);
        }, ct);

    private async Task<RestoreOutcome> RestoreBackupCoreAsync(string fileName, string expectedChecksum, CancellationToken ct)
    {
        var inspection = backups.Inspect(fileName);
        if (!inspection.CanRestore)
        {
            throw AppException.Validation("fileName", $"This backup cannot be restored: {inspection.Problem}");
        }

        var path = backups.ResolveBackupPath(fileName);
        if (!string.Equals(BackupManager.ComputeChecksum(path), expectedChecksum, StringComparison.Ordinal))
        {
            throw AppException.Validation("checksum", "This backup changed after you reviewed it. Review it again before restoring.");
        }

        var staged = DatabaseReplacement.StagedPath(options);
        File.Copy(path, staged, overwrite: true);
        if (!string.Equals(BackupManager.ComputeChecksum(staged), expectedChecksum, StringComparison.Ordinal))
        {
            BackupManager.TryDelete(staged);
            throw new AppException(AppErrorKind.StorageUnavailable, "The backup could not be copied intact. Nothing was changed.");
        }

        return await ReplaceWithStagedAsync(staged, "restore from a backup", fileName, ct);
    }

    /// <summary>
    /// Replaces the live database with a staged, already validated file (a backup copy or a
    /// database built from a JSON archive). The original is set aside until the new file opens.
    /// </summary>
    private async Task<RestoreOutcome> ReplaceWithStagedAsync(string stagedPath, string description, string restoredFrom, CancellationToken ct)
    {
        RestoreOutcome? outcome = null;
        try
        {
            await Quiesce(async () =>
            {
                outcome = await store.RunExclusiveAsync(async token =>
                {
                    var safety = await backups.CreateAsync(BackupReason.PreRestore, token);
                    SqliteConnection.ClearAllPools();
                    DatabaseReplacement.Swap(options, stagedPath, description, clock.GetUtcNow());
                    try
                    {
                        var (version, counts) = await OpenAndPrepareAsync(options, clock.GetUtcNow(), token);
                        SqliteConnection.ClearAllPools();
                        DatabaseReplacement.Complete(options);
                        return new RestoreOutcome(restoredFrom, safety.FileName, version, counts);
                    }
                    catch
                    {
                        SqliteConnection.ClearAllPools();
                        DatabaseReplacement.Revert(options);
                        throw;
                    }
                }, ct);
            }, ct);
        }
        finally
        {
            BackupManager.TryDelete(stagedPath);
        }

        // A backup holds its own profile; the seed only fills in anything missing.
        await StorageBootstrap.SeedAsync(store, ids, clock, ct);
        return outcome!;
    }

    /// <summary>
    /// "Delete all local data" (PLAN §14): with background work stopped, removes the database
    /// and, when asked, Chaos's own backup files, then starts a fresh empty database. Export
    /// files saved elsewhere are never touched. A safety backup is not made, because the point
    /// is to remove the data; the user is asked to confirm in Settings.
    /// </summary>
    public Task<ResetOutcome> ResetAsync(bool deleteBackups, CancellationToken ct) =>
        GuardAsync(() => ResetCoreAsync(deleteBackups, ct), ct);

    private async Task<ResetOutcome> ResetCoreAsync(bool deleteBackups, CancellationToken ct)
    {
        var deleted = 0;
        await Quiesce(async () =>
        {
            await store.RunExclusiveAsync<object?>(async token =>
            {
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                {
                    if (File.Exists(options.DatabasePath + suffix))
                    {
                        File.Delete(options.DatabasePath + suffix);
                    }
                }

                BackupManager.TryDelete(DatabaseReplacement.StagedPath(options));
                if (deleteBackups && Directory.Exists(options.BackupsDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(options.BackupsDirectory, "chaos-*"))
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }

                await OpenAndPrepareAsync(options, clock.GetUtcNow(), token);
                SqliteConnection.ClearAllPools();
                return null;
            }, ct);
        }, ct);

        await StorageBootstrap.SeedAsync(store, ids, clock, ct);
        return new ResetOutcome(deleteBackups, deleted);
    }

    /// <summary>
    /// Opens the database file now in place: WAL on, migrations to this build's version, the
    /// search index rebuilt when stale, integrity checked, and notification schedules from
    /// another time or machine cleared so they are scheduled again. Throws when it is unusable.
    /// </summary>
    public static async Task<(long SchemaVersion, DataCounts Counts)> OpenAndPrepareAsync(StorageOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var source = new SqliteConnectionSource(options.DatabasePath);
        await using var connection = source.Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        var version = await SqliteMigrationRunner.MigrateAsync(connection, ct);
        await SearchIndexWriter.EnsureCurrentAsync(connection, now, ct);

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            if (check.ExecuteScalar() as string != "ok")
            {
                throw new AppException(AppErrorKind.StorageUnavailable, "The restored data did not pass its check after opening, so your previous data was kept.");
            }
        }

        using (var jobs = connection.CreateCommand())
        {
            jobs.CommandText = "UPDATE NotificationJobs SET OsState = 0, OsNotificationId = NULL, OsAttempts = 0, OsLastAttemptUtc = NULL, OsError = NULL WHERE OsState <> 0 OR OsNotificationId IS NOT NULL;";
            jobs.ExecuteNonQuery();
        }

        using (var github = connection.CreateCommand())
        {
            github.CommandText = """
                DELETE FROM GitHubSyncCheckpoints;
                UPDATE GitHubRepositories SET IssuesCursorUtc=NULL,CommentsCursorUtc=NULL,IssuesEtag=NULL,CommentsEtag=NULL,LabelsEtag=NULL,MilestonesEtag=NULL;
                UPDATE GitHubConnections SET RateLimitResetUtc=NULL;
                UPDATE GitHubIssueLinks SET IssueEtag=NULL,NextAttemptUtc=NULL,ErrorAttempts=0;
                UPDATE GitHubCommentLinks SET NextAttemptUtc=NULL,ErrorAttempts=0;
                """;
            await github.ExecuteNonQueryAsync(ct);
        }
        return (version, BackupManager.ReadCounts(connection));
    }

    /// <summary>OS notification IDs the current database has scheduled, so they can be taken back before a replace or reset.</summary>
    public async Task<IReadOnlyList<string>> ListScheduledOsNotificationIdsAsync(CancellationToken ct) =>
        await store.QueryAsync(async (session, token) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT OsNotificationId FROM NotificationJobs WHERE OsNotificationId IS NOT NULL;";
            var ids = new List<string>();
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                ids.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)ids;
        }, ct);
}
