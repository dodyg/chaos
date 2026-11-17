using Chaos.Application.Abstractions;
using Chaos.Sync;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>Legacy duplicates of a natural key (§7.6): the loser's children move to the winner, then it is tombstoned.</summary>
internal sealed class RepointChildrenRule : IUniqueRule
{
    public static readonly RepointChildrenRule Instance = new();

    private RepointChildrenRule()
    {
    }

    public MergeResult ResolveLoser(UniqueConflict conflict, MergeResult merge)
    {
        var (connection, table, loserKey, winnerKey, wall) = (conflict.Connection, conflict.Table, conflict.LoserKey, conflict.WinnerKey, conflict.Wall);
        foreach (var sql in SyncRepointSql.For(table.Table, loserKey, winnerKey))
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$loser", loserKey);
            command.Parameters.AddWithValue("$winner", winnerKey);
            command.ExecuteNonQuery();
        }

        if (conflict.Schema.RetireColumn(table.Table) is { } retire)
        {
            using var tombstone = connection.CreateCommand();
            tombstone.CommandText = $"UPDATE {SyncSql.Quote(table.Table)} SET {SyncSql.Quote(retire)} = $now, Version = Version + 1, UpdatedAtUtc = $now WHERE Id = $loser;";
            tombstone.Parameters.AddWithValue("$loser", loserKey);
            tombstone.Parameters.AddWithValue("$now", StorageFormats.Instant(wall));
            tombstone.ExecuteNonQuery();
        }

        if (table.Kind == "recurrence_occurrence")
        {
            // The identity index (SeriesId, NominalKey) is not partial, so a retired
            // duplicate cannot even exist as a tombstone: the loser leaves entirely,
            // its task retires when nothing active points at it, and the person is told.
            // Its clocks stay as a watermark, so a replay of the loser's change cannot
            // bring it back round after round.
            RetiredLoser.RetireOccurrenceTask(connection, loserKey, wall, clearTaskId: false);
            using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM RecurrenceOccurrences WHERE Id = $loser;";
            remove.Parameters.AddWithValue("$loser", loserKey);
            remove.ExecuteNonQuery();
            conflict.Attention("sync.retired", table.Kind, loserKey,
                "The same recurrence occurrence was created on two devices. One copy was removed; the other keeps the plan.");
        }

        return merge;
    }

    public Task WriteIncomingLoserAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct) =>
        RetiredLoser.WriteAsync(conflict, merge, ct);
}
