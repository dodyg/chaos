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

    /// <summary>Raised when a flush wrote outbox rows (just before the transaction commits); the sync engine wakes on it and debounces.</summary>
    public event Action? OutboxWritten;

    public void Flush(SqliteConnection connection)
    {
        var enabled = SyncSql.Scalar(connection, "SELECT Enabled FROM SyncState WHERE Id = 1;");
        if (enabled == 0)
        {
            SyncSql.Execute(connection, "DELETE FROM SyncPending;");
            return;
        }

        var device = SyncSql.Text(connection, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no device identity, so changes cannot be captured for sync.");
        var clockState = new HybridClock(Guid.Parse(device), clock);
        var last = SyncSql.Text(connection, "SELECT LastHlc FROM SyncState WHERE Id = 1;");
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
        foreach (var (kind, key, pended) in entities)
        {
            var table = TablesByKind[kind];
            IReadOnlySet<string> groups = pended;
            if (pended.Contains(SyncTriggerInstaller.PurgeGroup))
            {
                if (!RowExists(connection, table, key))
                {
                    // A hard delete in the same transaction outranks anything the row's inserts
                    // and updates pended: the whole entity travels as a purge. Its clocks stay as
                    // a watermark, so a replayed older change cannot resurrect the row, while a
                    // genuinely newer insert (the row re-created) still wins.
                    var purgeId = NewChangeId();
                    WriteOutbox(connection, purgeId, kind, key, "purge", PurgePayload(purgeId, kind, key), now);
                    continue;
                }

                // Deleted and re-inserted under the same key in one transaction (a week plan's
                // content is replaced that way): the row still exists, so it travels whole as an
                // upsert of every group; a purge would delete it on every other device.
                groups = table.Groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
            }

            var changeId = NewChangeId();
            var changeClock = clockState.TickLocal().Encode();
            var (op, payload) = EntityPayload(table, key, groups, changeId, changeClock, connection);
            WriteOutbox(connection, changeId, kind, key, op, payload, now);
            if (op == "upsert")
            {
                foreach (var group in groups)
                {
                    // The flush just issued this clock: it always replaces the stored one.
                    SyncSql.UpsertClock(connection, kind, key, group, changeClock, onlyIfNewer: false);
                }
            }
        }

        using var state = connection.CreateCommand();
        state.CommandText = "UPDATE SyncState SET LastHlc = $hlc WHERE Id = 1;";
        state.Parameters.AddWithValue("$hlc", clockState.Last.Encode());
        state.ExecuteNonQuery();

        SyncSql.Execute(connection, "DELETE FROM SyncPending;");
        if (entities.Count > 0)
        {
            OutboxWritten?.Invoke();
        }
    }

    private static bool RowExists(SqliteConnection connection, SyncTable table, string key)
    {
        using var select = SelectByKey(connection, table, key, "1");
        return select.ExecuteScalar() is not null;
    }

    private static SqliteCommand SelectByKey(SqliteConnection connection, SyncTable table, string key, string columns)
    {
        var keyValues = key.Split('|');
        if (keyValues.Length != table.Key.Count)
        {
            throw new AppException(AppErrorKind.StorageUnavailable, $"A pending change for {table.Kind} has a key of the wrong shape.");
        }

        var select = connection.CreateCommand();
        var where = string.Join(" AND ", table.Key.Select((column, index) => $"{SyncSql.Quote(column)} = $k{index}"));
        select.CommandText = $"SELECT {columns} FROM {SyncSql.Quote(table.Table)} WHERE {where};";
        for (var index = 0; index < keyValues.Length; index++)
        {
            select.Parameters.AddWithValue($"$k{index}", keyValues[index]);
        }

        return select;
    }

    private static void WriteOutbox(SqliteConnection connection, string changeId, string kind, string key, string op, string payload, string createdAtUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO SyncOutbox (ChangeId, Kind, EntityKey, Op, PayloadJson, CreatedAtUtc)
            VALUES ({SyncSql.Literal(changeId)}, {SyncSql.Literal(kind)}, {SyncSql.Literal(key)}, {SyncSql.Literal(op)}, $payload, {SyncSql.Literal(createdAtUtc)});
            """;
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The pended groups with the row's current values and the change's clock, in the §7.12 change
    /// shape, with the op the outbox row must carry (a purge when the row is gone).
    /// </summary>
    private static (string Op, string Payload) EntityPayload(
        SyncTable table, string key, IReadOnlySet<string> groups, string changeId, string changeClock, SqliteConnection connection)
    {
        using var select = SelectByKey(connection, table, key, "*");
        using var reader = select.ExecuteReader();
        if (!reader.Read())
        {
            // The row the triggers captured is gone: something deleted it later in the same
            // transaction, so the change is a purge, not an upsert.
            return ("purge", PurgePayload(changeId, table.Kind, key));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("changeId", changeId);
            writer.WriteString("kind", table.Kind);
            writer.WriteString("key", key);
            writer.WriteString("op", "upsert");
            writer.WriteStartObject("groups");
            foreach (var groupName in groups.Order(StringComparer.Ordinal))
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

        return ("upsert", Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string PurgePayload(string changeId, string kind, string key)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("changeId", changeId);
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
}
