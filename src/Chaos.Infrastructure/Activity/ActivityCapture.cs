using System.Collections.Concurrent;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Activity;

/// <summary>
/// PLAN2 §6.4 rules 1-3. Runs inside the store's write transaction, immediately before the sync
/// flush (which deletes <c>SyncPending</c> while sync is off). It turns the pended
/// (Kind, EntityKey) pairs plus the device-only notes into one ActivityLog row with its
/// entities and versions. A transaction that changed nothing writes nothing; a rollback takes the
/// row with it because it is written in the same transaction.
/// </summary>
internal static class ActivityCapture
{
    private static readonly Dictionary<string, SyncTable> TablesByKind =
        SyncCatalog.Tables.ToDictionary(table => table.Kind, StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, bool> HasVersion = new(StringComparer.Ordinal);

    /// <returns>The new <c>Seq</c>, or null when the transaction changed nothing.</returns>
    public static long? Capture(SqliteConnection connection, ActivityNotes notes, TimeProvider clock)
    {
        var entities = new Dictionary<(string Kind, string Key), (string Op, long? Version)>();

        var pended = new List<(string Kind, string Key)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT DISTINCT Kind, EntityKey FROM SyncPending ORDER BY Kind, EntityKey;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                pended.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (kind, key) in pended)
        {
            if (!TablesByKind.TryGetValue(kind, out var table))
            {
                entities[(kind, key)] = ("upsert", null);
                continue;
            }

            var version = ReadVersion(connection, table, key, out var exists);
            // A purge group whose row is gone, or any pended row that vanished later in the same
            // transaction, is a delete; a row deleted and re-inserted under its key is an upsert.
            entities[(kind, key)] = (exists ? "upsert" : "delete", exists ? version : null);
        }

        foreach (var (kind, key, op, version) in notes.Items)
        {
            entities[(kind, key)] = (op == ActivityOp.Delete ? "delete" : "upsert", version);
        }

        if (entities.Count == 0)
        {
            return null;
        }

        var scope = ActivityScope.Current;
        if (scope is null)
        {
            if (ActivityScope.Strict)
            {
                throw new InvalidOperationException(
                    "A write changed data outside an ActivityScope. Open one with ActivityScope.Rpc/Open at the entry point (PLAN2 §6.4).");
            }
        }
        else if (ActivityScope.Strict && !ActivityActions.IsKnown(scope.Action))
        {
            throw new InvalidOperationException($"'{scope.Action}' is not in ActivityActions; add it to the closed list (PLAN2 §6.4 rule 6).");
        }

        var source = scope?.Source ?? ActivitySources.System;
        var action = scope?.Action ?? ActivityActions.Unscoped;
        var operationId = scope?.OperationId ?? Guid.NewGuid().ToString("D");

        long seq;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ActivityLog (OperationId, OccurredAtUtc, Source, Action, Outcome, JobRunId)
                VALUES ($op, $at, $source, $action, 'ok', $job);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$op", operationId);
            insert.Parameters.AddWithValue("$at", StorageFormats.Instant(clock.GetUtcNow()));
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$action", action);
            insert.Parameters.AddWithValue("$job", (object?)scope?.JobRunId ?? DBNull.Value);
            seq = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        foreach (var ((kind, key), (op, version)) in entities)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ActivityLogEntities (Seq, Kind, EntityKey, Op, Version) VALUES ($seq, $kind, $key, $op, $version);";
            insert.Parameters.AddWithValue("$seq", seq);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$op", op);
            insert.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        return seq;
    }

    private static long? ReadVersion(SqliteConnection connection, SyncTable table, string key, out bool exists)
    {
        var keyValues = key.Split('|');
        var withVersion = HasVersion.GetOrAdd(table.Table, name => TableHasVersion(connection, name));
        using var select = connection.CreateCommand();
        var where = string.Join(" AND ", table.Key.Select((column, index) => $"{SyncSql.Quote(column)} = $k{index}"));
        select.CommandText = $"SELECT {(withVersion ? SyncSql.Quote("Version") : "1")} FROM {SyncSql.Quote(table.Table)} WHERE {where};";
        for (var index = 0; index < keyValues.Length && index < table.Key.Count; index++)
        {
            select.Parameters.AddWithValue($"$k{index}", keyValues[index]);
        }

        var value = select.ExecuteScalar();
        exists = value is not null && value is not DBNull;
        return exists && withVersion ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;
    }

    private static bool TableHasVersion(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info({SyncSql.Literal(table)}) WHERE name = 'Version';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
}
