using System.Globalization;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Search;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync.UniqueRules;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Sync.SyncRows;

namespace Chaos.Infrastructure.Sync;

public sealed partial class SyncApplier
{
    // ---- Purge (§7.4 step 2.1) -----------------------------------------------------------

    private static void PurgeEntity(SqliteConnection connection, SyncSchema schema, SyncTable table, string entityKey)
    {
        var key = ParseKey(table, entityKey);
        var kindSql = SyncPurgeSql.For(table.Kind, [.. key.Values]);
        using var command = connection.CreateCommand();
        if (kindSql.Count > 0)
        {
            command.CommandText = string.Join("\n", kindSql);
        }
        else
        {
            command.CommandText = $"DELETE FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, key)};";
        }

        BindKey(command, table, key);
        command.ExecuteNonQuery();

        // The entity's clocks stay as a watermark: a replayed change older than the last one seen
        // for this entity must not resurrect the row, while a genuinely newer insert (the row
        // re-created on another device) still wins.
    }

    // ---- Unique rules (§7.6) ---------------------------------------------------------------

    private enum ConstraintKind
    {
        Unique,
        ForeignKey,
        NotNull,
        Check,
        Other,
    }

    private enum RuleOutcome
    {
        /// <summary>The rule resolved the conflict and the row was written.</summary>
        Resolved,

        /// <summary>The change waits in the inbox (no rule, or its parent has not arrived).</summary>
        Parked,
    }

