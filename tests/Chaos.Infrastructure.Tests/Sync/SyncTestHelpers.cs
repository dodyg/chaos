using System.Text.Json;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>Sync-enabled observation helpers for the capture tests (PLAN2 §7.3).</summary>
internal static class SyncTestHelpers
{
    /// <summary>Turns sync on; the flush then turns pended groups into outbox changes.</summary>
    public static Task EnableSyncAsync(DataProtection.DataHost host) =>
        host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = "UPDATE SyncState SET Enabled = 1 WHERE Id = 1;";
            await command.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);

    public static async Task<string> TextAsync(DataProtection.DataHost host, string sql)
    {
        var value = await ScalarAsync(host, sql);
        return value as string ?? throw new InvalidOperationException($"No text result for: {sql}");
    }

    public static async Task<object?> ScalarAsync(DataProtection.DataHost host, string sql) =>
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(token);
        });

    /// <summary>The outbox changes of one entity, oldest first, with (op, group names).</summary>
    public static async Task<IReadOnlyList<(string Op, IReadOnlyList<string> Groups)>> ChangesAsync(
        DataProtection.DataHost host, string kind, string entityKey)
    {
        var payloads = new List<string>();
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = "SELECT Op, PayloadJson FROM SyncOutbox WHERE Kind = $kind AND EntityKey = $key ORDER BY Seq;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$key", entityKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                payloads.Add(reader.GetString(1));
            }

            return 0;
        }, CancellationToken.None);

        return [.. payloads.Select(payload =>
        {
            var root = JsonDocument.Parse(payload).RootElement;
            var op = root.GetProperty("op").GetString()!;
            var groups = root.GetProperty("groups").EnumerateObject().Select(property => property.Name).ToList();
            return (op, (IReadOnlyList<string>)groups);
        })];
    }
}
