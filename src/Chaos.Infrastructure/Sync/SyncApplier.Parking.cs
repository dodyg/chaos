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
    // ---- Parking and attention (§7.8, §7.9) --------------------------------------------------

    /// <summary>Parks a change (§7.8); true when it was not parked before, so its attention item is raised only once.</summary>
    private bool Park(SqliteConnection connection, SyncChange change, string payload, string reason)
    {
        bool isNew;
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM SyncInboxParked WHERE ChangeId = $id;";
            exists.Parameters.AddWithValue("$id", change.ChangeId);
            isNew = exists.ExecuteScalar() is null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncInboxParked (ChangeId, Kind, EntityKey, Op, PayloadJson, Reason, FirstParkedAtUtc, PullCycles)
            VALUES ($id, $kind, $key, $op, $payload, $reason, $now, 0)
            ON CONFLICT (ChangeId) DO UPDATE SET Reason = excluded.Reason;
            """;
        command.Parameters.AddWithValue("$id", change.ChangeId);
        command.Parameters.AddWithValue("$kind", change.Kind);
        command.Parameters.AddWithValue("$key", change.EntityKey);
        command.Parameters.AddWithValue("$op", change.Op == SyncOp.Purge ? "purge" : "upsert");
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        command.ExecuteNonQuery();
        return isNew;
    }

    private static List<(string ChangeId, string Payload)> ReadParked(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ChangeId, PayloadJson FROM SyncInboxParked ORDER BY FirstParkedAtUtc, ChangeId;";
        using var reader = command.ExecuteReader();
        var parked = new List<(string, string)>();
        while (reader.Read())
        {
            parked.Add((reader.GetString(0), reader.GetString(1)));
        }

        return parked;
    }

    private static void DeleteParked(SqliteConnection connection, string changeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncInboxParked WHERE ChangeId = $id;";
        command.Parameters.AddWithValue("$id", changeId);
        command.ExecuteNonQuery();
    }

    private static void BumpParkedCycles(SqliteConnection connection, string changeId)
    {
        // §7.8: parked for more than ten pull cycles becomes an attention item — the pull loop
        // itself raises it (P13); the counter is what it reads.
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SyncInboxParked SET PullCycles = PullCycles + 1 WHERE ChangeId = $id;";
        command.Parameters.AddWithValue("$id", changeId);
        command.ExecuteNonQuery();
    }

    private static bool OpenAttentionExists(SqliteConnection connection, string category, string entityKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SyncAttention WHERE Category = $category AND EntityKey = $key AND ResolvedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$key", entityKey);
        return command.ExecuteScalar() is not null;
    }

    private void Attention(SqliteConnection connection, string category, string? kind, string? entityKey, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncAttention (Id, Category, Kind, EntityKey, Message, DetailJson, CreatedAtUtc)
            VALUES ($id, $category, $kind, $key, $message, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)entityKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        command.ExecuteNonQuery();
    }
}
