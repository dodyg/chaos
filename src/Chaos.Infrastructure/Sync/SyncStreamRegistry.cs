using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>A stream advertised by the service. Kind is account or space.</summary>
public sealed record DiscoveredStream(Guid Id, string Kind, Guid? SpaceId, int? Role);

/// <summary>Reconciles the service's readable streams with durable local join and eviction state.</summary>
public sealed class SyncStreamRegistry(SqliteApplicationStore store)
{
    public Task ReconcileAsync(Guid accountId, IReadOnlyList<DiscoveredStream> streams, CancellationToken ct = default)
    {
        if (accountId == Guid.Empty || streams.Select(s => s.Id).Distinct().Count() != streams.Count
            || streams.Count(s => s.Kind == "account" && s.Id == accountId && s.SpaceId is null && s.Role is null) != 1
            || streams.Any(s => s.Id == Guid.Empty || (s.Kind == "account" && (s.Id != accountId || s.SpaceId is not null || s.Role is not null))
                || (s.Kind == "space" && (s.SpaceId != s.Id || s.Role is not (1 or 2 or 3)))
                || s.Kind is not ("account" or "space")))
            throw new AppException(AppErrorKind.Validation, "The service returned an invalid stream list.");

        return store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            using (var account = connection.CreateCommand())
            {
                account.CommandText = "SELECT AccountId FROM SyncState WHERE Id=1;";
                if (!string.Equals(account.ExecuteScalar() as string, accountId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new AppException(AppErrorKind.StorageUnavailable, "The discovered streams do not match this device's account.");
                account.CommandText = "SELECT count(*) FROM SyncStreams WHERE Kind='account' AND StreamId<>$account COLLATE NOCASE;";
                account.Parameters.AddWithValue("$account", accountId.ToString("D"));
                if (Convert.ToInt64(account.ExecuteScalar()) != 0)
                    throw new AppException(AppErrorKind.StorageUnavailable, "This device still has stream state for another account.");
            }
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SyncStreams (StreamId, Kind, SpaceId, Role, Cursor, State)
                VALUES ($id, $kind, $space, $role, CASE WHEN $kind='account' THEN (SELECT Cursor FROM SyncState WHERE Id=1) ELSE 0 END, $state)
                ON CONFLICT(StreamId) DO UPDATE SET Role=excluded.Role
                WHERE SyncStreams.State <> 'evicting' AND SyncStreams.Kind=excluded.Kind
                  AND SyncStreams.SpaceId IS excluded.SpaceId;
                """;
            var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var kind = command.Parameters.Add("$kind", Microsoft.Data.Sqlite.SqliteType.Text);
            var space = command.Parameters.Add("$space", Microsoft.Data.Sqlite.SqliteType.Text);
            var role = command.Parameters.Add("$role", Microsoft.Data.Sqlite.SqliteType.Integer);
            var state = command.Parameters.Add("$state", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var stream in streams)
            {
                id.Value = stream.Id.ToString("D");
                kind.Value = stream.Kind;
                space.Value = stream.SpaceId is { } spaceId ? spaceId.ToString("D") : DBNull.Value;
                role.Value = stream.Role is { } memberRole ? memberRole : DBNull.Value;
                state.Value = stream.Kind == "account" ? "active" : "joining";
                if (command.ExecuteNonQuery() == 0)
                {
                    using var check = connection.CreateCommand();
                    check.CommandText = "SELECT count(*) FROM SyncStreams WHERE StreamId=$id AND Kind=$kind AND SpaceId IS $space AND State='evicting';";
                    check.Parameters.AddWithValue("$id", id.Value);
                    check.Parameters.AddWithValue("$kind", kind.Value);
                    check.Parameters.AddWithValue("$space", space.Value);
                    if (Convert.ToInt64(check.ExecuteScalar()) != 1)
                        throw new AppException(AppErrorKind.StorageUnavailable, "The service returned a stream with conflicting local identity.");
                }
            }

            using var missing = connection.CreateCommand();
            var names = new List<string>();
            foreach (var stream in streams.Where(s => s.Kind == "space"))
            {
                var parameter = "$id" + names.Count;
                names.Add(parameter);
                missing.Parameters.AddWithValue(parameter, stream.Id.ToString("D"));
            }
            missing.CommandText = names.Count == 0
                ? "UPDATE SyncStreams SET State='evicting' WHERE Kind='space';"
                : $"UPDATE SyncStreams SET State='evicting' WHERE Kind='space' AND StreamId NOT IN ({string.Join(",", names)});";
            missing.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
    }
}
