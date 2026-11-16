using Chaos.Application.Abstractions;
using Chaos.Sync;
using static Chaos.Infrastructure.Sync.SyncRows;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>One winner, the other retired (§7.6): the loser is tombstoned (or removed, when its table has no tombstone) and a notice explains.</summary>
internal sealed class KeepLowestRetireOtherRule : IUniqueRule
{
    public static readonly KeepLowestRetireOtherRule Instance = new();

    private KeepLowestRetireOtherRule()
    {
    }

    public MergeResult ResolveLoser(UniqueConflict conflict, MergeResult merge)
    {
        var (connection, table, loserKey, wall) = (conflict.Connection, conflict.Table, conflict.LoserKey, conflict.Wall);
        var loser = ParseKey(table, loserKey);
        var retire = conflict.Schema.RetireColumn(table.Table);
        if (retire is null)
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, loser)};";
            BindKey(delete, table, loser);
            delete.ExecuteNonQuery();
        }
        else
        {
            using var tombstone = connection.CreateCommand();
            tombstone.CommandText = $"UPDATE {SyncSql.Quote(table.Table)} SET {SyncSql.Quote(retire)} = $now, Version = Version + 1, UpdatedAtUtc = $now WHERE {Where(table, loser)} AND {SyncSql.Quote(retire)} IS NULL;";
            tombstone.Parameters.AddWithValue("$now", StorageFormats.Instant(wall));
            BindKey(tombstone, table, loser);
            tombstone.ExecuteNonQuery();
        }

        // A retired occurrence takes its task with it (§7.6): a completed duplicate task that no
        // active occurrence points at would otherwise linger as work to do. The loser's TaskId
        // is cleared too, or the partial one-task-per-occurrence index would keep blocking the
        // winner's task claim.
        if (table.Kind == "recurrence_occurrence")
        {
            RetiredLoser.RetireOccurrenceTask(connection, loserKey, wall, clearTaskId: true);
        }

        conflict.Attention("sync.retired", table.Kind, loserKey,
            $"Two devices each kept their own {table.Table} row. The one from {(string.CompareOrdinal(conflict.Change.EntityKey, loserKey) <= 0 ? "another device" : "this device")} was retired; you can restore it from the trash.");
        return merge;
    }

    public Task WriteIncomingLoserAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct) =>
        RetiredLoser.WriteAsync(conflict, merge, ct);
}
