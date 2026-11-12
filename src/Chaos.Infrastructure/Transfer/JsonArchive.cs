using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Transfer;

/// <summary>All rows of one table, columns in table order. Values are long, double, string, byte[] or null.</summary>
public sealed record ArchiveTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

public sealed record ArchiveFileEntry(string Path, string Sha256, long Bytes);

/// <summary>manifest.json of a Chaos JSON archive.</summary>
public sealed record ArchiveManifest
{
    public const string FormatName = "chaos-archive";
    public const int CurrentFormatVersion = 1;

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required long SchemaVersion { get; init; }
    public required string AppVersion { get; init; }
    public required DateTimeOffset ExportedAtUtc { get; init; }
    public required string ImportPolicy { get; init; }
    public required IReadOnlyList<ArchiveFileEntry> Files { get; init; }
    public required IReadOnlyDictionary<string, long> Tables { get; init; }
    public required IReadOnlyList<string> NotIncluded { get; init; }
}

public sealed record ArchiveContent(ArchiveManifest Manifest, IReadOnlyList<ArchiveTable> Tables)
{
    public ArchiveTable? Table(string name) => Tables.FirstOrDefault(table => table.Name == name);
}

/// <summary>
/// The versioned, lossless JSON archive (PLAN §14): a ZIP with manifest.json (format, schema
/// version, import policy, per-file SHA-256) and data.json (every row of every user-data table,
/// with stable IDs). Rebuildable or machine-specific data is left out: migration bookkeeping,
/// the backup log, operation receipts, the search index, and OS notification schedule IDs and
/// retry state (reset to "not scheduled"). The archive is not a copy of the live WAL database.
/// Restoring builds a fresh database at the archive's schema version, loads every row, then
/// migrates it forward, so archives from older versions restore into newer ones.
/// </summary>
public static class JsonArchive
{
    public const string ManifestEntry = "manifest.json";
    public const string DataEntry = "data.json";

    public const string ImportPolicyText =
        "Restore replaces all local data with this archive after a safety backup. " +
        "Merge adds tasks whose IDs are not already present (with their subtasks, tags and diary) and keeps local data on any ID conflict.";

