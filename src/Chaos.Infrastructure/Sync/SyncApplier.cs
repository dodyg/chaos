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

/// <summary>What one apply pass did (PLAN2 §7.4); the changed entity list feeds P13's publishers.</summary>
public sealed record SyncApplyOutcome(
    long Applied,
    long Purged,
    long Skipped,
    IReadOnlyList<string> ParkedChangeIds)
{
    public bool Changed => Applied > 0 || Purged > 0;
}

/// <summary>
/// Applies remote changes to the local database (PLAN2 §7.4) in the serialized writer: merge per
/// field group by hybrid clock, purges as hard deletes with their dependents, unique-index breaks
/// resolved by the catalog's deterministic rules (§7.6), changes that reference something not yet
/// present parked in <c>SyncInboxParked</c> and retried after the batch (§7.8), replaced values kept
/// in history (§7.7), the local <c>Version</c> bumped once per changed entity so stale undo entries
/// and open editors notice (§7.10), and the search documents of changed sources refreshed. There is
/// no network here: P13 feeds it from the server, tests feed it from another data root's outbox.
/// The capture triggers are suppressed while applying because every device runs the same
/// deterministic merge and the same rules from the same inputs, so nothing an apply writes would
/// add information by being captured again.
/// </summary>
public sealed partial class SyncApplier(SqliteApplicationStore store, TimeProvider clock, IIdGenerator ids)
{
    private static readonly Dictionary<string, SyncTable> TablesByKind = SyncCatalog.Tables.ToDictionary(table => table.Kind);

    private const string ChangeSavepoint = "sync_change";

    public async Task<SyncApplyOutcome> ApplyAsync(IReadOnlyList<string> changePayloads, CancellationToken ct = default, long? throughSequence = null)
    {
        var parked = new List<string>();
        var applied = 0L;
        var purged = 0L;
        var skipped = 0L;

        // A payload that does not parse cannot be parked (it has no change id), so it is skipped with an attention item
        // instead of failing the whole chunk, which would leave the cursor behind it forever.
        var changes = new List<(SyncChange Change, string Payload)>();
        var malformed = new List<string>();
        foreach (var payload in changePayloads)
        {
            try
            {
                changes.Add((SyncChangeParser.Parse(payload), payload));
            }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
            {
                malformed.Add(error.GetType().Name);
            }
        }

        skipped += malformed.Count;

        await store.ExecuteAsync(async (session, token) =>
        {
            var connection = session.Connection();
            var schema = new SyncSchema(connection);
            SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 1 WHERE Id = 1;");
            SyncSql.Execute(connection, "PRAGMA defer_foreign_keys = ON;");
            try
            {
                // Progress (applied + purged) when each change of this pass was parked: retrying
                // it is only worth it once something else landed after it.
                var parkedAtProgress = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var errorType in malformed)
                {
                    Attention(connection, "sync.unexpected", null, null, $"A change from another device could not be read ({errorType}) and was ignored.");
                }

                foreach (var (change, payload) in changes)
                {
                    var outcome = await ApplyStepAsync(connection, schema, change, payload, token);
                    Count(outcome, change.ChangeId, ref applied, ref purged, ref skipped, parked);
                    if (outcome is StepResult.Parked)
                    {
                        parkedAtProgress[change.ChangeId] = applied + purged;
                    }
                }

                // §7.8: after each batch, parked changes are retried; a parent that arrived in
                // this batch lets its children through. A change parked in this pass with nothing
                // applied since is not retried: it would only park again.
                foreach (var (changeId, payload) in ReadParked(connection))
                {
                    if (parkedAtProgress.TryGetValue(changeId, out var progress) && progress == applied + purged)
                    {
                        continue;
                    }

                    SyncChange change;
                    try
                    {
                        change = SyncChangeParser.Parse(payload);
                    }
                    catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
                    {
                        continue;
                    }

                    var outcome = await ApplyStepAsync(connection, schema, change, payload, token);
                    if (outcome is StepResult.Parked)
                    {
                        BumpParkedCycles(connection, changeId);
                        if (!parked.Contains(changeId))
                        {
                            parked.Add(changeId);
                        }
                    }
                    else
                    {
                        parked.Remove(changeId);
                        Count(outcome, change.ChangeId, ref applied, ref purged, ref skipped, parked);
                        DeleteParked(connection, changeId);
                    }
                }

                if (throughSequence is { } sequence)
                {
                    SyncSql.Execute(connection, $"UPDATE SyncState SET Cursor = MAX(Cursor, {sequence.ToString(CultureInfo.InvariantCulture)}) WHERE Id = 1;");
                }

                ReceiveClocks(connection, changes.Select(entry => entry.Change));
            }
            finally
            {
                SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;");
            }

            return 0;
        }, ct);

        return new SyncApplyOutcome(applied, purged, skipped, parked);

