using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.DataProtection;

/// <summary>Plain counts that identify what a backup or archive holds, for previews (PLAN §14).</summary>
public sealed record DataCounts(long Tasks, long OpenTasks, long Lists, long Subtasks, long DiaryEntries, long PadItems, long Tags)
{
    public static readonly DataCounts Empty = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The sidecar written next to every backup file (<c>name.db.json</c>). It is written last, so
/// its presence marks a finished, validated backup; the backups folder, not the database,
/// is the source of truth for which backups exist, because a restore replaces the database.
/// </summary>
public sealed record BackupManifest
{
    public const string FormatName = "chaos-backup";
    public const int CurrentFormatVersion = 1;

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string FileName { get; init; }
    public required long SchemaVersion { get; init; }
    public required string AppVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required BackupReason Reason { get; init; }
    public required string ChecksumSha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DataCounts Counts { get; init; }
}

/// <summary>One file in the backups folder as listed: a manifest when it has one, else what is known.</summary>
public sealed record BackupEntry(string FileName, BackupManifest? Manifest, BackupValidationStatus Status, string? Problem, long SizeBytes, DateTimeOffset CreatedAtUtc);

/// <summary>Result of fully checking one backup before a restore: checksum, integrity, foreign keys and schema.</summary>
public sealed record BackupInspection(string FileName, bool CanRestore, string? Problem, BackupManifest? Manifest, long SchemaVersion, DataCounts Counts);

/// <summary>What validating a database file found; never mutates the file.</summary>
public sealed record DatabaseFileCheck(bool Valid, string? Problem, long SchemaVersion, DataCounts Counts);

/// <summary>
/// Validated local backups (PLAN §14). A backup is made with SQLite's online backup API from a
/// connection of its own, so it is coherent while WAL is active, into a temporary file that is
/// switched to rollback-journal mode, checked with integrity_check and foreign_key_check,
/// checksummed, renamed into place and only then described by its manifest. Retention runs only
/// after a new backup has succeeded and never deletes files it cannot read a manifest for.
/// Backups are ordinary local files protected by the device account; they are not encrypted.
/// </summary>
public sealed class BackupManager(SqliteConnectionSource source, string backupsDirectory, string appVersion, TimeProvider clock, Action<string>? afterCopyTestHook = null)
{
    public const int DailyToKeep = 7;
    public const int WeeklyToKeep = 4;
    public const int SafetyToKeep = 3;
    public const string ManifestSuffix = ".json";
    private const string PartialSuffix = ".partial";

    internal static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _statusSync = new();
    private BackupManifest? _lastSuccess;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAtUtc;

    public string BackupsDirectory => backupsDirectory;

    /// <summary>The most recent backup made by this process, or null.</summary>
    public BackupManifest? LastSuccess { get { lock (_statusSync) return _lastSuccess; } }

    /// <summary>The last backup failure (cleared by the next success), shown in Settings.</summary>
    public (string Message, DateTimeOffset AtUtc)? LastError
    {
        get { lock (_statusSync) return _lastError is null ? null : (_lastError, _lastErrorAtUtc!.Value); }
    }

    /// <summary>Makes, validates and records one backup, then applies retention. Throws a readable AppException on failure.</summary>
    public async Task<BackupManifest> CreateAsync(BackupReason reason, CancellationToken ct)
    {
        try
        {
            var manifest = await CreateCoreAsync(reason, ct);
            lock (_statusSync)
            {
                _lastSuccess = manifest;
                _lastError = null;
                _lastErrorAtUtc = null;
            }

            ApplyRetention();
            return manifest;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var mapped = MapFailure(error);
            lock (_statusSync)
            {
                _lastError = mapped.Message;
                _lastErrorAtUtc = clock.GetUtcNow();
            }

            throw mapped;
        }
    }