    private static ConstraintKind Classify(SqliteException error, out IReadOnlyList<string> columns, out string? namedIndex)
    {
        columns = [];
        namedIndex = null;
        var message = error.Message ?? string.Empty;
        const string unique = "UNIQUE constraint failed: ";
        const string byIndex = "UNIQUE constraint failed: index '";
        if (message.Contains(byIndex, StringComparison.OrdinalIgnoreCase))
        {
            // An index with an expression column (the tag sibling index) is reported by name.
            var from = message.IndexOf(byIndex, StringComparison.OrdinalIgnoreCase) + byIndex.Length;
            var close = message.IndexOf('\'', from);
            namedIndex = message.Substring(from, close - from);
            return ConstraintKind.Unique;
        }

        if (message.Contains(unique, StringComparison.OrdinalIgnoreCase))
        {
            var start = message.IndexOf(unique, StringComparison.OrdinalIgnoreCase) + unique.Length;
            // The engine wraps the column list in quotes ('Cols'.'), so trim its punctuation.
            columns = [.. message[start..].Split(", ").Select(part => part.Trim(".'. \n\r".ToCharArray()).Split('.').Last())];
            return ConstraintKind.Unique;
        }

        if (message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintKind.ForeignKey;
        }

        if (message.Contains("NOT NULL constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintKind.NotNull;
        }

        if (message.Contains("CHECK constraint failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConstraintKind.Check;
        }

        return ConstraintKind.Other;
    }

    /// <summary>
    /// Resolves one unique-index break with the catalog's rule (<see cref="IUniqueRule"/>). The
    /// winner is the row with the lowest entity key (ordinal string comparison), so every device
    /// makes the same choice (§7.6).
    /// </summary>
    private async Task<RuleOutcome> ResolveUniqueAsync(
        SqliteConnection connection, SyncSchema schema, SyncTable table, IReadOnlyDictionary<string, string> key, SyncChange change,
        MergeResult merge, IReadOnlyList<string> columns, DateTimeOffset wall, ParkSlip park, string? namedIndex, CancellationToken ct, int depth = 0)
    {
        var index = namedIndex ?? schema.FindUniqueIndex(table.Table, columns);
        if (namedIndex is not null && columns.Count == 0)
        {
            columns = schema.NamedIndexColumns(table.Table, namedIndex);
        }

        var catalogRule = index is null ? null : table.UniqueRules.FirstOrDefault(candidate => candidate.Index == index);
        if (catalogRule is null)
        {
            park.Park($"two rows claim one unique value of {table.Table} and no rule resolves it", "sync.conflict",
                $"A change for {table.Table} collided with a local row and no rule resolved it. It waits in the sync inbox.");
            return RuleOutcome.Parked;
        }

        if (catalogRule.Kind == SyncUniqueRuleKind.Adopt)
        {
            park.Park("this row is adopted during device join, never during an ordinary apply", "sync.adopt",
                "A row arrived that this device can only adopt while joining an account. It waits in the sync inbox.");
            return RuleOutcome.Parked;
        }

        var otherKey = FindConflictingKey(connection, schema, table, catalogRule.Index, columns, merge);
        if (otherKey is null)
        {
            park.Park($"the colliding row of {table.Table} could not be found");
            return RuleOutcome.Parked;
        }

        var rule = IUniqueRule.For(catalogRule.Kind);
        var conflict = new UniqueConflict(
            connection, schema, table, change, key,
            LocalKey: string.Join("|", table.Key.Select(column => otherKey[column])),
            wall,
            WriteNewRowAsync: (written, token) => WriteWinnersAsync(connection, schema, table, key, exists: false, written, wall, change, token),
            Attention: (category, kind, entityKey, message) => Attention(connection, category, kind, entityKey, message));

        merge = rule.ResolveLoser(conflict, merge);
        if (!conflict.IncomingWins)
        {
            // The incoming loser is written in its retired/renamed shape so both devices hold the
            // same rows; its clock is stored so the change does not win again.
            if (table.Kind == "recurrence_occurrence" && OccurrenceIdentityExists(connection, merge))
            {
                // The identity index (SeriesId, NominalKey) is not partial: while the loser's
                // series-and-nominal twin exists here, the loser row cannot, not even tombstoned —
                // no matter which index the violation named. Its clocks are stored as a watermark
                // so the change does not come back round after round.
                StoreLoserClocks(connection, table, change);
            }
            else
            {
                await rule.WriteIncomingLoserAsync(conflict, merge, ct);
            }

            return RuleOutcome.Resolved;
        }

        try
        {
            await WriteWinnersAsync(connection, schema, table, key, RowExists(connection, table, key), merge, wall, change, ct);
            return RuleOutcome.Resolved;
        }
        catch (SqliteException retry)
        {
            var kind = Classify(retry, out var retryColumns, out var retryIndex);
            if (kind == ConstraintKind.ForeignKey)
            {
                park.Park("the change references something that has not arrived yet");
                return RuleOutcome.Parked;
            }

            // Resolving one duplicate can surface the next (an occurrence's identity and its
            // task claim resolve one after the other): resolve them in turn, within reason.
            // The nested resolution writes the row itself, so its outcome is ours.
            if (kind == ConstraintKind.Unique && depth < 2)
            {
                return await ResolveUniqueAsync(connection, schema, table, key, change, merge, retryColumns, wall, park, retryIndex, ct, depth + 1);
            }

            throw;
        }
    }
    private static IReadOnlyDictionary<string, string>? FindConflictingKey(
        SqliteConnection connection, SyncSchema schema, SyncTable table, string index, IReadOnlyList<string> columns, MergeResult merge)
    {
        // An expression index column (the tag sibling index's COALESCE) reaches us as its
        // expression text; only real columns can be looked up, and they identify the row.
        columns = [.. columns.Where(column => schema.HasColumn(table.Table, column))];
        var winnerValues = WinnerValues(merge);
        var values = new List<object?>();
        foreach (var column in columns)
        {
            if (!winnerValues.TryGetValue(column, out var value))
            {
                return null; // the incoming change does not even carry the conflicting column
            }

            values.Add(value);
        }

        using var command = connection.CreateCommand();
        var where = string.Join(" AND ", columns.Select((column, position) => $"{SyncSql.Quote(column)} = $c{position}"));
        command.CommandText = $"SELECT {string.Join(", ", table.Key.Select(SyncSql.Quote))} FROM {SyncSql.Quote(table.Table)} WHERE {where}{PartialIndexPredicate(connection, index)} LIMIT 1;";
        for (var position = 0; position < values.Count; position++)
        {
            command.Parameters.AddWithValue($"$c{position}", values[position] ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return table.Key.Select((column, position) => (column, reader.GetValue(position)?.ToString() ?? string.Empty)).ToDictionary();
    }

    /// <summary>
    /// The partial-index WHERE text of a rule index (§7.2): the rows the index covers, so the rule finds only a row that can really conflict.
    /// Read from the migrated schema, so it cannot drift from the index definition.
    /// </summary>
    private static string PartialIndexPredicate(SqliteConnection connection, string? index)
    {
        if (index is null)
        {
            return string.Empty;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", index);
        if (command.ExecuteScalar() is not string definition)
        {
            return string.Empty;
        }

        var where = definition.LastIndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);
        return where < 0 ? string.Empty : " AND (" + definition[(where + " WHERE ".Length)..].TrimEnd().TrimEnd(';') + ")";
    }

    /// <summary>True when the change's occurrence identity (series and nominal key) already has a row here.</summary>
    private static bool OccurrenceIdentityExists(SqliteConnection connection, MergeResult merge)
    {
        var values = WinnerValues(merge);
        var series = values.GetValueOrDefault("SeriesId");
        var nominal = values.GetValueOrDefault("NominalKey");
        if (series is null || nominal is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM RecurrenceOccurrences WHERE SeriesId = $series AND NominalKey = $nominal LIMIT 1;";
        command.Parameters.AddWithValue("$series", series);
        command.Parameters.AddWithValue("$nominal", nominal);
        return command.ExecuteScalar() is not null;
    }
}
