using Chaos.Application.Abstractions;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Sync.SyncRows;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>Containers and templates (§7.6): the loser is renamed to the first free numbered name in its space.</summary>
internal sealed class RenameLoserRule : IUniqueRule
{
    public static readonly RenameLoserRule Instance = new();

    private RenameLoserRule()
    {
    }

    public MergeResult ResolveLoser(UniqueConflict conflict, MergeResult merge)
    {
        var (connection, table) = (conflict.Connection, conflict.Table);
        var loser = ParseKey(table, conflict.LoserKey);
        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT Name, SpaceId FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, loser)};";
        BindKey(read, table, loser);
        using var row = read.ExecuteReader();
        if (!row.Read())
        {
            return merge; // the loser is the incoming row: its rename happens in WriteIncomingLoserAsync
        }

        var (candidate, normalized) = FirstFreeName(connection, table, row.GetString(1), row.GetString(0));
        using var rename = connection.CreateCommand();
        rename.CommandText = $"UPDATE {SyncSql.Quote(table.Table)} SET Name = $name, NormalizedName = $norm, Version = Version + 1, UpdatedAtUtc = $wall WHERE {Where(table, loser)};";
        rename.Parameters.AddWithValue("$name", candidate);
        rename.Parameters.AddWithValue("$norm", normalized);
        rename.Parameters.AddWithValue("$wall", StorageFormats.Instant(conflict.Wall));
        BindKey(rename, table, loser);
        rename.ExecuteNonQuery();
        return merge;
    }

    /// <summary>A renamed incoming loser lands under the first free numbered name in its own space.</summary>
    public async Task WriteIncomingLoserAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct)
    {
        var values = WinnerValues(merge);
        if (values.GetValueOrDefault("Name") is not string name || values.GetValueOrDefault("SpaceId") is not string space)
        {
            return;
        }

        var change = conflict.Change;
        var (candidate, normalized) = FirstFreeName(conflict.Connection, conflict.Table, space, name);
        var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal)
        {
            ["name"] = new SyncGroupState(
                change.Groups.TryGetValue("name", out var state) ? state.Hlc : change.Groups.Values.First().Hlc,
                new SyncGroupValues(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Name"] = candidate,
                    ["NormalizedName"] = normalized,
                })),
        };
        await conflict.WriteNewRowAsync(merge with { Winners = winners }, ct);
    }

    /// <summary>The first free numbered name "<c>name (n)</c>", n from 2, in one space of a named table.</summary>
    private static (string Name, string Normalized) FirstFreeName(SqliteConnection connection, SyncTable table, string space, string name)
    {
        for (var attempt = 2; ; attempt++)
        {
            var candidate = $"{name} ({attempt})";
            var normalized = candidate.Trim().ToLowerInvariant();
            using var taken = connection.CreateCommand();
            taken.CommandText = $"SELECT 1 FROM {SyncSql.Quote(table.Table)} WHERE SpaceId = $space AND NormalizedName = $norm LIMIT 1;";
            taken.Parameters.AddWithValue("$space", space);
            taken.Parameters.AddWithValue("$norm", normalized);
            if (taken.ExecuteScalar() is null)
            {
                return (candidate, normalized);
            }
        }
    }
}