    private async Task<BackupManifest> CreateCoreAsync(BackupReason reason, CancellationToken ct)
    {
        Directory.CreateDirectory(backupsDirectory);
        RemoveStalePartials();

        var now = clock.GetUtcNow();
        var fileName = $"chaos-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.db";
        var finalPath = Path.Combine(backupsDirectory, fileName);
        var partialPath = finalPath + PartialSuffix;

        try
        {
            long schemaVersion;
            await using (var from = source.Open())
            {
                schemaVersion = await SqliteMigrationRunner.ReadCurrentSchemaVersionUnsafeAsync(from, ct);
                await using var to = OpenFile(partialPath, SqliteOpenMode.ReadWriteCreate);
                from.BackupDatabase(to);
                // A backup is a single self-contained file: no WAL beside it in the backups folder.
                Execute(to, "PRAGMA journal_mode=DELETE;");
            }

            afterCopyTestHook?.Invoke(partialPath);

            var check = CheckDatabaseFile(partialPath);
            if (!check.Valid)
            {
                throw new AppException(AppErrorKind.StorageUnavailable, $"The new backup failed its check ({check.Problem}), so it was discarded. Your data was not changed.");
            }

            var checksum = ComputeChecksum(partialPath);
            var size = new FileInfo(partialPath).Length;
            File.Move(partialPath, finalPath);

            var manifest = new BackupManifest
            {
                FileName = fileName,
                SchemaVersion = schemaVersion,
                AppVersion = appVersion,
                CreatedAtUtc = now,
                Reason = reason,
                ChecksumSha256 = checksum,
                SizeBytes = size,
                Counts = check.Counts,
            };
            WriteManifest(manifest);

            // The catalog row is a log only; it lives in the database a restore replaces. (A
            // database without schema never gets here: CheckDatabaseFile refuses it above.)
            await using (var connection = source.Open())
            {
                await new SqliteBackupCatalogStore(connection).AddAsync(new BackupCatalogRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    FileName = fileName,
                    SchemaVersion = schemaVersion,
                    AppVersion = appVersion,
                    CreatedAtUtc = now,
                    ChecksumSha256 = checksum,
                    ValidationStatus = BackupValidationStatus.Valid,
                    Reason = reason,
                }, ct);
            }

            return manifest;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    /// <summary>Every backup file in the folder, newest first, with its manifest when it has one.</summary>
    public IReadOnlyList<BackupEntry> List()
    {
        if (!Directory.Exists(backupsDirectory))
        {
            return [];
        }

        var entries = new List<BackupEntry>();
        foreach (var path in Directory.EnumerateFiles(backupsDirectory, "*.db"))
        {
            var fileName = Path.GetFileName(path);
            var info = new FileInfo(path);
            var manifest = TryReadManifest(fileName, out var manifestProblem);
            entries.Add(manifest is null
                ? new BackupEntry(fileName, null, BackupValidationStatus.Unvalidated, manifestProblem ?? "This file has no backup description; it is checked when you choose it.", info.Length, info.LastWriteTimeUtc)
                : new BackupEntry(fileName, manifest, BackupValidationStatus.Valid, null, info.Length, manifest.CreatedAtUtc));
        }

        return [.. entries.OrderByDescending(entry => entry.CreatedAtUtc).ThenByDescending(entry => entry.FileName, StringComparer.Ordinal)];
    }

    /// <summary>The newest backup with a manifest, or null.</summary>
    public BackupManifest? Latest() => List().Select(entry => entry.Manifest).FirstOrDefault(manifest => manifest is not null);

    /// <summary>Checks one backup completely before it may be restored; never mutates it.</summary>
    public BackupInspection Inspect(string fileName)
    {
        var path = ResolveBackupPath(fileName);
        if (!File.Exists(path))
        {
            return new BackupInspection(fileName, false, "That backup file no longer exists.", null, 0, DataCounts.Empty);
        }

        var manifest = TryReadManifest(fileName, out _);
        if (manifest is not null && !string.Equals(manifest.ChecksumSha256, ComputeChecksum(path), StringComparison.Ordinal))
        {
            return new BackupInspection(fileName, false, "This backup changed after it was made (its checksum does not match), so it cannot be trusted.", manifest, manifest.SchemaVersion, manifest.Counts);
        }

        var check = CheckDatabaseFile(path);
        if (!check.Valid)
        {
            return new BackupInspection(fileName, false, check.Problem, manifest, check.SchemaVersion, check.Counts);
        }

        return new BackupInspection(fileName, true, null, manifest, check.SchemaVersion, check.Counts);
    }

    /// <summary>Full path of a backup chosen by name; refuses anything that is not a plain file name in the backups folder.</summary>
    public string ResolveBackupPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("..", StringComparison.Ordinal) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw AppException.Validation("fileName", "Choose a backup from the list.");
        }

