using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.DataProtection;

/// <summary>
/// PLAN §15 WP12 backups and restore on real file-backed SQLite: WAL-active backup, retention,
/// corrupt/newer refusal, no-space failure, restore with a safety backup, interrupted restore
/// recovery, and the exit criterion of restoring into another clean data root.
/// </summary>
public sealed class BackupRestoreTests
{
    private static async Task<string> CaptureAsync(DataHost host, string title)
    {
        var inbox = await host.Store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile!.Id, token);
            return (await session.Containers.GetInboxAsync(space!.Id, token))!.Id;
        });
        return (await host.Tasks.CaptureAsync(new CaptureTaskCommand(DataHost.Op(), inbox, title, null))).Task.Id;
    }

    private static async Task<IReadOnlyList<string>> TitlesAsync(DataHost host) =>
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = ((Chaos.Infrastructure.Sqlite.SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = "SELECT Title FROM Tasks WHERE DeletedAtUtc IS NULL ORDER BY Title;";
            var titles = new List<string>();
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                titles.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)titles;
        });

    [Test]
    public async Task CreateAsync_WhileWalHoldsUncheckpointedCommits_BackupHasLatestDataAndIsSelfContained()
    {
        using var host = await DataHost.StartAsync();
        await CaptureAsync(host, "In the WAL only");
        await Assert.That(new FileInfo(host.Storage.DatabasePath + "-wal").Length).IsGreaterThan(0L);

        var manifest = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);

        var path = Path.Combine(host.Backups.BackupsDirectory, manifest.FileName);
        await Assert.That(manifest.Counts.Tasks).IsEqualTo(1L);
        await Assert.That(File.Exists(path + "-wal")).IsFalse();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        await Assert.That(mode.ExecuteScalar() as string).IsEqualTo("delete");
        await Assert.That(host.Backups.Inspect(manifest.FileName).CanRestore).IsTrue();
    }

    [Test]
    public async Task SelectForDeletion_ManyBackups_KeepsSevenDailyFourWeeklyAndThreeSafety()
    {
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var backups = new List<BackupManifest>();
        for (var day = 0; day < 60; day++)
        {
            foreach (var hour in new[] { 0, 6 })
            {
                backups.Add(Manifest($"d{day:00}-{hour}", start.AddDays(day).AddHours(hour), BackupReason.Scheduled));
            }
        }

        for (var n = 0; n < 5; n++)
        {
            backups.Add(Manifest($"safety{n}", start.AddDays(10 + n).AddHours(1), BackupReason.PreImport));
        }

        var deleted = BackupManager.SelectForDeletion(backups).ToHashSet();
        var kept = backups.Where(b => !deleted.Contains(b.FileName)).Select(b => b.FileName).ToHashSet();

        // Newest of each of the last seven days.
        for (var day = 53; day < 60; day++)
        {
            await Assert.That(kept.Contains($"d{day:00}-6")).IsTrue();
            await Assert.That(kept.Contains($"d{day:00}-0")).IsFalse();
        }

        // The three newest safety backups survive; the two older ones do not.
        await Assert.That(kept.Contains("safety4")).IsTrue();
        await Assert.That(kept.Contains("safety2")).IsTrue();
        await Assert.That(kept.Contains("safety1")).IsFalse();
        // Weeks start on Monday: the two newest weeks end inside the seven daily days, so the
        // weekly rule adds the Sundays of the two weeks before them (days 47 and 40).
        await Assert.That(kept.Contains("d47-6")).IsTrue();
        await Assert.That(kept.Contains("d40-6")).IsTrue();
        await Assert.That(kept.Count).IsEqualTo(7 + 2 + 3);
    }

    private static BackupManifest Manifest(string name, DateTimeOffset at, BackupReason reason) => new()
    {
        FileName = name, SchemaVersion = 12, AppVersion = "0.1.0", CreatedAtUtc = at, Reason = reason,
        ChecksumSha256 = new string('0', 64), SizeBytes = 1, Counts = DataCounts.Empty,
    };

    [Test]
    public async Task CreateAsync_DiskFullDuringCopy_ReportsStorageFullLeavesNoFileAndKeepsOlderBackups()
    {
        var fail = false;
        using var host = await DataHost.StartAsync(afterBackupCopy: _ =>
        {
            if (fail)
            {
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            }
        });
        await CaptureAsync(host, "Something");
        var good = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        host.Clock.Now = host.Clock.Now.AddDays(1);
        fail = true;

        AppException? error = null;
        try
        {
            await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        }
        catch (AppException caught)
        {
            error = caught;
        }

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.StorageFull);
        await Assert.That(host.Backups.LastError).IsNotNull();
        var files = Directory.GetFiles(host.Backups.BackupsDirectory).Select(Path.GetFileName).ToList();
        await Assert.That(files.Count).IsEqualTo(2); // the good backup and its manifest; no partial file
        await Assert.That(files).Contains(good.FileName);
    }

    [Test]
    public async Task Inspect_TamperedOrForeignOrNewerFiles_AreRefused()
    {
        using var host = await DataHost.StartAsync();
        await CaptureAsync(host, "Something");
        var manifest = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        var path = Path.Combine(host.Backups.BackupsDirectory, manifest.FileName);

        // A file with no description that is not a database.
        await File.WriteAllTextAsync(Path.Combine(host.Backups.BackupsDirectory, "chaos-junk.db"), "not sqlite");
        await Assert.That(host.Backups.Inspect("chaos-junk.db").CanRestore).IsFalse();

        // A copy claiming a newer schema.
        var newer = Path.Combine(host.Backups.BackupsDirectory, "chaos-newer.db");
        File.Copy(path, newer);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = newer, Pooling = false }.ToString()))
        {
            connection.Open();
            using var bump = connection.CreateCommand();
            bump.CommandText = "INSERT INTO SchemaMigrations (Version, Checksum, AppliedAtUtc) VALUES (99, 'x', '2030-01-01T00:00:00.0000000Z');";
            bump.ExecuteNonQuery();
        }

        var newerInspection = host.Backups.Inspect("chaos-newer.db");
        await Assert.That(newerInspection.CanRestore).IsFalse();
        await Assert.That(newerInspection.Problem!).Contains("newer version");

        // The described backup with bytes changed after it was made.
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
        var tampered = host.Backups.Inspect(manifest.FileName);
        await Assert.That(tampered.CanRestore).IsFalse();
        await Assert.That(tampered.Problem!).Contains("checksum");

        await Assert.That(() => host.Backups.ResolveBackupPath("..\\chaos.db")).Throws<AppException>();
    }

    [Test]
    public async Task RestoreBackupAsync_ReplacesLiveDataKeepsSafetyBackupAndSurvivesRestart()
    {
        var root = TestPaths.NewDataRoot();
        string safety;
        using (var host = await DataHost.StartAsync(root))
        {
            await CaptureAsync(host, "Before backup");
            var backup = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
            await CaptureAsync(host, "After backup");
            host.Clock.Now = host.Clock.Now.AddMinutes(5);

            var outcome = await host.Protection.RestoreBackupAsync(backup.FileName, backup.ChecksumSha256, CancellationToken.None);
            safety = outcome.SafetyBackupFileName;

            await Assert.That(await TitlesAsync(host)).IsEquivalentTo(new[] { "Before backup" });
            // The live store keeps working on the restored file.
            await CaptureAsync(host, "After restore");
            await Assert.That(host.Backups.Inspect(safety).Counts.Tasks).IsEqualTo(2L);
            await Assert.That(File.Exists(DatabaseReplacement.MarkerPath(host.Storage.Options))).IsFalse();
            await Assert.That(File.Exists(host.Storage.DatabasePath + DatabaseReplacement.ReplacedSuffix)).IsFalse();
        }

        using var restarted = await DataHost.StartAsync(root);
        await Assert.That(await TitlesAsync(restarted)).IsEquivalentTo(new[] { "After restore", "Before backup" });
        await Assert.That(restarted.Storage.RecoveryNotice).IsNull();
    }

    [Test]
    public async Task RestoreBackupAsync_ChecksumDiffersFromPreview_IsRefusedWithoutChanges()
    {
        using var host = await DataHost.StartAsync();
        await CaptureAsync(host, "Keep me");
        var backup = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        await CaptureAsync(host, "Also keep me");

        await Assert.That(async () => await host.Protection.RestoreBackupAsync(backup.FileName, new string('a', 64), CancellationToken.None)).Throws<AppException>();
        await Assert.That((await TitlesAsync(host)).Count).IsEqualTo(2);
    }

    [Test]
    public async Task RestoreIntoAnotherCleanRoot_SemanticContentsMatchTheSource()
    {
        using var source = await DataHost.StartAsync();
        await source.SeedAsync();
        var backup = await source.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        var expected = await source.DumpAsync();

        using var target = await DataHost.StartAsync();
        await CaptureAsync(target, "Target's own task, replaced by the restore");
        Directory.CreateDirectory(target.Backups.BackupsDirectory);
        File.Copy(Path.Combine(source.Backups.BackupsDirectory, backup.FileName), Path.Combine(target.Backups.BackupsDirectory, backup.FileName));
        File.Copy(Path.Combine(source.Backups.BackupsDirectory, backup.FileName + BackupManager.ManifestSuffix), Path.Combine(target.Backups.BackupsDirectory, backup.FileName + BackupManager.ManifestSuffix));

        await target.Protection.RestoreBackupAsync(backup.FileName, backup.ChecksumSha256, CancellationToken.None);

        await AssertSameData(expected, await target.DumpAsync());
        // Search works on the restored data.
        await Assert.That(await target.CountAsync("SELECT count(*) FROM SearchIndex WHERE SearchIndex MATCH 'plumber';")).IsEqualTo(1L);
    }

    internal static async Task AssertSameData(IReadOnlyList<Transfer.ArchiveTable> expected, IReadOnlyList<Transfer.ArchiveTable> actual)
    {
        await Assert.That(actual.Select(t => t.Name)).IsEquivalentTo(expected.Select(t => t.Name));
        foreach (var table in expected)
        {
            var other = actual.Single(t => t.Name == table.Name);
            await Assert.That(other.Columns).IsEquivalentTo(table.Columns);
            await Assert.That(JsonSerializer.Serialize(other.Rows)).IsEqualTo(JsonSerializer.Serialize(table.Rows));
        }
    }

    [Test]
    public async Task Startup_AfterCrashWhileMovingOriginalsAside_KeepsPreviousData()
    {
        var root = TestPaths.NewDataRoot();
        StorageOptions options;
        using (var host = await DataHost.StartAsync(root))
        {
            await CaptureAsync(host, "Precious");
            options = host.Storage.Options;
        }

        SqliteConnection.ClearAllPools();
        // Simulate a crash in the middle of moving out: the main file moved, a marker says so.
        File.Move(options.DatabasePath, options.DatabasePath + DatabaseReplacement.ReplacedSuffix);
        await File.WriteAllTextAsync(DatabaseReplacement.MarkerPath(options),
            JsonSerializer.Serialize(new ReplacementMarker(ReplacementStage.MovingOut, "restore from a backup", DataHost.Start), new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        await File.WriteAllTextAsync(DatabaseReplacement.StagedPath(options), "half-copied");

        using var restarted = await DataHost.StartAsync(root);

        await Assert.That(await TitlesAsync(restarted)).IsEquivalentTo(new[] { "Precious" });
        await Assert.That(restarted.Storage.RecoveryNotice!.KeptPrevious).IsTrue();
        await Assert.That(File.Exists(DatabaseReplacement.MarkerPath(options))).IsFalse();
        await Assert.That(File.Exists(DatabaseReplacement.StagedPath(options))).IsFalse();
    }

    [Test]
    public async Task Startup_AfterCrashWithBrokenNewFileInPlace_RevertsToOriginal()
    {
        var root = TestPaths.NewDataRoot();
        StorageOptions options;
        using (var host = await DataHost.StartAsync(root))
        {
            await CaptureAsync(host, "Original");
            options = host.Storage.Options;
        }

        SqliteConnection.ClearAllPools();
        var staged = DatabaseReplacement.StagedPath(options);
        await File.WriteAllTextAsync(staged, "this is not a database");
        DatabaseReplacement.Swap(options, staged, "restore from a backup", DataHost.Start);

        using var restarted = await DataHost.StartAsync(root);

        await Assert.That(await TitlesAsync(restarted)).IsEquivalentTo(new[] { "Original" });
        await Assert.That(restarted.Storage.RecoveryNotice!.KeptPrevious).IsTrue();
    }

    [Test]
    public async Task Startup_AfterCrashOnceValidNewFileWasInPlace_FinishesTheRestore()
    {
        var root = TestPaths.NewDataRoot();
        StorageOptions options;
        string backupPath;
        using (var host = await DataHost.StartAsync(root))
        {
            await CaptureAsync(host, "From backup");
            var backup = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
            backupPath = Path.Combine(host.Backups.BackupsDirectory, backup.FileName);
            await CaptureAsync(host, "Later");
            options = host.Storage.Options;
        }

        SqliteConnection.ClearAllPools();
        File.Copy(backupPath, DatabaseReplacement.StagedPath(options));
        DatabaseReplacement.Swap(options, DatabaseReplacement.StagedPath(options), "restore from a backup", DataHost.Start);

        using var restarted = await DataHost.StartAsync(root);

        await Assert.That(await TitlesAsync(restarted)).IsEquivalentTo(new[] { "From backup" });
        await Assert.That(restarted.Storage.RecoveryNotice!.KeptPrevious).IsFalse();
        await Assert.That(File.Exists(options.DatabasePath + DatabaseReplacement.ReplacedSuffix)).IsFalse();
    }

    [Test]
    public async Task ScheduledBackup_OnlyWhenADayOldAndSomethingChanged()
    {
        using var host = await DataHost.StartAsync();
        await CaptureAsync(host, "Change");
        await Assert.That(await host.Protection.RunScheduledBackupIfDueAsync(CancellationToken.None)).IsNotNull();

        host.Clock.Now = host.Clock.Now.AddHours(2);
        await CaptureAsync(host, "Another change");
        await Assert.That(await host.Protection.RunScheduledBackupIfDueAsync(CancellationToken.None)).IsNull();

        host.Clock.Now = host.Clock.Now.AddDays(1);
        await Assert.That(await host.Protection.RunScheduledBackupIfDueAsync(CancellationToken.None)).IsNotNull();

        host.Clock.Now = host.Clock.Now.AddDays(2); // nothing changed since
        await Assert.That(await host.Protection.RunScheduledBackupIfDueAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ResetAsync_DeletesDataAndBackupsButNotExports()
    {
        using var host = await DataHost.StartAsync();
        await host.SeedAsync();
        await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        var exports = Path.Combine(host.Root, "exports-elsewhere");
        var export = await host.Transfer.ExportAsync(Transfer.ExportFormat.Json, exports, CancellationToken.None);

        await host.Protection.ResetAsync(deleteBackups: true, CancellationToken.None);

        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Containers WHERE IsInbox = 1;")).IsEqualTo(1L);
        await Assert.That(Directory.GetFiles(host.Backups.BackupsDirectory, "chaos-*")).IsEmpty();
        await Assert.That(File.Exists(export.Path)).IsTrue();
        // The fresh database is usable at once.
        await CaptureAsync(host, "Fresh start");
    }
}
