using System.Globalization;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// The PLAN2 §7.3 flush: groups the pending rows by entity, ticks the hybrid logical clock
/// once per entity, reads each entity's current row by key, and writes one SyncOutbox change
/// carrying the pended groups with their current values and the new clock (or a purge for
/// entities whose delete trigger fired). SyncClocks records the winning clock per group and
/// SyncState.LastHlc the last issued one. Sync stays disabled until P13, so until then the
/// flush is the cheap "delete pending" path.
/// </summary>
public sealed class SyncFlush(TimeProvider clock) : ISyncCapture
{
    private static readonly Dictionary<string, SyncTable> TablesByKind =
        SyncCatalog.Tables.Where(table => table.Mode != SyncMode.LocalOnly).ToDictionary(table => table.Kind, StringComparer.Ordinal);

    public void Flush(SqliteConnection connection)
    {
        var enabled = Scalar(connection, "SELECT Enabled FROM SyncState WHERE Id = 1;");
        if (enabled == 0)
        {
            Execute(connection, "DELETE FROM SyncPending;");
            return;
        }

        var device = Text(connection, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no device identity, so changes cannot be captured for sync.");
        var clockState = new HybridClock(Guid.Parse(device), clock);
        var last = Text(connection, "SELECT LastHlc FROM SyncState WHERE Id = 1;");
        if (last is not null)
        {
            clockState.Restore(Hlc.Parse(last));
        }

        var entities = new List<(string Kind, string Key, SortedSet<string> Groups)>();
        using (var pending = connection.CreateCommand())
        {
            pending.CommandText = "SELECT Kind, EntityKey, GroupName FROM SyncPending ORDER BY Kind, EntityKey, GroupName;";
            using var reader = pending.ExecuteReader();
            (string Kind, string Key, SortedSet<string> Groups)? current = null;
            while (reader.Read())
            {
                var kind = reader.GetString(0);
                var key = reader.GetString(1);
                var group = reader.GetString(2);
                if (current is { } entry && entry.Kind == kind && entry.Key == key)
                {
                    entry.Groups.Add(group);
                }
                else
                {
                    current = (kind, key, [group]);
                    entities.Add(current.Value);
                }
            }
        }

        var now = StorageFormats.Instant(clock.GetUtcNow());
        foreach (var (kind, key, groups) in entities)
        {
            if (groups.Contains(SyncTriggerInstaller.PurgeGroup))
            {
                // A hard delete in the same transaction outranks anything the row's inserts and
                // updates pended: the whole entity travels as a purge, and its clocks reset.
                WriteOutbox(connection, NewChangeId(), kind, key, "purge", PurgePayload(kind, key), now);
                Execute(connection, $"DELETE FROM SyncClocks WHERE Kind = {Literal(kind)} AND EntityKey = {Literal(key)};");
                continue;
            }

            var changeClock = clockState.TickLocal().Encode();
            var table = TablesByKind[kind];
            var payload = EntityPayload(table, key, groups, changeClock, connection);
            WriteOutbox(connection, NewChangeId(), kind, key, "upsert", payload, now);
            foreach (var group in groups)
            {
                using var upsert = connection.CreateCommand();
                upsert.CommandText = $"""
                    INSERT INTO SyncClocks (Kind, EntityKey, GroupName, Hlc) VALUES ({Literal(kind)}, {Literal(key)}, {Literal(group)}, {Literal(changeClock)})
                    ON CONFLICT (Kind, EntityKey, GroupName) DO UPDATE SET Hlc = excluded.Hlc;
                    """;
                upsert.ExecuteNonQuery();
            }
        }

        using var state = connection.CreateCommand();
        state.CommandText = "UPDATE SyncState SET LastHlc = $hlc WHERE Id = 1;";
        state.Parameters.AddWithValue("$hlc", clockState.Last.Encode());
        state.ExecuteNonQuery();

        Execute(connection, "DELETE FROM SyncPending;");
    }

    private static void WriteOutbox(SqliteConnection connection, string changeId, string kind, string key, string op, string payload, string createdAtUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO SyncOutbox (ChangeId, Kind, EntityKey, Op, PayloadJson, CreatedAtUtc)
            VALUES ({Literal(changeId)}, {Literal(kind)}, {Literal(key)}, {Literal(op)}, $payload, {Literal(createdAtUtc)});
            """;
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
    }

    /// <summary>The pended groups with the row's current values and the change's clock, in the §7.12 change shape.</summary>
    private static string EntityPayload(SyncTable table, string key, IReadOnlySet<string> groups, string changeClock, SqliteConnection connection)
    {
        var keyValues = key.Split('|');
        if (keyValues.Length != table.Key.Count)
        {
            throw new AppException(AppErrorKind.StorageUnavailable, $"A pending change for {table.Kind} has a key of the wrong shape.");
        }

        using var select = connection.CreateCommand();
        var where = string.Join(" AND ", table.Key.Select((column, index) => $"{Quote(column)} = $k{index}"));
        select.CommandText = $"SELECT * FROM {Quote(table.Table)} WHERE {where};";
        for (var index = 0; index < keyValues.Length; index++)
        {
            select.Parameters.AddWithValue($"$k{index}", keyValues[index]);
        }

        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            // The row the triggers captured is gone: something deleted it later in the same
            // transaction, so the change is a purge, not an upsert.
            return PurgePayload(table.Kind, key);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("changeId", Guid.NewGuid().ToString());
            writer.WriteString("kind", table.Kind);
            writer.WriteString("key", key);
            writer.WriteString("op", "upsert");
            writer.WriteStartObject("groups");
            foreach (var groupName in groups)
            {
                var group = table.Groups.First(candidate => candidate.Name == groupName);
                writer.WriteStartObject(group.Name);
                writer.WriteString("hlc", changeClock);
                writer.WriteStartObject("values");
                foreach (var column in group.Columns)
                {
                    WriteValue(writer, column, reader, reader.GetOrdinal(column));
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string PurgePayload(string kind, string key)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("changeId", Guid.NewGuid().ToString());
            writer.WriteString("kind", kind);
            writer.WriteString("key", key);
            writer.WriteString("op", "purge");
            writer.WriteStartObject("groups");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, SqliteDataReader reader, int ordinal)
    {
        writer.WritePropertyName(name);
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNullValue();
        }
        else if (reader.GetFieldType(ordinal) == typeof(long))
        {
            writer.WriteNumberValue(reader.GetInt64(ordinal));
        }
        else
        {
            writer.WriteStringValue(reader.GetString(ordinal));
        }
    }

    private static string NewChangeId() => Guid.NewGuid().ToString();

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