        return Path.Combine(backupsDirectory, fileName);
    }

    /// <summary>
    /// Keeps the newest backup of each of the last seven days that have one and of each of the
    /// last four weeks, the three newest safety backups (before a migration, import or restore)
    /// and always the newest backup; deletes the other described backups. Files without a
    /// manifest are never deleted here.
    /// </summary>
    public IReadOnlyList<string> ApplyRetention()
    {
        var described = List().Where(entry => entry.Manifest is not null).Select(entry => entry.Manifest!).ToList();
        var deleted = new List<string>();
        foreach (var fileName in SelectForDeletion(described))
        {
            TryDelete(Path.Combine(backupsDirectory, fileName + ManifestSuffix));
            TryDelete(Path.Combine(backupsDirectory, fileName));
            deleted.Add(fileName);
        }

        return deleted;
    }

    /// <summary>The retention rule as a pure function, newest first by creation instant (UTC calendar).</summary>
    public static IReadOnlyList<string> SelectForDeletion(IReadOnlyList<BackupManifest> backups)
    {
        if (backups.Count == 0)
        {
            return [];
        }

        var ordered = backups.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.FileName, StringComparer.Ordinal).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal) { ordered[0].FileName };

        foreach (var day in ordered.GroupBy(item => DateOnly.FromDateTime(item.CreatedAtUtc.UtcDateTime)).Take(DailyToKeep))
        {
            keep.Add(day.First().FileName);
        }

        foreach (var week in ordered.GroupBy(item => WeekStart(DateOnly.FromDateTime(item.CreatedAtUtc.UtcDateTime))).Take(WeeklyToKeep))
        {
            keep.Add(week.First().FileName);
        }

        foreach (var safety in ordered.Where(item => item.Reason is BackupReason.PreMigration or BackupReason.PreImport or BackupReason.PreRestore).Take(SafetyToKeep))
        {
            keep.Add(safety.FileName);
        }

        return [.. ordered.Where(item => !keep.Contains(item.FileName)).Select(item => item.FileName)];
    }

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// True when no backup was made in the last day and something changed since the newest
    /// backup (PLAN §14: at least daily after changes).
    /// </summary>
    public async Task<bool> ScheduledBackupDueAsync(CancellationToken ct)
    {
        var latest = Latest();
        var now = clock.GetUtcNow();
        if (latest is not null && now - latest.CreatedAtUtc < TimeSpan.FromDays(1))
        {
            return false;
        }

        await using var connection = source.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM OperationReceipts WHERE CommittedAtUtc > $since)
                OR EXISTS (SELECT 1 FROM TaskHistory WHERE RecordedAtUtc > $since)
                OR EXISTS (SELECT 1 FROM ScratchPadItems WHERE UpdatedAtUtc > $since);
            """;
        command.Parameters.AddWithValue("$since", latest is null ? "" : StorageFormats.Instant(latest.CreatedAtUtc));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Opens a database file read-only and checks it: SQLite integrity, foreign keys, and a
    /// schema version this build can open. Also counts the main kinds of data for previews.
    /// </summary>
    public static DatabaseFileCheck CheckDatabaseFile(string path)
    {
        try
        {
            using var connection = OpenFile(path, SqliteOpenMode.ReadOnly);
            using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                if (integrity.ExecuteScalar() as string != "ok")
                {
                    return new DatabaseFileCheck(false, "the file is damaged (SQLite integrity check failed)", 0, DataCounts.Empty);
                }
            }

            using (var keys = connection.CreateCommand())
            {
                keys.CommandText = "PRAGMA foreign_key_check;";
                using var reader = keys.ExecuteReader();
                if (reader.Read())
                {
                    return new DatabaseFileCheck(false, $"the file has broken links between records (table {reader.GetString(0)})", 0, DataCounts.Empty);
                }
            }

            var version = SqliteMigrationRunner.ReadCurrentSchemaVersionUnsafeAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
            if (version > SqliteMigrationRunner.MaxKnownSchemaVersion)
            {
                return new DatabaseFileCheck(false, $"it was made by a newer version of Chaos (data version {version}; this version reads up to {SqliteMigrationRunner.MaxKnownSchemaVersion})", version, DataCounts.Empty);
            }

            if (version < 1)
            {
                return new DatabaseFileCheck(false, "it holds no Chaos data", version, DataCounts.Empty);
            }

            return new DatabaseFileCheck(true, null, version, ReadCounts(connection));
        }
        catch (SqliteException error)
        {
            return new DatabaseFileCheck(false, $"it is not a readable Chaos database ({error.SqliteErrorCode})", 0, DataCounts.Empty);
        }
    }

    /// <summary>Counts of active data in an open database (tables missing in older schemas count as zero).</summary>
    public static DataCounts ReadCounts(SqliteConnection connection)
    {
        long Count(string sql)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                return 0;
            }
        }

        return new DataCounts(
            Count("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;"),
            Count("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL AND Status NOT IN (4,5);"),
            Count("SELECT count(*) FROM Containers WHERE DeletedAtUtc IS NULL;"),
            Count("SELECT count(*) FROM Subtasks WHERE DeletedAtUtc IS NULL;"),
            Count("SELECT count(*) FROM DiaryEntries WHERE DeletedAtUtc IS NULL;"),
            Count("SELECT count(*) FROM ScratchPadItems WHERE DeletedAtUtc IS NULL;"),
            Count("SELECT count(*) FROM Tags WHERE DeletedAtUtc IS NULL;"));
    }

    public static string ComputeChecksum(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>A connection to a file outside the live database: unpooled, so its handle closes with it.</summary>
    internal static SqliteConnection OpenFile(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Turns disk and SQLite failures into readable app errors; a full disk is StorageFull.</summary>
    public static AppException MapFailure(Exception error) => error switch
    {
        AppException app => app,
        IOException io when IsDiskFull(io) => new AppException(AppErrorKind.StorageFull, "There is not enough free disk space to make a backup. Free some space and try again; your data was not changed.", innerException: io),
        SqliteException { SqliteErrorCode: 13 } full => new AppException(AppErrorKind.StorageFull, "There is not enough free disk space to make a backup. Free some space and try again; your data was not changed.", innerException: full),
        IOException or UnauthorizedAccessException or SqliteException => new AppException(AppErrorKind.StorageUnavailable, $"The backup could not be written: {error.Message} Your data was not changed.", innerException: error),
        _ => new AppException(AppErrorKind.StorageUnavailable, "The backup could not be made. Your data was not changed.", innerException: error),
    };

    internal static bool IsDiskFull(IOException error) => (error.HResult & 0xFFFF) is 0x70 or 0x27;

    private BackupManifest? TryReadManifest(string fileName, out string? problem)
    {
        problem = null;
        var path = Path.Combine(backupsDirectory, fileName + ManifestSuffix);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), ManifestJson);
            if (manifest is null || manifest.Format != BackupManifest.FormatName || manifest.FileName != fileName)
            {
                problem = "Its backup description does not match the file.";
                return null;
            }

            return manifest;
        }
        catch (JsonException)
        {
            problem = "Its backup description is unreadable.";
            return null;
        }
    }

    private void WriteManifest(BackupManifest manifest)
    {
        var path = Path.Combine(backupsDirectory, manifest.FileName + ManifestSuffix);
        var temp = path + PartialSuffix;
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, ManifestJson));
        File.Move(temp, path, overwrite: true);
    }

    private void RemoveStalePartials()
    {
        foreach (var stale in Directory.EnumerateFiles(backupsDirectory, "*" + PartialSuffix))
        {
            TryDelete(stale);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Chaos could not delete {0}: {1}", path, error.Message);
        }
    }
}
