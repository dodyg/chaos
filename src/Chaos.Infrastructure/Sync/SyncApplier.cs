using System.Globalization;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Infrastructure.Search;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Activity;
using Chaos.Infrastructure.Sync.UniqueRules;
using Chaos.Domain.Tasks;
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

    private sealed record MovedChange(string ChangeId, string Kind, string EntityKey, string Target);

    public async Task<SyncApplyOutcome> ApplyAsync(IReadOnlyList<string> changePayloads, CancellationToken ct = default, long? throughSequence = null, string? streamId = null)
    {
        var parked = new List<string>();
        var applied = 0L;
        var purged = 0L;
        var skipped = 0L;

        // A payload that does not parse cannot be parked (it has no change id), so it is skipped with an attention item
        // instead of failing the whole chunk, which would leave the cursor behind it forever.
        var changes = new List<(SyncChange? Change, MovedChange? Moved, string Payload)>();
        var malformed = new List<string>();
        foreach (var payload in changePayloads)
        {
            var isMoved = false;
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.TryGetProperty("op", out var operation) && operation.GetString() == "moved")
                {
                    isMoved = true;
                    var changeId = root.GetProperty("changeId").GetString();
                    var kind = root.GetProperty("kind").GetString();
                    var key = root.GetProperty("key").GetString();
                    var target = root.GetProperty("to").GetString();
                    if (string.IsNullOrWhiteSpace(changeId) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(key)
                        || !Guid.TryParseExact(target, "D", out var targetId))
                        throw new AppException(AppErrorKind.StorageUnavailable, "A moved change from the service is invalid.");
                    changes.Add((null, new MovedChange(changeId, kind, key, targetId.ToString("D")), payload));
                }
                else
                    changes.Add((SyncChangeParser.Parse(payload), null, payload));
            }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
            {
                if (isMoved) throw new AppException(AppErrorKind.StorageUnavailable, "A moved change from the service is invalid.");
                malformed.Add(error.GetType().Name);
            }
        }

        skipped += malformed.Count;

        using var activity = ActivityScope.Open(ActivitySources.Sync, ActivityActions.RemoteApply);
        await store.ExecuteAsync(async (session, token) =>
        {
            var connection = session.Connection();
            var schema = new SyncSchema(connection);
            var assignmentTasks = new HashSet<string>(StringComparer.Ordinal);
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

                foreach (var (change, moved, payload) in changes)
                {
                    if (moved is not null)
                    {
                        var result = ApplyMoved(connection, schema, moved);
                        if (result == StepResult.Applied)
                            session.Activity.Note(moved.Kind, moved.EntityKey, ActivityOp.Upsert);
                        Count(result, moved.ChangeId, ref applied, ref purged, ref skipped, parked);
                        continue;
                    }
                    if (change is null) throw new InvalidOperationException("A parsed sync change is missing.");
                    var outcome = await ApplyStepWithTextAsync((SqliteDataSession)session, connection, schema, change, payload, token);
                    if (outcome is StepResult.Applied or StepResult.Purged)
                        session.Activity.Note(change.Kind, change.EntityKey, outcome == StepResult.Purged ? ActivityOp.Delete : ActivityOp.Upsert);
                    if (outcome == StepResult.Applied && change.Kind is "task_assignee" or "task")
                        assignmentTasks.Add(change.EntityKey.Split('|')[0]);
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

                    var outcome = await ApplyStepWithTextAsync((SqliteDataSession)session, connection, schema, change, payload, token);
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
                        if (outcome is StepResult.Applied or StepResult.Purged)
                            session.Activity.Note(change.Kind, change.EntityKey, outcome == StepResult.Purged ? ActivityOp.Delete : ActivityOp.Upsert);
                        if (outcome == StepResult.Applied && change.Kind is "task_assignee" or "task")
                            assignmentTasks.Add(change.EntityKey.Split('|')[0]);
                        parked.Remove(changeId);
                        Count(outcome, change.ChangeId, ref applied, ref purged, ref skipped, parked);
                        DeleteParked(connection, changeId);
                    }
                }

                if (throughSequence is { } sequence)
                {
                    if (streamId is null)
                    {
                        SyncSql.Execute(connection, $"UPDATE SyncState SET Cursor = MAX(Cursor, {sequence.ToString(CultureInfo.InvariantCulture)}) WHERE Id = 1;");
                    }
                    else
                    {
                        using var advance = connection.CreateCommand();
                        advance.CommandText = "UPDATE SyncStreams SET Cursor = MAX(Cursor, $sequence) WHERE StreamId = $stream AND State IN ('joining', 'active');";
                        advance.Parameters.AddWithValue("$sequence", sequence);
                        advance.Parameters.AddWithValue("$stream", streamId);
                        if (advance.ExecuteNonQuery() != 1)
                            throw new AppException(AppErrorKind.StorageUnavailable, "Cannot advance the cursor of a missing or evicting stream.");
                    }
                }

                ReceiveClocks(connection, changes.Where(entry => entry.Change is not null).Select(entry => entry.Change!));
            }
            finally
            {
                SyncSql.Execute(connection, "UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;");
            }

            foreach (var taskId in assignmentTasks)
            {
                var task = await session.Tasks.GetAsync(taskId, token);
                if (task is null || task.AssignMode != TaskAssignMode.Each || task.Status == TaskItemStatus.Cancelled) continue;
                var rows = await session.Tasks.ListAssigneesAsync(taskId, token);
                var completed = TaskAssignment.EachCompletion(rows.Select(row => new AssignmentCompletion(row.CompletedAtUtc, row.DeletedAtUtc)));
                if (completed is not { } at || (task.Status == TaskItemStatus.Done && task.CompletedAtUtc == at)) continue;
                var updated = task with { Status = TaskItemStatus.Done, CompletedAtUtc = at, CancelledAtUtc = null,
                    UpdatedAtUtc = clock.GetUtcNow(), Version = task.Version + 1 };
                if (await session.Tasks.UpdateAsync(updated, task.Version, token) is not null)
                    session.Activity.Note("task", taskId, ActivityOp.Upsert);
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

    private async Task<StepResult> ApplyStepWithTextAsync(SqliteDataSession session, SqliteConnection connection, SyncSchema schema,
        SyncChange change, string payload, CancellationToken ct)
    {
        var before = change.ActorId is not null && change.Op == SyncOp.Upsert ? ReadText(connection, change) : null;
        var result = await ApplyStepAsync(connection, schema, change, payload, ct);
        if (result == StepResult.Applied && change.ActorId is { } actor && ReadText(connection, change) is { } after
            && !string.Equals(before?.Text, after.Text, StringComparison.Ordinal))
            session.Notes.NoteRemoteText(new RemoteTextChange(change.ChangeId, change.Kind, change.EntityKey, after.SpaceId, actor, before?.Text, after.Text));
        return result;
    }

    private static (string SpaceId, string Text)? ReadText(SqliteConnection connection, SyncChange change)
    {
        var query = change.Kind switch
        {
            "task" => "SELECT SpaceId, Notes FROM Tasks WHERE Id=$id AND DeletedAtUtc IS NULL",
            "diary_entry" => "SELECT t.SpaceId, d.RawMarkdown FROM DiaryEntries d JOIN Tasks t ON t.Id=d.TaskId WHERE d.Id=$id AND d.DeletedAtUtc IS NULL",
            "page" => "SELECT SpaceId, RawMarkdown FROM Pages WHERE Id=$id AND DeletedAtUtc IS NULL",
            "pad" => "SELECT SpaceId, RawMarkdown FROM ScratchPadItems WHERE Id=$id AND DeletedAtUtc IS NULL",
            "task_assignee" => "SELECT t.SpaceId, a.MemberId FROM TaskAssignees a JOIN Tasks t ON t.Id=a.TaskId WHERE a.TaskId=$id AND a.MemberId=$member AND a.DeletedAtUtc IS NULL",
            _ => null,
        };
        if (query is null) return null;
        using var command = connection.CreateCommand();
        command.CommandText = query;
        var parts = change.EntityKey.Split('|');
        command.Parameters.AddWithValue("$id", parts[0]);
        if (change.Kind == "task_assignee")
        {
            if (parts.Length != 2) return null;
            command.Parameters.AddWithValue("$member", parts[1]);
        }
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)) : null;
    }

    private static StepResult ApplyMoved(SqliteConnection connection, SyncSchema schema, MovedChange moved)
    {
        using var access = connection.CreateCommand();
        access.CommandText = "SELECT count(*) FROM SyncStreams WHERE StreamId=$target AND Kind='space' AND State IN ('joining','active');";
        access.Parameters.AddWithValue("$target", moved.Target);
        var readable = Convert.ToInt64(access.ExecuteScalar()) == 1;
        if (!TablesByKind.TryGetValue(moved.Kind, out var table) || table.Mode == SyncMode.LocalOnly || SyncKinds.Find(moved.Kind) is not { } kind)
            throw new AppException(AppErrorKind.StorageUnavailable, "A moved item has an unknown kind.");
        var key = ParseKey(table, moved.EntityKey);
        if (!readable)
        {
            // §7.6: the work went to a space this device cannot read, so it leaves the device. Overlays stay for the server's tombstones.
            return SyncEviction.EvictEntities(connection, [(moved.Kind, moved.EntityKey)]) > 0 ? StepResult.Purged : StepResult.Skipped;
        }

        if (!kind.HasSpace || !RowExists(connection, table, key)) return StepResult.Skipped;
        if (!schema.HasColumn(table.Table, "SpaceId"))
            throw new AppException(AppErrorKind.StorageUnavailable, "A moved item has no space column.");
        using var update = connection.CreateCommand();
        var version = schema.HasColumn(table.Table, "Version") ? ", Version=Version+1" : string.Empty;
        update.CommandText = $"UPDATE {SyncSql.Quote(table.Table)} SET SpaceId=$target{version} WHERE {Where(table, key)} AND SpaceId<>$target;";
        BindKey(update, table, key);
        update.Parameters.AddWithValue("$target", moved.Target);
        return update.ExecuteNonQuery() == 0 ? StepResult.Skipped : StepResult.Applied;
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
