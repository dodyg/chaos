using System.Globalization;
using Chaos.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Transfer;

/// <summary>
/// PLAN3 §8.7: an archive that holds shared spaces is restored with each shared space's work as a Personal copy under
/// new IDs, and without its membership. The copies cannot collide with the shared originals the service still holds,
/// and nothing in the archive can make this device a member of anything. Personal rows that pointed at the shared work
/// (My Day, week plans, reminders, personal tags) follow it to the copies.
/// </summary>
internal static class SharedSpaceCopies
{
    private const string Map = "temp.ArchiveIdMap";

    public static int Convert(SqliteConnection connection)
    {
        var personal = Text(connection, "SELECT Id FROM Spaces WHERE Kind = 1 AND DeletedAtUtc IS NULL ORDER BY CreatedAtUtc, Id LIMIT 1;");
        var shared = new List<(string Id, string Name)>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT Id, Name FROM Spaces WHERE Kind = 2 ORDER BY Id;";
            using var reader = list.ExecuteReader();
            while (reader.Read()) shared.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (shared.Count == 0 || personal is null)
        {
            SyncSqlExecute(connection, "DELETE FROM SpaceMembers;");
            return 0;
        }

        var columns = IdColumns(connection);
        using var transaction = connection.BeginTransaction();
        SyncSqlExecute(connection, "PRAGMA defer_foreign_keys = ON;");
        foreach (var (spaceId, name) in shared)
        {
            var entities = SyncEviction.SpaceEntities(connection, spaceId);
            SyncSqlExecute(connection, $"DROP TABLE IF EXISTS {Map}; CREATE TEMP TABLE {Map["temp.".Length..]} (Old TEXT NOT NULL PRIMARY KEY, New TEXT NOT NULL);");
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT OR IGNORE INTO {Map} (Old, New) VALUES ($old, $new);";
                var old = insert.Parameters.Add("$old", SqliteType.Text);
                var fresh = insert.Parameters.Add("$new", SqliteType.Text);
                old.Value = spaceId;
                fresh.Value = personal;
                insert.ExecuteNonQuery();
                // Single-column keys get new IDs; composite link keys are made of those columns and follow them.
                foreach (var (kind, key) in entities)
                {
                    if (kind is "space" or "space_member" || key.Contains('|', StringComparison.Ordinal) || !Guid.TryParse(key, out _)) continue;
                    old.Value = key;
                    fresh.Value = Guid.NewGuid().ToString("D");
                    insert.ExecuteNonQuery();
                }
            }

            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DELETE FROM SpaceMembers WHERE SpaceId = $space; DELETE FROM Spaces WHERE Id = $space;";
                drop.Parameters.AddWithValue("$space", spaceId);
                drop.ExecuteNonQuery();
            }

            // A list or template whose name the Personal space already uses gets the space's name added, so the per-space
            // unique name rules (on NormalizedName) still hold after the move.
            foreach (var table in new[] { "Containers", "TaskTemplates" })
            {
                RenameClashes(connection, table, spaceId, personal, name);
            }

            foreach (var (table, column) in columns)
            {
                SyncSqlExecute(connection, $"UPDATE \"{table}\" SET \"{column}\" = (SELECT New FROM {Map} WHERE Old = \"{table}\".\"{column}\") WHERE \"{column}\" IN (SELECT Old FROM {Map});");
            }
        }

        SyncSqlExecute(connection, $"DROP TABLE IF EXISTS {Map};");
        SyncSqlExecute(connection, "DELETE FROM SpaceMembers;");
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check;";
            using var violations = check.ExecuteReader();
            if (violations.Read())
            {
                throw Chaos.Application.Abstractions.AppException.Validation("file",
                    $"The shared spaces in this archive could not be copied ({violations.GetString(0)}), so it was not restored.");
            }
        }

        transaction.Commit();
        return shared.Count;
    }

    private static void RenameClashes(SqliteConnection connection, string table, string spaceId, string personal, string spaceName)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var moving = new List<(string Id, string Name)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT Id, Name, NormalizedName, SpaceId FROM \"{table}\" WHERE SpaceId IN ($space, $personal) AND DeletedAtUtc IS NULL;";
            select.Parameters.AddWithValue("$space", spaceId);
            select.Parameters.AddWithValue("$personal", personal);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(3) == personal) taken.Add(reader.GetString(2));
                else moving.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        using var update = connection.CreateCommand();
        update.CommandText = $"UPDATE \"{table}\" SET Name = $name, NormalizedName = $normalized WHERE Id = $id;";
        var id = update.Parameters.Add("$id", SqliteType.Text);
        var newName = update.Parameters.Add("$name", SqliteType.Text);
        var normalized = update.Parameters.Add("$normalized", SqliteType.Text);
        foreach (var (rowId, rowName) in moving)
        {
            var candidate = rowName;
            for (var attempt = 1; taken.Contains(candidate.Trim().ToLowerInvariant()); attempt++)
            {
                candidate = attempt == 1 ? $"{rowName} ({spaceName})" : $"{rowName} ({spaceName} {attempt.ToString(CultureInfo.InvariantCulture)})";
            }

            taken.Add(candidate.Trim().ToLowerInvariant());
            if (candidate == rowName) continue;
            id.Value = rowId;
            newName.Value = candidate;
            normalized.Value = candidate.Trim().ToLowerInvariant();
            update.ExecuteNonQuery();
        }
    }

    /// <summary>Every TEXT column of every ordinary table: IDs are TEXT. Virtual tables and their shadow tables are left alone.</summary>
    private static List<(string Table, string Column)> IdColumns(SqliteConnection connection)
    {
        var tables = new List<string>();
        var virtualTables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                (sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) ? virtualTables : tables).Add(reader.GetString(0));
            }
        }

        var result = new List<(string, string)>();
        foreach (var table in tables.Where(table => table != "SchemaMigrations"
            && !virtualTables.Any(name => table.StartsWith(name + "_", StringComparison.Ordinal))))
        {
            using var info = connection.CreateCommand();
            info.CommandText = "SELECT name, type FROM pragma_table_info($table);";
            info.Parameters.AddWithValue("$table", table);
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((table, reader.GetString(0)));
                }
            }
        }

        return result;
    }

    private static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return System.Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;
    }

    private static void SyncSqlExecute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
