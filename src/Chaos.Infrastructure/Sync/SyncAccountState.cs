using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>The IDs this data root uses for its profile, Personal space and Inbox (PLAN2 §7.13).</summary>
public sealed record LocalIdentityIds(string ProfileId, string PersonalSpaceId, string InboxId);

/// <summary>What this computer holds, as the join dialog shows it: live tasks, lists, tags and pages.</summary>
public sealed record LocalContentCounts(long Tasks, long Lists, long Tags, long Pages)
{
    public bool Any => Tasks + Lists + Tags + Pages > 0;
}

/// <summary>Where a fault is injected into <see cref="SyncAccountState.AdoptAsync"/> by tests: after this many statements ran.</summary>
public sealed record AdoptFaultInjection(int AfterStatements);

/// <summary>
/// The local side of joining, leaving and re-joining an account (PLAN2 §7.13): the account binding in <c>SyncState</c>,
/// adopting the account's canonical profile, Personal space and Inbox IDs, turning sync on and off, and uploading what the
/// account does not have yet. Every method is one short store call; none touches the network.
/// </summary>
public sealed class SyncAccountState(SqliteApplicationStore store)
{
    private static readonly string[] ParentTables = ["Profiles", "Spaces", "Containers"];

    public Task<LocalIdentityIds> ReadIdsAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(ReadIds(((SqliteDataSession)session).Connection)), ct);

    public Task<LocalContentCounts> CountAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            return Task.FromResult(new LocalContentCounts(
                SyncSql.Scalar(connection, "SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;"),
                SyncSql.Scalar(connection, "SELECT count(*) FROM Containers WHERE DeletedAtUtc IS NULL AND IsInbox = 0;"),
                SyncSql.Scalar(connection, "SELECT count(*) FROM Tags WHERE DeletedAtUtc IS NULL;"),
                SyncSql.Scalar(connection, "SELECT count(*) FROM Pages WHERE DeletedAtUtc IS NULL;")));
        }, ct);

    /// <summary>The account this device syncs with, or null when it is offline-only.</summary>
    public Task<string?> BoundAccountAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(SyncSql.Text(((SqliteDataSession)session).Connection, "SELECT AccountId FROM SyncState WHERE Id = 1;")), ct);

    public Task<bool> IsEnabledAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(SyncSql.Scalar(((SqliteDataSession)session).Connection, "SELECT Enabled FROM SyncState WHERE Id = 1;") == 1), ct);

    private static LocalIdentityIds ReadIds(SqliteConnection connection)
    {
        var profile = SyncSql.Text(connection, "SELECT Id FROM Profiles ORDER BY CreatedAtUtc, Id LIMIT 1;")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no profile.");
        var space = SyncSql.Text(connection, $"SELECT Id FROM Spaces WHERE OwnerProfileId = {SyncSql.Literal(profile)} AND Kind = 1 AND DeletedAtUtc IS NULL ORDER BY Id LIMIT 1;")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no Personal space.");
        var inbox = SyncSql.Text(connection, $"SELECT Id FROM Containers WHERE SpaceId = {SyncSql.Literal(space)} AND IsInbox = 1 AND DeletedAtUtc IS NULL ORDER BY Id LIMIT 1;")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no Inbox.");
        return new LocalIdentityIds(profile, space, inbox);
    }

    /// <summary>
    /// Rewrites this device's profile, Personal space and Inbox IDs to the account's canonical ones, in one transaction: the
    /// three parent rows and every foreign-key column that points at them (found from the database's own foreign-key list, so a
    /// later migration cannot be missed), then <c>PRAGMA foreign_key_check</c>. Anything wrong rolls the whole thing back. The
    /// capture triggers are off while it runs, and the sync bookkeeping is cleared because it names the old IDs.
    /// </summary>
    public Task AdoptAsync(LocalIdentityIds canonical, AdoptFaultInjection? fault = null, CancellationToken ct = default) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            var local = ReadIds(connection);
            var statements = 0;
            void Run(string sql, params (string Name, string Value)[] parameters)
            {
                if (fault is not null && statements >= fault.AfterStatements)
                {
                    throw new AppException(AppErrorKind.StorageUnavailable, "Injected failure while adopting the account's IDs.");
                }

                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                command.ExecuteNonQuery();
                statements++;
            }

            SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 1 WHERE Id = 1;");
            try
            {
                SyncSql.Execute(connection, "PRAGMA defer_foreign_keys = ON;");
                var moves = new (string Parent, string Old, string New)[]
                {
                    ("Profiles", local.ProfileId, canonical.ProfileId),
                    ("Spaces", local.PersonalSpaceId, canonical.PersonalSpaceId),
                    ("Containers", local.InboxId, canonical.InboxId),
                };
                var columns = ForeignKeyColumns(connection);
                foreach (var (parent, from, to) in moves)
                {
                    if (string.Equals(from, to, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Run($"UPDATE {SyncSql.Quote(parent)} SET Id = $to WHERE Id = $from;", ("$to", to), ("$from", from));
                    // History keeps JSON snapshots of the aggregates it describes; undoing from one must name the IDs that exist now.
                    Run("UPDATE TaskHistory SET SnapshotBefore = replace(SnapshotBefore, $from, $to), SnapshotAfter = replace(SnapshotAfter, $from, $to) WHERE instr(COALESCE(SnapshotBefore, '') || COALESCE(SnapshotAfter, ''), $from) > 0;", ("$to", to), ("$from", from));
                    foreach (var (table, column, _) in columns.Where(c => c.Parent == parent))
                    {
                        Run($"UPDATE {SyncSql.Quote(table)} SET {SyncSql.Quote(column)} = $to WHERE {SyncSql.Quote(column)} = $from;", ("$to", to), ("$from", from));
                    }
                }

                Run("DELETE FROM SyncOutbox;");
                Run("DELETE FROM SyncPending;");
                Run("DELETE FROM SyncClocks;");
                Run("DELETE FROM SyncInboxParked;");

                using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA foreign_key_check;";
                using var reader = check.ExecuteReader();
                if (reader.Read())
                {
                    throw new AppException(AppErrorKind.StorageUnavailable, "Adopting the account's IDs left a broken reference, so nothing was changed.");
                }
            }
            finally
            {
                SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;");
            }

            return Task.FromResult(0);
        }, ct);

    private static List<(string Table, string Column, string Parent)> ForeignKeyColumns(SqliteConnection connection)
    {
        var columns = new List<(string Table, string Column, string Parent)>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.name, f."from", f."table"
            FROM sqlite_master m, pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table'
            ORDER BY m.name, f."from";
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parent = ParentTables.FirstOrDefault(name => string.Equals(name, reader.GetString(2), StringComparison.OrdinalIgnoreCase));
            if (parent is not null)
            {
                columns.Add((reader.GetString(0), reader.GetString(1), parent));
            }
        }

        return columns;
    }

    /// <summary>Turns sync on for this account: the cursor starts at zero and the upload of what the account lacks is owed.</summary>
    public Task EnableAsync(Guid accountId, CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            var ids = ReadIds(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE SyncState SET Enabled = 1, AccountId = $account, ProfileBinding = $profile, Cursor = 0, BackfillPending = 1, LastError = NULL WHERE Id = 1;";
            command.Parameters.AddWithValue("$account", accountId.ToString("D"));
            command.Parameters.AddWithValue("$profile", ids.ProfileId);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);

    /// <summary>
    /// Makes this device offline-only: sync off, binding forgotten, the outbox and every clock cleared (the clocks belong to the
    /// account being left; keeping them would make a later join think the data was already uploaded). The data stays.
    /// </summary>
    public Task DisableAsync(CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            SyncSql.Execute(connection, "UPDATE SyncState SET Enabled = 0, AccountId = NULL, ProfileBinding = NULL, Cursor = 0, BackfillPending = 0, LastError = NULL WHERE Id = 1;");
            SyncSql.Execute(connection, "DELETE FROM SyncOutbox;");
            SyncSql.Execute(connection, "DELETE FROM SyncPending;");
            SyncSql.Execute(connection, "DELETE FROM SyncClocks;");
            SyncSql.Execute(connection, "DELETE FROM SyncInboxParked;");
            return Task.FromResult(0);
        }, ct);

    /// <summary>A new device ID for a copied data root, and a clock that starts over (the old one names the old device).</summary>
    public Task ReplaceDeviceIdAsync(Guid deviceId, CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE DeviceSettings SET Value = $id WHERE Key = 'DeviceId'; UPDATE SyncState SET LastHlc = NULL WHERE Id = 1;";
            command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);

    public Task<bool> BackfillPendingAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(SyncSql.Scalar(((SqliteDataSession)session).Connection, "SELECT BackfillPending FROM SyncState WHERE Id = 1;") == 1), ct);

    /// <summary>
    /// Queues every row and field group the sync bookkeeping has never seen (no clock) as a change, parents first, one table per
    /// transaction. The write's flush gives each a fresh clock and an outbox row. For the first device that is everything; for a
    /// device that joined, the pull has already given the account's rows their clocks, so only what is new here goes up. Safe to run
    /// again: a second pass finds nothing without a clock.
    /// </summary>
    public async Task<long> BackfillAsync(CancellationToken ct)
    {
        long queued = 0;
        foreach (var table in SyncCatalog.Ordered().Where(table => table.Mode != SyncMode.LocalOnly))
        {
            queued += await store.ExecuteAsync((session, _) =>
            {
                var connection = ((SqliteDataSession)session).Connection;
                long count = 0;
                var key = string.Join(" || '|' || ", table.Key.Select(column => $"t.{SyncSql.Quote(column)}"));
                foreach (var group in table.Groups)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        INSERT INTO SyncPending (Kind, EntityKey, GroupName)
                        SELECT {SyncSql.Literal(table.Kind)}, {key}, {SyncSql.Literal(group.Name)} FROM {SyncSql.Quote(table.Table)} t
                        WHERE NOT EXISTS (SELECT 1 FROM SyncClocks c WHERE c.Kind = {SyncSql.Literal(table.Kind)} AND c.EntityKey = {key} AND c.GroupName = {SyncSql.Literal(group.Name)})
                          AND NOT EXISTS (SELECT 1 FROM SyncPending p WHERE p.Kind = {SyncSql.Literal(table.Kind)} AND p.EntityKey = {key} AND p.GroupName = {SyncSql.Literal(group.Name)});
                        """;
                    count += command.ExecuteNonQuery();
                }

                return Task.FromResult(count);
            }, ct);
        }

        await store.ExecuteAsync((session, _) =>
        {
            SyncSql.Execute(((SqliteDataSession)session).Connection, "UPDATE SyncState SET BackfillPending = 0 WHERE Id = 1;");
            return Task.FromResult(0);
        }, ct);
        return queued;
    }
}
