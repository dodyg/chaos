using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chaos.Infrastructure.Service;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// Installs the change-capture triggers of PLAN2 §7.3: for every Merge or AppendOnly table,
/// an AFTER INSERT, AFTER UPDATE and AFTER DELETE trigger that pend the changed field groups
/// in SyncPending (or the whole entity as <c>__purge</c> on delete). Every trigger is
/// suppressed while an apply is running (SyncApplyState.Applying), so applying remote changes
/// never captures itself. Pending rows are deduplicated with WHERE NOT EXISTS: SQLite hands
/// trigger-body statements the outer statement's conflict algorithm, so an outer write's
/// default ABORT would defeat INSERT OR IGNORE.
/// <para>
/// The installer runs at startup after migrations. It hashes the catalog; only when the hash
/// differs from SyncState.TriggerHash does it drop every TR_Sync_% trigger and recreate them,
/// in one transaction — so a catalog change (every migration that touches the schema) is
/// picked up, and ordinary starts do no work.
/// </para>
/// </summary>
public static class SyncTriggerInstaller
{
    public const string TriggerPrefix = "TR_Sync_";

    public const string PurgeGroup = "__purge";

    /// <summary>Installs the triggers when the catalog changed; returns true when it did.</summary>
    public static bool Install(SqliteConnection connection)
    {
        var hash = CatalogHash();
        if (ReadTriggerHash(connection) == hash && TriggersPresent(connection))
        {
            return false;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            DropExisting(connection, transaction);
            foreach (var table in SyncCatalog.Ordered())
            {
                if (!CapturesLocalChanges(table))
                {
                    continue;
                }

                foreach (var trigger in new[] { CreateInsertTrigger(table), CreateUpdateTrigger(table), CreateDeleteTrigger(table) })
                {
                    Execute(connection, transaction, trigger);
                }
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE SyncState SET TriggerHash = $hash WHERE Id = 1;";
            update.Parameters.AddWithValue("$hash", hash);
            update.ExecuteNonQuery();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        return true;
    }

    /// <summary>A stable hash over the catalog's meaning, so any change re-creates the triggers.</summary>
    public static string CatalogHash()
    {
        var text = new StringBuilder().Append("client-schema|").Append(SqliteDeviceIdentity.ClientSchema).Append('\n');
        foreach (var table in SyncCatalog.Ordered())
        {
            text.Append(table.Table).Append('|').Append(table.Kind).Append('|').Append(table.Mode).Append('|')
                .Append(string.Join(",", table.Key)).Append('|')
                .Append(string.Join(";", table.Groups.Select(group => group.Name + ":" + string.Join(",", group.Columns)))).Append('|')
                .Append(string.Join(",", table.LocalColumns)).Append('|')
                .Append(string.Join(";", table.UniqueRules.Select(rule => rule.Index + ":" + rule.Kind))).Append('|')
                .Append(string.Join(";", table.DeterministicIndexes.Select(exemption => exemption.Index))).Append('|')
                .Append(table.ApplyOrder).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string? ReadTriggerHash(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TriggerHash FROM SyncState WHERE Id = 1;";
        return command.ExecuteScalar() as string;
    }

    private static bool TriggersPresent(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE $pattern;";
        command.Parameters.AddWithValue("$pattern", TriggerPrefix + "%");
        var present = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        var expected = SyncCatalog.Tables.Count(CapturesLocalChanges) * 3;
        return present == expected;
    }

    internal static bool CapturesLocalChanges(SyncTable table) =>
        table.Mode != SyncMode.LocalOnly && SyncKinds.Find(table.Kind)?.Route.Kind != SyncRouteKind.ServerOnly;

    internal static IEnumerable<SyncFieldGroup> CapturedGroups(SyncTable table) =>
        table.Groups.Where(group => SyncKinds.Find(table.Kind)?.Group(group.Name)?.Since <= SqliteDeviceIdentity.ClientSchema);

    private static void DropExisting(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Drop by listing, not by name list in code, so a renamed or removed table's old
        // triggers cannot survive a catalog change.
        var names = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE $pattern;";
            list.Parameters.AddWithValue("$pattern", TriggerPrefix + "%");
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            Execute(connection, transaction, "DROP TRIGGER IF EXISTS " + SyncSql.Quote(name) + ";");
        }
    }

    // The pending inserts are deduplicated with WHERE NOT EXISTS rather than INSERT OR
    // IGNORE: SQLite gives statements inside a trigger program the conflict-resolution
    // algorithm of the statement that fired the trigger, so an outer write's default ABORT
    // would override OR IGNORE and a repeated group would fail the SyncPending key.
    private static string CreateInsertTrigger(SyncTable table)
    {
        var body = string.Join("\n  ", CapturedGroups(table).Select(group =>
        {
            return PendingInsert(table, Key("NEW", table.Key), group.Name, condition: null);
        }));
        return $"""
            CREATE TRIGGER {TriggerName(table, "Insert")} AFTER INSERT ON {SyncSql.Quote(table.Table)}
                WHEN (SELECT Applying FROM SyncApplyState WHERE Id = 1) = 0
            BEGIN
              {body}
            END;
            """;
    }

    private static string CreateUpdateTrigger(SyncTable table)
    {
        var body = string.Join("\n  ", CapturedGroups(table).Select(group =>
        {
            var changed = string.Join(" OR ", group.Columns.Select(column => $"OLD.{SyncSql.Quote(column)} IS NOT NEW.{SyncSql.Quote(column)}"));
            return PendingInsert(table, Key("NEW", table.Key), group.Name, condition: changed);
        }));
        return $"""
            CREATE TRIGGER {TriggerName(table, "Update")} AFTER UPDATE ON {SyncSql.Quote(table.Table)}
                WHEN (SELECT Applying FROM SyncApplyState WHERE Id = 1) = 0
            BEGIN
              {body}
            END;
            """;
    }

    private static string CreateDeleteTrigger(SyncTable table)
    {
        return $"""
            CREATE TRIGGER {TriggerName(table, "Delete")} AFTER DELETE ON {SyncSql.Quote(table.Table)}
                WHEN (SELECT Applying FROM SyncApplyState WHERE Id = 1) = 0
            BEGIN
              {PendingInsert(table, Key("OLD", table.Key), PurgeGroup, condition: null)}
            END;
            """;
    }

    private static string TriggerName(SyncTable table, string verb) => SyncSql.Quote($"{TriggerPrefix}{table.Table}_{verb}");

    /// <summary>One deduplicated SyncPending insert of a trigger body; kind and group are escaped literals.</summary>
    private static string PendingInsert(SyncTable table, string key, string group, string? condition)
    {
        var kind = SyncSql.Literal(table.Kind);
        var groupLiteral = SyncSql.Literal(group);
        var where = condition is null ? string.Empty : $"({condition}) AND ";
        return $"INSERT INTO SyncPending (Kind, EntityKey, GroupName) SELECT {kind}, {key}, {groupLiteral} WHERE {where}NOT EXISTS (SELECT 1 FROM SyncPending WHERE Kind = {kind} AND EntityKey = {key} AND GroupName = {groupLiteral});";
    }

    /// <summary>The entity key: the primary key, or the composite key joined with '|' (§7.3).</summary>
    private static string Key(string prefix, IReadOnlyList<string> key) =>
        string.Join(" || '|' || ", key.Select(column => $"{prefix}.{SyncSql.Quote(column)}"));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
