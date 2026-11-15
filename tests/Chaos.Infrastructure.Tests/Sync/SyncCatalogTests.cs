using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// The catalog guard (PLAN2 §7.2): a freshly migrated database must be fully explained by
/// SyncCatalog — every table cataloged, every column either in exactly one field group, a
/// local column, or UpdatedAtUtc, primary keys equal to the catalog keys, and every unique
/// index of a synced table either ruled or exempt. Without this test, every later migration
/// would drift from the catalog silently.
/// </summary>
public sealed class SyncCatalogTests
{
    private static SqliteConnection OpenMigrated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chaos-catalog-guard-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        return connection;
    }

    private static bool IsVirtualTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return (command.ExecuteScalar() as string)?.StartsWith("CREATE VIRTUAL TABLE", StringComparison.Ordinal) == true;
    }

    private sealed record Column(string Name, int Pk, string Type);

    private sealed record Index(string Name, bool Unique, string Origin, List<string> Columns);

    private static (List<Column> Columns, List<Index> Indexes) Inspect(SqliteConnection connection, string table)
    {
        var columns = new List<Column>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, pk, type FROM pragma_table_info($table) ORDER BY cid;";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new Column(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
            }
        }

        var indexes = new List<Index>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, \"unique\", origin FROM pragma_index_list($table);";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                indexes.Add(new Index(reader.GetString(0), reader.GetInt64(1) == 1, reader.GetString(2), []));
            }
        }

        foreach (var index in indexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(name, '<expr>') FROM pragma_index_info($index) ORDER BY seqno;";
            command.Parameters.AddWithValue("$index", index.Name);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                index.Columns.Add(reader.GetString(0));
            }
        }

        return (columns, indexes);
    }

    [Test]
    public async Task SyncCatalog_EveryTableAndColumnClassified()
    {
        using var connection = OpenMigrated();
        Chaos.Infrastructure.Sync.SyncTriggerInstaller.Install(connection);
        using (var dump = new StreamWriter(Path.Combine(Path.GetTempPath(), "chaos-triggers-tasks.txt")))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND (sql LIKE '%Tasks%' OR name LIKE 'TR_Sync_Tasks%') ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                dump.WriteLine(reader.GetString(0));
                dump.WriteLine(reader.GetString(1));
                dump.WriteLine("-----");
            }
        }
        var catalog = SyncCatalog.Tables.ToDictionary(table => table.Table, StringComparer.OrdinalIgnoreCase);

        var actualTables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (name.StartsWith("sqlite_", StringComparison.Ordinal) || name.StartsWith("SearchIndex_", StringComparison.Ordinal))
                {
                    continue; // internal and FTS shadow tables
                }

                actualTables.Add(name);
            }
        }

        var missing = actualTables.Where(table => !catalog.ContainsKey(table)).ToList();
        var extra = catalog.Keys.Where(table => !actualTables.Contains(table, StringComparer.OrdinalIgnoreCase)).ToList();
        await Assert.That(missing).IsEmpty();
        await Assert.That(extra).IsEmpty();

        var problems = new List<string>();
        foreach (var table in actualTables)
        {
            var entry = catalog[table];
            var virtualTable = IsVirtualTable(connection, table);
            var (columns, indexes) = Inspect(connection, table);
            var columnNames = columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (virtualTable)
            {
                continue; // fts5-generated: presence in the catalog is the contract (SearchIndex)
            }

            // The catalog key is exactly the primary key.
            var primaryKey = columns.Where(column => column.Pk > 0).OrderBy(column => column.Pk).Select(column => column.Name).ToList();
            if (!primaryKey.SequenceEqual(entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{table}: key [{string.Join(", ", entry.Key)}] != primary key [{string.Join(", ", primaryKey)}]");
            }

            // LocalOnly tables are classified as a whole: every column stays on the device by
            // definition, so only presence and the primary key are checked for them.
            if (entry.Mode == SyncMode.LocalOnly)
            {
                continue;
            }

            // Every catalog column exists; every schema column is classified exactly once.
            // Key columns are their own classification: they travel as the entity identity.
            var grouped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in entry.Groups)
            {
                foreach (var column in group.Columns)
                {
                    if (!columnNames.Contains(column))
                    {
                        problems.Add($"{table}: group '{group.Name}' names missing column {column}");
                    }

                    if (column.Equals("UpdatedAtUtc", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{table}: UpdatedAtUtc must not be in a group (§7.2 rule 3)");
                    }

                    if (!grouped.TryAdd(column, group.Name))
                    {
                        problems.Add($"{table}: column {column} is in two groups ('{grouped[column]}', '{group.Name}')");
                    }
                }
            }

            foreach (var column in entry.LocalColumns)
            {
                if (!columnNames.Contains(column))
                {
                    problems.Add($"{table}: local column {column} does not exist");
                }

                if (grouped.ContainsKey(column))
                {
                    problems.Add($"{table}: column {column} is both local and in group '{grouped[column]}'");
                }
            }

            foreach (var column in columns.Select(column => column.Name))
            {
                var classified = column.Equals("UpdatedAtUtc", StringComparison.OrdinalIgnoreCase) || grouped.ContainsKey(column) ||
                    entry.LocalColumns.Contains(column, StringComparer.OrdinalIgnoreCase) ||
                    entry.Key.Contains(column, StringComparer.OrdinalIgnoreCase);
                if (!classified)
                {
                    problems.Add($"{table}: column {column} is not classified");
                }
            }

            // Every unique index of a synced table is ruled or exempt (§7.2 rule 1).
            {
                foreach (var index in indexes.Where(index => index.Unique))
                {
                    if (index.Origin == "pk")
                    {
                        continue; // the entity key is the uniqueness
                    }

                    var ruled = entry.UniqueRules.Any(rule => rule.Index.Equals(index.Name, StringComparison.OrdinalIgnoreCase));
                    var exempt = entry.DeterministicIndexes.Any(exemption => exemption.Index.Equals(index.Name, StringComparison.OrdinalIgnoreCase));
                    if (!ruled && !exempt)
                    {
                        problems.Add($"{table}: unique index {index.Name} ({string.Join(", ", index.Columns)}) has no rule or exemption");
                    }
                }
            }
        }

        await Assert.That(problems).IsEmpty();

        // Apply order is a total order (parents strictly before children).
        var orders = SyncCatalog.Tables.Select(table => table.ApplyOrder).ToList();
        await Assert.That(orders.Count).IsEqualTo(orders.Distinct().Count());
        var kinds = SyncCatalog.Tables.Select(table => table.Kind).ToList();
        await Assert.That(kinds.Count).IsEqualTo(kinds.Distinct(StringComparer.Ordinal).Count());
    }

    [Test]
    public async Task Parents_are_ordered_before_their_children()
    {
        // Spot-check the dependencies the wire order relies on (§7.8): a parent's insert
        // normally arrives before its children, and the applier parks on missing parents.
        var order = SyncCatalog.Tables.ToDictionary(table => table.Table, table => table.ApplyOrder, StringComparer.OrdinalIgnoreCase);
        var pairs = new[]
        {
            ("Tasks", "Subtasks"), ("Tasks", "TaskTags"), ("Subtasks", "SubtaskTags"), ("Tags", "TaskTags"),
            ("Spaces", "Containers"), ("Containers", "Sections"), ("Containers", "Tasks"), ("Tags", "PageTags"),
            ("Pages", "PageTags"), ("Pages", "PageTaskLinks"), ("DayPlans", "DayPlanItems"),
            ("WeekPlans", "WeekGoals"), ("WeekGoals", "WeekGoalTasks"), ("WeekPlans", "WeekAssignments"),
            ("RecurrenceSeries", "RecurrenceOccurrences"), ("GitHubConnections", "GitHubRepositories"),
            ("GitHubRepositories", "GitHubIssueLinks"), ("DiaryEntries", "GitHubCommentLinks"),
        };
        var problems = pairs.Where(pair => order[pair.Item1] >= order[pair.Item2])
            .Select(pair => $"{pair.Item1} ({order[pair.Item1]}) must come before {pair.Item2} ({order[pair.Item2]})").ToList();
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task UpdatedAtUtc_is_never_a_synced_group_anywhere()
    {
        var offenders = SyncCatalog.Tables
            .SelectMany(table => table.Groups, (table, group) => (table, group))
            .Where(pair => pair.group.Columns.Any(column => column.Equals("UpdatedAtUtc", StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.table.Table)
            .ToList();
        await Assert.That(offenders).IsEmpty();
    }
}
