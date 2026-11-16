using Chaos.Application.Abstractions;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// Row-level helpers shared by the applier and its unique rules: entity keys parsed and bound as
/// <c>$k0..$kn</c>, winning values flattened per column, and loser clocks kept as a watermark.
/// </summary>
internal static class SyncRows
{
    public static string Where(SyncTable table, IReadOnlyDictionary<string, string> key) =>
        string.Join(" AND ", table.Key.Select((column, index) => $"{SyncSql.Quote(column)} = $k{index}"));

    public static void BindKey(SqliteCommand command, SyncTable table, IReadOnlyDictionary<string, string> key)
    {
        foreach (var (column, index) in table.Key.Select((column, index) => (column, index)))
        {
            command.Parameters.AddWithValue($"$k{index}", key[column]);
        }
    }

    public static void Bind(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public static IReadOnlyDictionary<string, string> ParseKey(SyncTable table, string entityKey)
    {
        var parts = entityKey.Split('|');
        if (parts.Length != table.Key.Count)
        {
            throw new AppException(AppErrorKind.StorageUnavailable, $"A change for {table.Kind} has a key of the wrong shape.");
        }

        return table.Key.Select((column, index) => (column, parts[index])).ToDictionary();
    }

    /// <summary>The winning groups' values flattened to column → value (each column belongs to one group).</summary>
    public static Dictionary<string, object?> WinnerValues(MergeResult merge)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var winner in merge.Winners.Values)
        {
            foreach (var (column, value) in winner.Values.Values)
            {
                values[column] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Stores an incoming loser's clocks as a watermark, so the change does not win again on a
    /// replay; a higher stored clock is kept.
    /// </summary>
    public static void StoreLoserClocks(SqliteConnection connection, SyncTable table, SyncChange change)
    {
        foreach (var (group, state) in change.Groups)
        {
            SyncSql.UpsertClock(connection, table.Kind, change.EntityKey, group, state.Hlc.Encode(), onlyIfNewer: true);
        }
    }
}
