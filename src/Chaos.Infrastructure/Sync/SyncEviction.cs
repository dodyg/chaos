using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>What one eviction removed.</summary>
public sealed record SyncEvictionResult(string StreamId, string? SpaceName, long Entities, long DiscardedChanges);

/// <summary>
/// Removes a space this device can no longer read (PLAN3 §7.7). The space's entities and their dependents are found by
/// walking the routing table from the space, then deleted in one transaction with the apply flag set, so nothing is
/// captured. Personal overlays (account-stream rows that name a shared entity) are left for the server's tombstones.
/// The local <c>SyncStreams</c> row goes last in the same transaction, so a crash before commit re-runs the eviction.
/// </summary>
public sealed class SyncEviction(SqliteApplicationStore store, TimeProvider clock)
{
    private const string Keys = "temp.SyncEvictKeys";

    private static readonly Dictionary<string, SyncTable> TablesByName =
        SyncCatalog.Tables.ToDictionary(table => table.Table, StringComparer.OrdinalIgnoreCase);

    /// <summary>Evicts every stream in state <c>evicting</c>. Safe to call at startup and after every discovery.</summary>
    public async Task<IReadOnlyList<SyncEvictionResult>> EvictPendingAsync(CancellationToken ct = default)
    {
        var pending = await store.QueryAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT StreamId FROM SyncStreams WHERE Kind = 'space' AND State = 'evicting' ORDER BY StreamId;";
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));
            return Task.FromResult(ids);
        }, ct);

        var results = new List<SyncEvictionResult>();
        foreach (var id in pending)
        {
            results.Add(await EvictAsync(id, ct));
        }

        return results;
    }

    /// <summary>Evicts one stream that is already marked <c>evicting</c>.</summary>
    public Task<SyncEvictionResult> EvictAsync(string streamId, CancellationToken ct = default) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT count(*) FROM SyncStreams WHERE StreamId = $id AND Kind = 'space' AND State = 'evicting';";
                check.Parameters.AddWithValue("$id", streamId);
                if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    throw new AppException(AppErrorKind.StorageUnavailable,
                        "Only a stream marked for eviction can be evicted.");
            }

            var name = SpaceName(connection, streamId);
            SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 1 WHERE Id = 1;");
            SyncSql.Execute(connection, "PRAGMA defer_foreign_keys = ON;");
            try
            {
                Prepare(connection);
                CollectSpace(connection, streamId);
                var (entities, discarded) = Remove(connection);
                if (discarded > 0)
                {
                    Attention(connection, streamId,
                        $"You no longer have access to {name ?? "a shared space"}. {discarded.ToString(CultureInfo.InvariantCulture)} unsent "
                        + (discarded == 1 ? "change was" : "changes were") + " discarded.");
                }

                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM AttachmentStreamState WHERE StreamId = $id; DELETE FROM SyncStreams WHERE StreamId = $id AND State = 'evicting';";
                delete.Parameters.AddWithValue("$id", streamId);
                delete.ExecuteNonQuery();
                return Task.FromResult(new SyncEvictionResult(streamId, name, entities, discarded));
            }
            finally
            {
                SyncSql.Execute(connection, $"DROP TABLE IF EXISTS {Keys};");
                SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;");
            }
        }, ct);

    /// <summary>
    /// Evicts single entities and their dependents inside the caller's transaction (a <c>moved</c> change whose target this
    /// device cannot read). The caller has already set the apply flag. Returns the number of rows removed.
    /// </summary>
    internal static long EvictEntities(SqliteConnection connection, IEnumerable<(string Kind, string EntityKey)> seeds)
    {
        Prepare(connection);
        try
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT OR IGNORE INTO {Keys} (Kind, EntityKey) VALUES ($kind, $key);";
            var kind = insert.Parameters.Add("$kind", SqliteType.Text);
            var key = insert.Parameters.Add("$key", SqliteType.Text);
            foreach (var seed in seeds)
            {
                kind.Value = seed.Kind;
                key.Value = seed.EntityKey;
                insert.ExecuteNonQuery();
            }

            Walk(connection, spaceId: null);
            return Remove(connection).Entities;
        }
        finally
        {
            SyncSql.Execute(connection, $"DROP TABLE IF EXISTS {Keys};");
        }
    }

    /// <summary>The (Kind, EntityKey) of every entity of a space, by the same walk an eviction uses.</summary>
    internal static List<(string Kind, string EntityKey)> SpaceEntities(SqliteConnection connection, string spaceId)
    {
        Prepare(connection);
        try
        {
            CollectSpace(connection, spaceId);
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT Kind, EntityKey FROM {Keys};";
            using var reader = select.ExecuteReader();
            var entities = new List<(string, string)>();
            while (reader.Read()) entities.Add((reader.GetString(0), reader.GetString(1)));
            return entities;
        }
        finally
        {
            SyncSql.Execute(connection, $"DROP TABLE IF EXISTS {Keys};");
        }
    }

    private static void Prepare(SqliteConnection connection)
    {
        SyncSql.Execute(connection, $"DROP TABLE IF EXISTS {Keys};");
        SyncSql.Execute(connection, $"CREATE TEMP TABLE {Keys["temp.".Length..]} (Kind TEXT NOT NULL, EntityKey TEXT NOT NULL, PRIMARY KEY (Kind, EntityKey));");
    }

    private static void CollectSpace(SqliteConnection connection, string spaceId)
    {
        using var space = connection.CreateCommand();
        space.CommandText = $"INSERT OR IGNORE INTO {Keys} (Kind, EntityKey) SELECT 'space', Id FROM Spaces WHERE Id = $space COLLATE NOCASE;";
        space.Parameters.AddWithValue("$space", spaceId);
        space.ExecuteNonQuery();
        Walk(connection, spaceId);
    }

    /// <summary>Adds every entity whose route resolves, directly or through parents, to an entity already collected.</summary>
    private static void Walk(SqliteConnection connection, string? spaceId)
    {
        var schema = new SyncSchema(connection);
        foreach (var table in SyncCatalog.Ordered().Where(table => table.Mode != SyncMode.LocalOnly))
        {
            if (SyncKinds.Find(table.Kind) is not { } info) continue;
            var key = KeyExpression(table);
            var route = info.Route;
            string? condition = route.Kind switch
            {
                SyncRouteKind.OwnSpace when spaceId is not null && route.Column is { } column && schema.HasColumn(table.Table, column) =>
                    $"{SyncSql.Quote(column)} = $space COLLATE NOCASE",
                SyncRouteKind.ServerOnly when spaceId is not null && schema.HasColumn(table.Table, "SpaceId") =>
                    "\"SpaceId\" = $space COLLATE NOCASE",
                SyncRouteKind.Parent when route.DiscriminatorColumn is not null && route.Column is { } column =>
                    $"{SyncSql.Quote(column)} IN (SELECT EntityKey FROM {Keys} WHERE Kind IN ('container', 'saved_view'))",
                SyncRouteKind.Parent when route.ParentKind is { } parent && route.KeyPart is { } part =>
                    $"{SyncSql.Quote(table.Key[part])} IN (SELECT EntityKey FROM {Keys} WHERE Kind = {SyncSql.Literal(parent)})",
                SyncRouteKind.Parent when route.ParentKind is { } parent && route.Column is { } column =>
                    $"{SyncSql.Quote(column)} IN (SELECT EntityKey FROM {Keys} WHERE Kind = {SyncSql.Literal(parent)})",
                SyncRouteKind.FollowsTag when route.KeyPart is { } part =>
                    $"{SyncSql.Quote(table.Key[part])} IN (SELECT EntityKey FROM {Keys} WHERE Kind = 'tag')",
                _ => null,
            };
            if (condition is null) continue;

            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT OR IGNORE INTO {Keys} (Kind, EntityKey) SELECT {SyncSql.Literal(table.Kind)}, {key} FROM {SyncSql.Quote(table.Table)} WHERE {condition};";
            if (spaceId is not null) command.Parameters.AddWithValue("$space", spaceId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes the collected entities children first, with their local dependents, clocks, parked and unsent changes.</summary>
    private static (long Entities, long Discarded) Remove(SqliteConnection connection)
    {
        long discarded;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT count(*) FROM SyncOutbox o WHERE EXISTS (SELECT 1 FROM {Keys} k WHERE k.Kind = o.Kind AND k.EntityKey = o.EntityKey);";
            discarded = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        long entities = 0;
        foreach (var table in SyncCatalog.Ordered().Where(table => table.Mode != SyncMode.LocalOnly).Reverse())
        {
            var keys = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT EntityKey FROM {Keys} WHERE Kind = $kind;";
                select.Parameters.AddWithValue("$kind", table.Kind);
                using var reader = select.ExecuteReader();
                while (reader.Read()) keys.Add(reader.GetString(0));
            }

            if (keys.Count == 0) continue;
            var statements = SyncPurgeSql.For(table.Kind, table.Key).Where(KeepsOverlays).ToList();
            statements.Add($"DELETE FROM {SyncSql.Quote(table.Table)} WHERE {string.Join(" AND ", table.Key.Select((column, index) => $"{SyncSql.Quote(column)} = $k{index}"))};");
            using var delete = connection.CreateCommand();
            delete.CommandText = string.Join("\n", statements);
            var parameters = table.Key.Select((_, index) => delete.Parameters.Add($"$k{index}", SqliteType.Text)).ToList();
            foreach (var entityKey in keys)
            {
                var parts = entityKey.Split('|');
                if (parts.Length != parameters.Count) continue;
                for (var index = 0; index < parts.Length; index++) parameters[index].Value = parts[index];
                delete.ExecuteNonQuery();
                entities++;
            }
        }

        foreach (var local in new[] { "SyncClocks", "SyncInboxParked", "SyncOutbox", "SyncPending" })
        {
            SyncSql.Execute(connection, $"DELETE FROM {local} WHERE EXISTS (SELECT 1 FROM {Keys} k WHERE k.Kind = {local}.Kind AND k.EntityKey = {local}.EntityKey);");
        }

        return (entities, discarded);
    }

    /// <summary>
    /// A purge statement is skipped when it would touch an account-stream table: those rows are personal overlays, and
    /// the server tombstones them on every device of the account (§7.7 step 3). Tag links follow their tag and are
    /// collected by the walk when the tag itself leaves.
    /// </summary>
    private static bool KeepsOverlays(string statement)
    {
        var words = statement.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        var target = words.Length < 3 ? null : words[0] == "DELETE" ? words[2] : words[1];
        if (target is null || !TablesByName.TryGetValue(target, out var table) || table.Mode == SyncMode.LocalOnly) return true;
        return SyncKinds.Find(table.Kind)?.Route.Kind is not (SyncRouteKind.Account or SyncRouteKind.FollowsTag);
    }

    private static string KeyExpression(SyncTable table) =>
        string.Join(" || '|' || ", table.Key.Select(SyncSql.Quote));

    private static string? SpaceName(SqliteConnection connection, string spaceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Spaces WHERE Id = $id COLLATE NOCASE;";
        command.Parameters.AddWithValue("$id", spaceId);
        return command.ExecuteScalar() as string;
    }

    private void Attention(SqliteConnection connection, string streamId, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncAttention (Id, Category, Kind, EntityKey, Message, DetailJson, CreatedAtUtc)
            VALUES ($id, 'sync.evicted', 'space', $key, $message, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$key", streamId);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        command.ExecuteNonQuery();
    }
}
