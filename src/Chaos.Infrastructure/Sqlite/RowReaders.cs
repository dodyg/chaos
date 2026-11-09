using Chaos.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

internal static class RowReaders
{
    public static int ColumnIndex(this SqliteDataReader reader, string column) => reader.GetOrdinal(column);

    public static string S(this SqliteDataReader reader, string column) => reader.GetString(reader.ColumnIndex(column));

    public static string? Sn(this SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.ColumnIndex(column)) ? null : reader.GetString(reader.ColumnIndex(column));

    public static long L(this SqliteDataReader reader, string column) => reader.GetInt64(reader.ColumnIndex(column));

    public static long? Ln(this SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.ColumnIndex(column)) ? null : reader.GetInt64(reader.ColumnIndex(column));

    public static bool B(this SqliteDataReader reader, string column) => reader.GetInt64(reader.ColumnIndex(column)) != 0;

    public static DateTimeOffset I(this SqliteDataReader reader, string column) =>
        StorageFormats.ParseInstant(reader.S(column))
        ?? throw new InvalidOperationException($"Column '{column}' holds an invalid instant.");

    public static DateTimeOffset? In(this SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.ColumnIndex(column)) ? null : reader.I(column);

    public static DateOnly? Dn(this SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.ColumnIndex(column)) ? null : StorageFormats.ParseDate(reader.S(column));

    public static TimeOnly? Tn(this SqliteDataReader reader, string column) =>
        reader.IsDBNull(reader.ColumnIndex(column)) ? null : StorageFormats.ParseTime(reader.S(column));
}
