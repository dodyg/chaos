using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// Column facts of the migrated schema for one apply pass: which columns a table has, which of
/// them an arriving row must still fill (NOT NULL without a declared default, and whether the
/// neutral value is text or a number), which column retires a duplicate, and which unique index a
/// set of columns belongs to. The applier loads the tables it touches once per batch; identifiers
/// come from the database and the catalog, never from changes, so quoting stays safe.
/// </summary>
internal sealed class SyncSchema(SqliteConnection connection)
{
    private readonly Dictionary<string, List<(string Column, string? Default, bool Integer, bool Required)>> _facts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Columns(string table)
    {
        LoadFacts(table);
        return [.. _facts[table].Select(entry => entry.Column)];
    }

    public bool HasColumn(string table, string column) =>
        Columns(table).Contains(column, StringComparer.OrdinalIgnoreCase);

    /// <summary>The NOT NULL columns that have no declared default: an arriving row must fill them.</summary>
    public IReadOnlyList<string> RequiredWithoutDefault(string table)
    {
        LoadFacts(table);
        return [.. _facts[table].Where(entry => entry.Required && entry.Default is null).Select(entry => entry.Column)];
    }

    /// <summary>The neutral fill value's kind: text when the column is not declared numeric.</summary>
    public bool ColumnIsText(string table, string column)
    {
        LoadFacts(table);
        var entry = _facts[table].FirstOrDefault(candidate => candidate.Column == column);
        return entry.Column != column || !entry.Integer;
    }

    /// <summary>The column that retires a duplicate row (§7.6), or null when the row is removed outright.</summary>
    public string? RetireColumn(string table) =>
        table.Equals("DayPlanItems", StringComparison.OrdinalIgnoreCase)
            ? "RemovedAtUtc" // membership rows retire by removal, not a tombstone
            : HasColumn(table, "DeletedAtUtc") ? "DeletedAtUtc" : null;

    /// <summary>The table's foreign keys, grouped as declared: (child columns, parent table, parent columns).</summary>
    public IReadOnlyList<(string[] Columns, string Table, string[] ParentColumns)> ForeignKeys(string table)
    {
        var rows = new List<(long Id, int Seq, string Parent, string Column, string ParentColumn)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA foreign_key_list({SyncSql.Quote(table)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), (int)reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }

        return [.. rows
            .GroupBy(row => row.Id)
            .Select(group => (
                group.OrderBy(row => row.Seq).Select(row => row.Column).ToArray(),
                group.First().Parent,
                group.OrderBy(row => row.Seq).Select(row => row.ParentColumn).ToArray()))];
    }

    /// <summary>The unique index of one table whose columns match the violation, or null.</summary>
    public string? FindUniqueIndex(string table, IReadOnlyList<string> columns)
    {
        using var list = connection.CreateCommand();
        list.CommandText = $"PRAGMA index_list({SyncSql.Quote(table)});";
        using var reader = list.ExecuteReader();
        var indexes = new List<string>();
        while (reader.Read())
        {
            if (reader.GetInt64(2) == 1) // unique
            {
                indexes.Add(reader.GetString(1));
            }
        }

        // Exact match first; an index with an expression column (the tag sibling index's
        // COALESCE) matches when its named columns are a subset of the reported violation.
        string? partial = null;
        foreach (var index in indexes)
        {
            var indexColumns = new List<string>();
            using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info({SyncSql.Quote(index)});";
            using var indexReader = info.ExecuteReader();
            while (indexReader.Read())
            {
                if (!indexReader.IsDBNull(2))
                {
                    indexColumns.Add(indexReader.GetString(2));
                }
            }

            if (indexColumns.Count == columns.Count && !indexColumns.Except(columns, StringComparer.OrdinalIgnoreCase).Any())
            {
                return index;
            }

            if (indexColumns.Count < columns.Count && !indexColumns.Except(columns, StringComparer.OrdinalIgnoreCase).Any())
            {
                partial = index;
            }
        }

        return partial;
    }

    /// <summary>The named (non-expression) columns of one index, for a violation reported by name.</summary>
    public IReadOnlyList<string> NamedIndexColumns(string table, string index)
    {
        var columns = new List<string>();
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA index_info({SyncSql.Quote(index)});";
        using var indexReader = info.ExecuteReader();
        while (indexReader.Read())
        {
            if (!indexReader.IsDBNull(2))
            {
                columns.Add(indexReader.GetString(2));
            }
        }

        return columns;
    }

    private void LoadFacts(string table)
    {
        if (_facts.ContainsKey(table))
        {
            return;
        }

        var entries = new List<(string Column, string? Default, bool Integer, bool Required)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({SyncSql.Quote(table)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var @default = reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString();
                entries.Add((reader.GetString(1), @default, (reader.GetValue(2) as string ?? "TEXT").Contains("INT", StringComparison.OrdinalIgnoreCase), reader.GetInt64(3) != 0));
            }
        }

        _facts[table] = entries;
    }
}
