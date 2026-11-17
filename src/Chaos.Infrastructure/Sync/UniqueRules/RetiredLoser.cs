using Chaos.Application.Abstractions;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>What the rules that retire their loser (retire, repoint, append) share.</summary>
internal static class RetiredLoser
{
    /// <summary>
    /// Retired losers arrive tombstoned: the incoming row is written with its table's retire
    /// column set. A table without a synced retire column writes nothing.
    /// </summary>
    public static async Task WriteAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct)
    {
        var table = conflict.Table;
        if (conflict.Schema.RetireColumn(table.Table) is not { } retire
            || !table.Groups.Any(group => group.Columns.Contains(retire)))
        {
            return;
        }

        var retiredAt = StorageFormats.Instant(conflict.Wall);
        var group = table.Groups.First(candidate => candidate.Columns.Contains(retire));
        var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal);
        winners[group.Name] = winners.TryGetValue(group.Name, out var state)
            ? state with { Values = new SyncGroupValues(new Dictionary<string, object?>(state.Values.Values, StringComparer.OrdinalIgnoreCase) { [retire] = retiredAt }) }
            : new SyncGroupState(conflict.Change.Groups[group.Name].Hlc, new SyncGroupValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [retire] = retiredAt }));
        await conflict.WriteNewRowAsync(merge with { Winners = winners }, ct);
    }

    /// <summary>
    /// Retires the task a retired or removed duplicate occurrence pointed at, when no active
    /// occurrence still does; <paramref name="clearTaskId"/> also releases the occurrence's task claim.
    /// </summary>
    public static void RetireOccurrenceTask(SqliteConnection connection, string occurrenceId, DateTimeOffset wall, bool clearTaskId)
    {
        using var task = connection.CreateCommand();
        task.CommandText = """
            UPDATE Tasks SET DeletedAtUtc = $now, Version = Version + 1, UpdatedAtUtc = $now
            WHERE Id IN (SELECT TaskId FROM RecurrenceOccurrences WHERE Id = $occurrence AND TaskId IS NOT NULL)
              AND Id NOT IN (SELECT TaskId FROM RecurrenceOccurrences WHERE TaskId IS NOT NULL AND DeletedAtUtc IS NULL);
            """ + (clearTaskId ? "\nUPDATE RecurrenceOccurrences SET TaskId = NULL WHERE Id = $occurrence;" : string.Empty);
        task.Parameters.AddWithValue("$occurrence", occurrenceId);
        task.Parameters.AddWithValue("$now", StorageFormats.Instant(wall));
        task.ExecuteNonQuery();
    }
}
