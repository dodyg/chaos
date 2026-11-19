using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>What the sync indicator says (PLAN2 P15 item 3). <see cref="Hidden"/> means this computer has never used an account: nothing is shown.</summary>
public enum SyncStatusKind
{
    Hidden = 0,
    Synced = 1,
    Syncing = 2,
    Offline = 3,
    NeedsAttention = 4,
    SignedOut = 5,
    UpdateNeeded = 6,
}

/// <summary>One thing the person should look at (PLAN2 §7.9), with a plain explanation and what can be done about it.</summary>
public sealed record SyncAttentionItem(string Id, string Category, string Message, string Suggestion, DateTimeOffset CreatedAt, IReadOnlyList<string> Actions);

/// <summary>The sync state as the indicator and Settings → Account → Sync show it.</summary>
public sealed record SyncStatus(SyncStatusKind Kind, DateTimeOffset? LastSyncedAt, long PendingChanges, IReadOnlyList<SyncAttentionItem> Attention, string? Detail);

/// <summary>
/// Works out <see cref="SyncStatus"/> from <c>SyncState</c>, the outbox, <c>SyncAttention</c> and the engine's live state. It reads the
/// local database only, so it never waits on the network and is cheap enough to poll.
/// </summary>
public sealed class SyncStatusReader(SqliteApplicationStore store, SyncEngine engine)
{
    public async Task<SyncStatus> GetAsync(CancellationToken ct)
    {
        var (enabled, lastPush, lastPull, pending, attention) = await store.QueryAsync((session, _) =>
        {
            var connection = session.Connection();
            bool enabledNow;
            string? push, pull;
            using (var state = connection.CreateCommand())
            {
                state.CommandText = "SELECT Enabled, LastPushUtc, LastPullUtc FROM SyncState WHERE Id = 1;";
                using var reader = state.ExecuteReader();
                reader.Read();
                enabledNow = reader.GetInt64(0) == 1;
                push = reader.IsDBNull(1) ? null : reader.GetString(1);
                pull = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            var waiting = SyncSql.Scalar(connection, "SELECT count(*) FROM SyncOutbox;");
            var items = new List<SyncAttentionItem>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT Id, Category, Message, CreatedAtUtc FROM SyncAttention WHERE ResolvedAtUtc IS NULL ORDER BY CreatedAtUtc, Id;";
                using var reader = list.ExecuteReader();
                while (reader.Read())
                {
                    var category = reader.GetString(1);
                    var (suggestion, actions) = Describe(category);
                    items.Add(new SyncAttentionItem(reader.GetString(0), category, reader.GetString(2), suggestion,
                        DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal), actions));
                }
            }

            return Task.FromResult((enabledNow, push, pull, waiting, (IReadOnlyList<SyncAttentionItem>)items));
        }, ct);

        var last = new[] { lastPush, lastPull }.Where(value => value is not null)
            .Select(value => DateTimeOffset.Parse(value!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal))
            .DefaultIfEmpty().Max();
        var runtime = engine.Runtime;
        // An idle cycle writes nothing (PLAN2 7.4), so the engine remembers when the last good one finished.
        DateTimeOffset? lastSynced = last == default ? runtime.LastSuccessAt : runtime.LastSuccessAt is { } ran && ran > last ? ran : last;
        return Decide(enabled, lastSynced, pending, attention, runtime);
    }

    /// <summary>The indicator's rule, in order of how much the person needs to know (separate so tests can drive it with a fake engine).</summary>
    public static SyncStatus Decide(bool enabled, DateTimeOffset? lastSynced, long pending, IReadOnlyList<SyncAttentionItem> attention, SyncRuntime runtime)
    {
        if (!enabled)
        {
            // A computer that synced once and is not syncing now has signed out (or was removed); one that never did shows nothing.
            return lastSynced is null
                ? new SyncStatus(SyncStatusKind.Hidden, null, pending, attention, null)
                : new SyncStatus(SyncStatusKind.SignedOut, lastSynced, pending, attention, "Signed out. This computer keeps its data and works offline.");
        }

        if (runtime.Failure == ServiceFailure.SignInAgain)
        {
            return new SyncStatus(SyncStatusKind.SignedOut, lastSynced, pending, attention, "Sign in again to keep syncing.");
        }

        if (runtime.UpdateNeeded)
        {
            return new SyncStatus(SyncStatusKind.UpdateNeeded, lastSynced, pending, attention, "Update Chaos to keep syncing.");
        }

        if (runtime.Failure is not null and not ServiceFailure.Offline)
        {
            return new SyncStatus(SyncStatusKind.NeedsAttention, lastSynced, pending, attention,
                runtime.FailureDetail ?? "The online service could not complete sync. Try syncing again.");
        }

        if (runtime.Failure == ServiceFailure.Offline)
        {
            return new SyncStatus(SyncStatusKind.Offline, lastSynced, pending, attention,
                pending > 0 ? $"Offline. {pending} {(pending == 1 ? "change is" : "changes are")} waiting." : "Offline. Nothing is waiting.");
        }

        if (attention.Count > 0)
        {
            return new SyncStatus(SyncStatusKind.NeedsAttention, lastSynced, pending, attention, $"{attention.Count} {(attention.Count == 1 ? "thing needs" : "things need")} a look.");
        }

        return runtime.Syncing
            ? new SyncStatus(SyncStatusKind.Syncing, lastSynced, pending, attention, "Syncing…")
            : new SyncStatus(SyncStatusKind.Synced, lastSynced, pending, attention, null);
    }

    /// <summary>The plain explanation and the actions for an attention category (PLAN2 §7.9).</summary>
    private static (string Suggestion, IReadOnlyList<string> Actions) Describe(string category) => category switch
    {
        "sync.retired" => ("Chaos kept one copy of two that were made on different devices. The other copy is in Deleted items, so you can restore it.", ["Dismiss"]),
        "sync.parked" => ("Changes from another device could not be applied yet. Syncing again may fix it; if it does not, update Chaos on this computer.", ["Sync now", "Dismiss"]),
        "sync.rejected" => ("The online service did not accept a change from this computer. If it says to update Chaos, do that first.", ["Sync now", "Dismiss"]),
        "sync.invalid" or "sync.conflict" or "sync.adopt" or "sync.unexpected" => ("A change from another device could not be applied. Your own data is unchanged.", ["Sync now", "Dismiss"]),
        "attachment.blocked" => ("The online service's malware check blocked this file. It stays on this computer and is not shared with your other devices. Remove it if you do not trust it.", ["Dismiss"]),
        "attachment.quota" => ("Your online storage is full, so this file was not uploaded. It stays on this computer. Remove files you no longer need; Chaos tries again every hour.", ["Dismiss"]),
        _ => ("Look at what changed and dismiss this when you are done.", ["Dismiss"]),
    };

    /// <summary>Marks an attention item as dealt with. A parked or rejected one comes back if the problem is still there.</summary>
    public Task ResolveAsync(string id, CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE SyncAttention SET ResolvedAtUtc = $now WHERE Id = $id AND ResolvedAtUtc IS NULL;";
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
}
