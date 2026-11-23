using Chaos.Application.Abstractions;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Sync.SyncRows;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>
/// Sibling tags (§7.6): the loser's links become the winner's (a link only the loser had is
/// copied to the winner and the loser's row tombstoned, so link keys and their clocks stay
/// stable across devices), colliding children merge recursively, the rest move, and the loser
/// is tombstoned. Mirrors TagCommands.MergeAsync at the data level; every timestamp is the
/// change's wall time, so all devices write the same rows. A winner link that was itself
/// tombstoned comes back, or the item would end up with no active tag at all.
/// </summary>
internal sealed class MergeTagRule : IUniqueRule
{
    public static readonly MergeTagRule Instance = new();

    private MergeTagRule()
    {
    }

    public MergeResult ResolveLoser(UniqueConflict conflict, MergeResult merge)
    {
        Merge(conflict.Connection, conflict.WinnerKey, conflict.LoserKey, conflict.ClockText, StorageFormats.Instant(conflict.Wall));
        return merge;
    }

    /// <summary>The merge moved links and tombstoned; the incoming loser row itself is inserted tombstoned.</summary>
    public Task WriteIncomingLoserAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct)
    {
        var values = WinnerValues(merge);
        var wall = StorageFormats.Instant(conflict.Wall);

        using var insert = conflict.Connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Tags (Id, OwnerProfileId, SpaceId, DisplayName, NormalizedName, ParentTagId, ColorKey, Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $owner, $space, $name, $norm, $parent, $color, $position, 1, $created, $updated, $deleted)
            ON CONFLICT (Id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$id", conflict.Key["Id"]);
        insert.Parameters.AddWithValue("$owner", values.GetValueOrDefault("OwnerProfileId") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$space", values.GetValueOrDefault("SpaceId") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$name", values.GetValueOrDefault("DisplayName") ?? string.Empty);
        insert.Parameters.AddWithValue("$norm", values.GetValueOrDefault("NormalizedName") ?? string.Empty);
        insert.Parameters.AddWithValue("$parent", values.GetValueOrDefault("ParentTagId") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$color", values.GetValueOrDefault("ColorKey") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$position", values.GetValueOrDefault("Position") ?? 0L);
        insert.Parameters.AddWithValue("$created", values.GetValueOrDefault("CreatedAtUtc") ?? wall);
        insert.Parameters.AddWithValue("$updated", wall);
        insert.Parameters.AddWithValue("$deleted", wall);
        insert.ExecuteNonQuery();

        // The loser's groups carry the same clocks everywhere, so a replay never re-runs the merge.
        StoreLoserClocks(conflict.Connection, conflict.Table, conflict.Change);
        return Task.CompletedTask;
    }

    private static void Merge(SqliteConnection connection, string winnerKey, string loserKey, string clockText, string wall)
    {
        foreach (var (table, kind, first) in new (string Table, string Kind, string First)[]
        {
            ("TaskTags", "task_tag", "TaskId"),
            ("SubtaskTags", "subtask_tag", "SubtaskId"),
            ("ScratchPadTags", "pad_tag", "PadItemId"),
            ("PageTags", "page_tag", "PageId"),
        })
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    INSERT INTO {table} ({first}, TagId) SELECT {first}, $winner FROM {table} WHERE TagId = $loser AND DeletedAtUtc IS NULL
                    ON CONFLICT ({first}, TagId) DO UPDATE SET DeletedAtUtc = NULL;
                    UPDATE {table} SET DeletedAtUtc = $wall WHERE TagId = $loser AND DeletedAtUtc IS NULL;
                    """;
                command.Parameters.AddWithValue("$winner", winnerKey);
                command.Parameters.AddWithValue("$loser", loserKey);
                command.Parameters.AddWithValue("$wall", wall);
                command.ExecuteNonQuery();
            }

            // The copied links are new entities; their clock is the merging change's, the same
            // value on every device, and a later replay never lowers it (HLC strings sort ordinally).
            using (var clocks = connection.CreateCommand())
            {
                clocks.CommandText = $"""
                    INSERT INTO SyncClocks (Kind, EntityKey, GroupName, Hlc)
                        SELECT {SyncSql.Literal(kind)}, {first} || '|' || $winner, 'deleted', $hlc FROM {table} WHERE TagId = $winner AND DeletedAtUtc IS NULL
                    {SyncSql.ClockConflict(onlyIfNewer: true)};
                    """;
                clocks.Parameters.AddWithValue("$winner", winnerKey);
                clocks.Parameters.AddWithValue("$hlc", clockText);
                clocks.ExecuteNonQuery();
            }
        }

        // Children: a child whose normalized name collides with one of the winner's children
        // merges into it (recursively); the rest move. A child's key is its Id, so a move
        // changes no entity key.
        while (true)
        {
            using var colliding = connection.CreateCommand();
            colliding.CommandText = """
                SELECT l.Id AS LoserId, w.Id AS WinnerId
                FROM Tags l JOIN Tags w ON w.ParentTagId = $winner AND w.NormalizedName = l.NormalizedName AND w.DeletedAtUtc IS NULL
                WHERE l.ParentTagId = $loser AND l.DeletedAtUtc IS NULL;
                """;
            colliding.Parameters.AddWithValue("$winner", winnerKey);
            colliding.Parameters.AddWithValue("$loser", loserKey);
            var pairs = new List<(string Loser, string Winner)>();
            using (var reader = colliding.ExecuteReader())
            {
                while (reader.Read())
                {
                    pairs.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            if (pairs.Count == 0)
            {
                break;
            }

            foreach (var (childLoser, childWinner) in pairs)
            {
                Merge(connection, childWinner, childLoser, clockText, wall);
            }
        }

        using (var move = connection.CreateCommand())
        {
            move.CommandText = "UPDATE Tags SET ParentTagId = $winner WHERE ParentTagId = $loser AND DeletedAtUtc IS NULL;";
            move.Parameters.AddWithValue("$winner", winnerKey);
            move.Parameters.AddWithValue("$loser", loserKey);
            move.ExecuteNonQuery();
        }

        using var tombstone = connection.CreateCommand();
        tombstone.CommandText = "UPDATE Tags SET DeletedAtUtc = $wall, Version = Version + 1, UpdatedAtUtc = $wall WHERE Id = $loser AND DeletedAtUtc IS NULL;";
        tombstone.Parameters.AddWithValue("$loser", loserKey);
        tombstone.Parameters.AddWithValue("$wall", wall);
        tombstone.ExecuteNonQuery();
    }
}