        static void Count(StepResult outcome, string changeId, ref long applied, ref long purged, ref long skipped, List<string> parked)
        {
            switch (outcome)
            {
                case StepResult.Applied: applied++; break;
                case StepResult.Purged: purged++; break;
                case StepResult.Parked:
                    if (!parked.Contains(changeId))
                    {
                        parked.Add(changeId);
                    }

                    break;
                case StepResult.Skipped: skipped++; break;
            }
        }
    }

    /// <summary>
    /// §7.1 receive: the local clock learns the highest received clock, so the next local edit
    /// ticks past everything that arrived; without it, a device that only applied changes would
    /// keep issuing clocks older than what it received. A clock further ahead than
    /// <see cref="Hlc.MaxAcceptedSkew"/> is kept (it already won its merge everywhere) but
    /// reported once per sending device, as PLAN2 §P1 asks.
    /// </summary>
    private void ReceiveClocks(SqliteConnection connection, IEnumerable<SyncChange> changes)
    {
        var received = changes.SelectMany(change => change.Groups.Values.Select(group => group.Hlc)).ToList();
        if (received.Count == 0)
        {
            return;
        }

        var device = SyncSql.Text(connection, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';")
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "This data root has no device identity, so changes cannot be applied.");

        // Receive keeps this device's id on the clock (§7.1): only the wall and counter advance,
        // so two devices never issue the same clock for different changes.
        var hybrid = new HybridClock(Guid.Parse(device), clock);
        if (SyncSql.Text(connection, "SELECT LastHlc FROM SyncState WHERE Id = 1;") is { } last)
        {
            hybrid.Restore(Hlc.Parse(last));
        }

        var highest = received.Max();
        var result = hybrid.Receive(highest);
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE SyncState SET LastHlc = $hlc WHERE Id = 1;";
            update.Parameters.AddWithValue("$hlc", result.Clock.Encode());
            update.ExecuteNonQuery();
        }

        if (result.SkewBeyondCap && !OpenAttentionExists(connection, "sync.clock_skew", highest.DeviceId))
        {
            Attention(connection, "sync.clock_skew", null, highest.DeviceId,
                "Another device's clock is more than a day ahead of this one. Its changes were kept; check the date and time settings on both devices.");
        }
    }

    private enum StepResult
    {
        Applied,
        Purged,
        Skipped,
        Parked,
    }

    /// <summary>
    /// Why a change must wait in the inbox (§7.8), and the attention item that goes with it. The
    /// apply code fills it in; <see cref="ApplyStepAsync"/> writes it after rolling back whatever
    /// the failed attempt already wrote.
    /// </summary>
    private sealed class ParkSlip
    {
        public string Reason { get; private set; } = "the change could not be applied yet";

        public (string Category, string Message)? Notice { get; private set; }

        public void Park(string reason, string? category = null, string? message = null)
        {
            Reason = reason;
            Notice = category is null || message is null ? null : (category, message);
        }
    }

    /// <summary>
    /// Applies one change inside a savepoint. A parked change rolls back to it first, so history
    /// rows and rule side effects of the failed attempt do not stay behind; only the parked row
    /// (and, the first time, its attention item) remain.
    /// </summary>
    private async Task<StepResult> ApplyStepAsync(SqliteConnection connection, SyncSchema schema, SyncChange change, string payload, CancellationToken ct)
    {
        var slip = new ParkSlip();
        SyncSql.Execute(connection, $"SAVEPOINT {ChangeSavepoint};");
        StepResult outcome;
        try
        {
            outcome = await ApplyOneAsync(connection, schema, change, slip, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // One bad change (a malformed key, an unclassified constraint failure) must not block the chunk: set it aside.
            slip.Park($"the change could not be applied ({error.GetType().Name}: {error.Message})",
                "sync.unapplicable", "A change from another device could not be applied. It is set aside and tried again later.");
            outcome = StepResult.Parked;
        }

        if (outcome is StepResult.Parked)
        {
            SyncSql.Execute(connection, $"ROLLBACK TO {ChangeSavepoint};");
            if (Park(connection, change, payload, slip.Reason) && slip.Notice is { } notice)
            {
                Attention(connection, notice.Category, change.Kind, change.EntityKey, notice.Message);
            }
        }

        SyncSql.Execute(connection, $"RELEASE {ChangeSavepoint};");
        return outcome;
    }

    private async Task<StepResult> ApplyOneAsync(SqliteConnection connection, SyncSchema schema, SyncChange change, ParkSlip park, CancellationToken ct)
    {
        var table = TablesByKind.GetValueOrDefault(change.Kind);
        if (table is null || table.Mode == SyncMode.LocalOnly)
        {
            Attention(connection, "sync.unexpected", change.Kind, change.EntityKey,
                $"A change arrived for '{change.Kind}', which does not take part in sync. It was ignored.");
            return StepResult.Skipped;
        }

        if (change.Op == SyncOp.Purge)
        {
            // Idempotent: a purge whose row is already gone is a replay — the entity's clock
            // watermark (or its total absence) proves this device already settled it.
            var key = ParseKey(table, change.EntityKey);
            if (!RowExists(connection, table, key))
            {
                return StepResult.Skipped;
            }

            PurgeEntity(connection, schema, table, change.EntityKey);
            return StepResult.Purged;
        }

        return await ApplyUpsertAsync(connection, schema, table, change, park, ct);
    }
}