    /// <summary>Tables never exported, with the reason shown in the manifest.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExcludedTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SchemaMigrations"] = "rebuilt by the importing app",
        ["BackupCatalog"] = "log of this device's backups",
        ["OperationReceipts"] = "retry bookkeeping",
        ["SearchDocuments"] = "search index, rebuilt",
        ["SearchIndexState"] = "search index, rebuilt",
        ["GitHubSyncRuns"] = "device synchronization diagnostics",
        ["GitHubSyncCheckpoints"] = "device pagination, query and pre-import backup bookkeeping",
    };

    /// <summary>Columns reset on export because they describe this machine, not the data.</summary>
    private static readonly IReadOnlyDictionary<(string Table, string Column), object?> MachineColumns = new Dictionary<(string, string), object?>
    {
        [("NotificationJobs", "OsState")] = 0L,
        [("NotificationJobs", "OsNotificationId")] = null,
        [("NotificationJobs", "OsAttempts")] = 0L,
        [("NotificationJobs", "OsLastAttemptUtc")] = null,
        [("NotificationJobs", "OsError")] = null,
        [("ImportBatches", "BackupFileName")] = null,
    };

    private static bool IsExcluded(string table) =>
        ExcludedTables.ContainsKey(table) || table.StartsWith("SearchIndex", StringComparison.OrdinalIgnoreCase) || table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads every exportable table, parents before children, rows in primary-key order. Run inside a read transaction for a consistent snapshot.</summary>
    public static async Task<IReadOnlyList<ArchiveTable>> DumpAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tables = new List<ArchiveTable>();
        foreach (var name in OrderedTables(connection))
        {
            ct.ThrowIfCancellationRequested();
            var info = TableInfo(connection, name);
            var columns = info.Select(column => column.Name).ToList();
            var keys = info.Where(column => column.PrimaryKeyIndex > 0).OrderBy(column => column.PrimaryKeyIndex).Select(column => Quote(column.Name)).ToList();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(name)} ORDER BY {(keys.Count > 0 ? string.Join(", ", keys) : "rowid")};";
            var rows = new List<object?[]>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[columns.Count];
                for (var index = 0; index < columns.Count; index++)
                {
                    row[index] = MachineColumns.TryGetValue((name, columns[index]), out var reset) ? reset :
                        name.StartsWith("GitHub", StringComparison.Ordinal) && columns[index] == "ErrorAttempts" ? 0L :
                        name.StartsWith("GitHub", StringComparison.Ordinal) &&
                        (columns[index].EndsWith("Etag", StringComparison.Ordinal) || columns[index] is "IssuesCursorUtc" or "CommentsCursorUtc" or "RateLimitResetUtc" or "NextAttemptUtc") ? null :
                        reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                rows.Add(row);
            }

            tables.Add(new ArchiveTable(name, columns, rows));
        }

        return tables;
    }

    /// <summary>Writes a complete archive ZIP to <paramref name="output"/> from an open connection.</summary>
    public static async Task<ArchiveManifest> WriteAsync(SqliteConnection connection, Stream output, string appVersion, DateTimeOffset now, CancellationToken ct)
    {
        long schemaVersion;
        IReadOnlyList<ArchiveTable> tables;
        using (var snapshot = connection.BeginTransaction(deferred: true))
        {
            schemaVersion = await SqliteMigrationRunner.ReadCurrentSchemaVersionUnsafeAsync(connection, ct);
            tables = await DumpAsync(connection, ct);
            snapshot.Commit();
        }

        var data = SerializeTables(tables);
        var manifest = new ArchiveManifest
        {
            SchemaVersion = schemaVersion,
            AppVersion = appVersion,
            ExportedAtUtc = now,
            ImportPolicy = ImportPolicyText,
            Files = [new ArchiveFileEntry(DataEntry, Sha256(data), data.Length)],
            Tables = tables.ToDictionary(table => table.Name, table => (long)table.Rows.Count, StringComparer.Ordinal),
            NotIncluded = [.. ExcludedTables.Select(pair => $"{pair.Key}: {pair.Value}"), "SearchIndex: search index, rebuilt", "NotificationJobs OS schedule columns: belong to this device"],
        };

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(zip, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, BackupManager.ManifestJson), ct);
            await WriteEntryAsync(zip, DataEntry, data, ct);
        }

        return manifest;
    }

    /// <summary>Opens and validates an archive: only the two known entries, safe sizes, matching checksums, a known format and a schema this build can read.</summary>
    public static ArchiveContent Read(Stream input)
    {
        using var zip = SafeArchive.Open(input);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals([ManifestEntry, DataEntry]))
        {
            throw AppException.Validation("file", "This is not a Chaos archive: it must contain exactly manifest.json and data.json.");
        }

        ArchiveManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(SafeArchive.ReadEntry(zip.GetEntry(ManifestEntry)!), BackupManager.ManifestJson)
                ?? throw new JsonException("empty manifest");
        }
        catch (JsonException)
        {
            throw AppException.Validation("file", "The archive's manifest.json is unreadable.");
        }

        if (manifest.Format != ArchiveManifest.FormatName || manifest.FormatVersion != ArchiveManifest.CurrentFormatVersion)
        {
            throw AppException.Validation("file", $"This archive uses a format Chaos does not read ({manifest.Format} version {manifest.FormatVersion}).");
        }

        if (manifest.SchemaVersion < 1 || manifest.SchemaVersion > SqliteMigrationRunner.MaxKnownSchemaVersion)
        {
            throw AppException.Validation("file", $"This archive was made by a newer version of Chaos (data version {manifest.SchemaVersion}). Install that version to read it.");
        }

        var data = SafeArchive.ReadEntry(zip.GetEntry(DataEntry)!);
        var declared = manifest.Files.FirstOrDefault(file => file.Path == DataEntry);
        if (declared is null || declared.Sha256 != Sha256(data) || declared.Bytes != data.Length)
        {
            throw AppException.Validation("file", "The archive's data does not match its checksum, so it may be damaged or edited.");
        }

        return new ArchiveContent(manifest, DeserializeTables(data));
    }

    /// <summary>
    /// Builds a fresh database file from an archive at <paramref name="targetPath"/>: schema at
    /// the archive's version, every row inserted in one transaction with foreign keys checked at
    /// commit, then the remaining migrations and the search index. Throws when any row fails.
    /// </summary>
    public static async Task BuildDatabaseAsync(ArchiveContent archive, string targetPath, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            BackupManager.TryDelete(targetPath + suffix);
        }

        try
        {
            using (var connection = BackupManager.OpenFile(targetPath, SqliteOpenMode.ReadWriteCreate))
            {
                Execute(connection, "PRAGMA foreign_keys=ON;");
                var version = await SqliteMigrationRunner.MigrateAsync(connection, ct, archive.Manifest.SchemaVersion);
                if (version != archive.Manifest.SchemaVersion)
                {
                    throw AppException.Validation("file", "This archive's data version is not one this build can recreate.");
                }

                var known = OrderedTables(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    Execute(connection, "PRAGMA defer_foreign_keys=ON;");
                    foreach (var table in archive.Tables.OrderBy(table => IndexOf(known, table.Name)))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (IsExcluded(table.Name) || !known.Contains(table.Name, StringComparer.Ordinal))
                        {
                            throw AppException.Validation("file", $"The archive has a table this version does not know ({table.Name}).");
                        }

                        var columns = TableInfo(connection, table.Name).Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
                        var unknown = table.Columns.FirstOrDefault(column => !columns.Contains(column));
                        if (unknown is not null)
                        {
                            throw AppException.Validation("file", $"The archive has a column this version does not know ({table.Name}.{unknown}).");
                        }

                        using var insert = connection.CreateCommand();
                        insert.CommandText = $"INSERT OR REPLACE INTO {Quote(table.Name)} ({string.Join(", ", table.Columns.Select(Quote))}) VALUES ({string.Join(", ", table.Columns.Select((_, index) => "$p" + index))});";
                        var parameters = table.Columns.Select((_, index) => insert.Parameters.Add("$p" + index, SqliteType.Text)).ToArray();
                        var rowNumber = 0;
                        foreach (var row in table.Rows)
                        {
                            rowNumber++;
                            if (row.Length != table.Columns.Count)
                            {
                                throw AppException.Validation("file", $"Row {rowNumber} of {table.Name} has the wrong number of values.");
                            }

                            for (var index = 0; index < row.Length; index++)
                            {
                                parameters[index].SqliteType = row[index] switch { long => SqliteType.Integer, double => SqliteType.Real, byte[] => SqliteType.Blob, _ => SqliteType.Text };
                                parameters[index].Value = row[index] ?? DBNull.Value;
                            }

                            try
                            {
                                insert.ExecuteNonQuery();
                            }
                            catch (SqliteException error)
                            {
                                throw AppException.Validation("file", $"Row {rowNumber} of {table.Name} was refused: {error.Message}");
                            }
                        }
                    }

                    try
                    {
                        transaction.Commit();
                    }
                    catch (SqliteException error) when (error.SqliteErrorCode == 19)
                    {
                        throw AppException.Validation("file", "The archive has records that point to missing records, so it was not restored.");
                    }
                }

                await SqliteMigrationRunner.MigrateAsync(connection, ct);
                await SearchIndexWriter.EnsureCurrentAsync(connection, now, ct);
            }

            var check = BackupManager.CheckDatabaseFile(targetPath);
            if (!check.Valid)
            {
                throw AppException.Validation("file", $"The data rebuilt from this archive failed its check: {check.Problem}.");
            }
        }
        catch
        {
            BackupManager.TryDelete(targetPath);
            throw;
        }
    }

    /// <summary>Tables in dependency order (referenced tables first), excluding rebuildable ones.</summary>
    public static IReadOnlyList<string> OrderedTables(SqliteConnection connection)
    {
        var names = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (!IsExcluded(name))
                {
                    names.Add(name);
                }
            }
        }

        var dependencies = names.ToDictionary(name => name, name =>
        {
            using var keys = connection.CreateCommand();
            keys.CommandText = "SELECT DISTINCT \"table\" FROM pragma_foreign_key_list($name);";
            keys.Parameters.AddWithValue("$name", name);
            var parents = new List<string>();
            using var reader = keys.ExecuteReader();
            while (reader.Read())
            {
                var parent = reader.GetString(0);
                if (!string.Equals(parent, name, StringComparison.OrdinalIgnoreCase))
                {
                    parents.Add(parent);
                }
            }

            return parents;
        }, StringComparer.Ordinal);

        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string name)
        {
            if (!visited.Add(name))
            {
                return;
            }

            foreach (var parent in dependencies.GetValueOrDefault(name) ?? [])
            {
                var match = names.FirstOrDefault(candidate => string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    Visit(match);
                }
            }

            ordered.Add(name);
        }

        foreach (var name in names)
        {
            Visit(name);
        }

        return ordered;
    }

    private sealed record ColumnInfo(string Name, int PrimaryKeyIndex);

    private static List<ColumnInfo> TableInfo(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, pk FROM pragma_table_info($name) ORDER BY cid;";
        command.Parameters.AddWithValue("$name", table);
        var columns = new List<ColumnInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new ColumnInfo(reader.GetString(0), reader.GetInt32(1)));
        }

        return columns;
    }

    private static int IndexOf(IReadOnlyList<string> ordered, string name)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            if (string.Equals(ordered[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    internal static byte[] SerializeTables(IReadOnlyList<ArchiveTable> tables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tables");
            foreach (var table in tables)
            {
                writer.WriteStartObject();
                writer.WriteString("name", table.Name);
                writer.WriteStartArray("columns");
                foreach (var column in table.Columns)
                {
                    writer.WriteStringValue(column);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("rows");
                foreach (var row in table.Rows)
                {
                    writer.WriteStartArray();
                    foreach (var value in row)
                    {
                        switch (value)
                        {
                            case null: writer.WriteNullValue(); break;
                            case long number: writer.WriteNumberValue(number); break;
                            case double real:
                                writer.WriteStartObject();
                                writer.WriteString("$real", real.ToString("R", CultureInfo.InvariantCulture));
                                writer.WriteEndObject();
                                break;
                            case byte[] bytes:
                                writer.WriteStartObject();
                                writer.WriteBase64String("$base64", bytes);
                                writer.WriteEndObject();
                                break;
                            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                        }
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static IReadOnlyList<ArchiveTable> DeserializeTables(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
            var tables = new List<ArchiveTable>();
            foreach (var table in document.RootElement.GetProperty("tables").EnumerateArray())
            {
                var name = table.GetProperty("name").GetString() ?? throw new JsonException("table name");
                var columns = table.GetProperty("columns").EnumerateArray().Select(column => column.GetString() ?? throw new JsonException("column")).ToList();
                var rows = new List<object?[]>();
                foreach (var row in table.GetProperty("rows").EnumerateArray())
                {
                    rows.Add([.. row.EnumerateArray().Select(ReadValue)]);
                }

                tables.Add(new ArchiveTable(name, columns, rows));
            }

            return tables;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw AppException.Validation("file", "The archive's data.json is not in the expected shape.");
        }
    }

    private static object? ReadValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var number) ? number : throw new FormatException("non-integer number"),
        JsonValueKind.Object when value.TryGetProperty("$base64", out var bytes) => bytes.GetBytesFromBase64(),
        JsonValueKind.Object when value.TryGetProperty("$real", out var real) => double.Parse(real.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new FormatException("unsupported value"),
    };

    private static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, ct);
    }

    internal static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string Utf8(byte[] data) => Encoding.UTF8.GetString(data);
}
