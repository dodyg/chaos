using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// Shared SQL helpers of the sync layer: identifier quoting and value literals for generated SQL
/// (triggers, flush, applier), the one-value query shorthands, and the single <c>SyncClocks</c>
/// upsert. Identifiers come from the database and the catalog, never from changes.
/// </summary>
internal static class SyncSql
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static string? Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The conflict clause of a <c>SyncClocks</c> insert. <paramref name="onlyIfNewer"/> keeps the
    /// stored clock when it is already higher (side effects and replays must never lower a
    /// watermark); without it the new clock always replaces the stored one (the merge already
    /// decided it wins, or the flush just issued it).
    /// </summary>
    public static string ClockConflict(bool onlyIfNewer) =>
        "ON CONFLICT (Kind, EntityKey, GroupName) DO UPDATE SET Hlc = excluded.Hlc"
        + (onlyIfNewer ? " WHERE excluded.Hlc > SyncClocks.Hlc" : string.Empty);

    /// <summary>Stores one group's clock; see <see cref="ClockConflict"/> for <paramref name="onlyIfNewer"/>.</summary>
    public static void UpsertClock(SqliteConnection connection, string kind, string entityKey, string group, string hlc, bool onlyIfNewer)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO SyncClocks (Kind, EntityKey, GroupName, Hlc) VALUES ($kind, $key, $group, $hlc) {ClockConflict(onlyIfNewer)};";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", entityKey);
        command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$hlc", hlc);
        command.ExecuteNonQuery();
    }
}
