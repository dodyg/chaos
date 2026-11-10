using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tasks;

/// <summary>The task aggregate as one versioned snapshot: task row plus its subtasks (PLAN §9).</summary>
public sealed record TaskAggregateSnapshot(TaskRecord Task, IReadOnlyList<SubtaskRecord> Subtasks);

/// <summary>Envelope for stored operation results; the replay flag is composed at the caller, never stored.</summary>
public sealed record StoredResultEnvelope<T>(T Result);

/// <summary>
/// Plumbing shared by the task-lifecycle commands (PLAN §4/§5): every mutation runs in one
/// write transaction, replays an OperationId with the same normalized payload, conflicts on a
/// reused OperationId with different content, and appends a history entry with before/after
/// aggregate snapshots in the same transaction.
/// </summary>
internal static class TaskMutationSupport
{
    /// <summary>
    /// One write transaction with the primary profile and current time bound in. Throwing
    /// inside rolls the whole transaction back (the store's contract).
    /// </summary>
    public static async Task<T> InWriteTransactionAsync<T>(
        IApplicationStore store,
        TimeProvider clock,
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body,
        CancellationToken ct) => await store.ExecuteAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No local profile exists yet. Restart Chaos to complete first-run setup.");
            var now = clock.GetUtcNow();
            return await body(session, profile, now, token);
        }, ct);

    /// <summary>
    /// Same OperationId with the same normalized payload replays the stored result; the same
    /// OperationId with different content is an operation conflict (PLAN §5 OperationReceipts).
    /// The replay flag is returned beside the result so callers can compose it.
    /// </summary>
    public static async Task<(T Result, bool Replayed)> ReplayOrRunAsync<T>(
        IDataSession session,
        string profileId,
        string operationId,
        string commandKind,
        object normalizedPayload,
        DateTimeOffset now,
        Func<CancellationToken, Task<T>> run,
        CancellationToken token)
    {
        var payloadHash = StoredJsonFormats.Hash(normalizedPayload);
        var receipt = await session.Receipts.FindAsync(profileId, operationId, token);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.CommandKind, commandKind, StringComparison.Ordinal))
            {
                throw AppException.OperationConflict(
                    "This operation ID was already used for a different kind of change. Use a new operation ID.");
            }

            if (!string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                throw AppException.OperationConflict(
                    "This operation was already used with different content. Retry the same change; for new content use a new operation.");
            }

            // Package 1 stored capture receipts directly as a versioned TaskRecord.
            // Preserve retries across the upgrade to the lifecycle result envelope.
            if (commandKind == "task.capture" && typeof(T) == typeof(CaptureTaskResult))
            {
                using var document = System.Text.Json.JsonDocument.Parse(receipt.ResultJson);
                if (document.RootElement.TryGetProperty("payload", out var payload)
                    && !payload.TryGetProperty("result", out _))
                {
                    var task = StoredJsonFormats.ReadSnapshot<TaskRecord>(receipt.ResultJson)
                        ?? throw new AppException(AppErrorKind.StorageUnavailable, "The stored result of this operation could not be read.");
                    return ((T)(object)new CaptureTaskResult(task, operationId, Replayed: true), true);
                }
            }

            var stored = StoredJsonFormats.ReadSnapshot<StoredResultEnvelope<T>>(receipt.ResultJson)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "The stored result of this operation could not be read. Refresh and try the change again.");
            return (stored.Result, true);
        }

        var result = await run(token);
        await session.Receipts.AddAsync(new OperationReceiptRecord
        {
            ProfileId = profileId,
            OperationId = operationId,
            CommandKind = commandKind,
            PayloadHash = payloadHash,
            ResultJson = StoredJsonFormats.Snapshot(new StoredResultEnvelope<T>(result)),
            CommittedAtUtc = now,
        }, token);
        return (result, false);
    }

    /// <summary>Appends one history entry with versioned before/after aggregate snapshots.</summary>
    public static async Task WriteTaskHistoryAsync(
        IDataSession session,
        IIdGenerator ids,
        string operationId,
        TaskAggregateSnapshot? before,
        TaskAggregateSnapshot after,
        string eventKind,
        IReadOnlyList<string> changedFields,
        DateTimeOffset now,
        CancellationToken token) => await session.History.AddAsync(new TaskHistoryRecord
        {
            Id = ids.NewId().ToString(),
            TaskId = after.Task.Id,
            OperationId = operationId,
            EffectiveAtUtc = now,
            Actor = "local",
            EventKind = eventKind,
            ChangedFields = changedFields,
            SnapshotBefore = before is null ? null : StoredJsonFormats.Snapshot(before),
            SnapshotAfter = StoredJsonFormats.Snapshot(after),
            AggregateVersion = after.Task.Version,
            RecordedAtUtc = now,
        }, token);

    /// <summary>Names of the task fields whose value actually changed between two versions.</summary>
    public static IReadOnlyList<string> ChangedTaskFields(TaskRecord before, TaskRecord after)
    {
        var changed = new List<string>(8);
        void When<TValue>(string name, TValue a, TValue b)
        {
            if (!EqualityComparer<TValue>.Default.Equals(a, b))
            {
                changed.Add(name);
            }
        }

        When("ContainerId", before.ContainerId, after.ContainerId);
        When("SectionId", before.SectionId, after.SectionId);
        When("Title", before.Title, after.Title);
        When("Notes", before.Notes, after.Notes);
        When("Status", before.Status, after.Status);
        When("Priority", before.Priority, after.Priority);
        When("EstimateMinutes", before.EstimateMinutes, after.EstimateMinutes);
        When("CapacityCategory", before.CapacityCategory, after.CapacityCategory);
        When("PlannedDate", before.PlannedDate, after.PlannedDate);
        When("PlannedTime", before.PlannedTime, after.PlannedTime);
        When("PlannedZoneId", before.PlannedZoneId, after.PlannedZoneId);
        When("DueDate", before.DueDate, after.DueDate);
        When("DueTime", before.DueTime, after.DueTime);
        When("DueZoneId", before.DueZoneId, after.DueZoneId);
        When("CompletedAtUtc", before.CompletedAtUtc, after.CompletedAtUtc);
        When("CancelledAtUtc", before.CancelledAtUtc, after.CancelledAtUtc);
        When("Position", before.Position, after.Position);
        When("DeletedAtUtc", before.DeletedAtUtc, after.DeletedAtUtc);
        return changed;
    }

    /// <summary>
    /// The nonterminal status to restore when reopening (PLAN §6): the status the task (or
    /// subtask) held right before it became terminal, read from the newest history entry
    /// whose before-snapshot shows a nonterminal state; Not started when history cannot say.
    /// </summary>
    public static async Task<TaskItemStatus> PreviousNonterminalStatusAsync(
        IDataSession session,
        string taskId,
        string? subtaskId,
        CancellationToken token)
    {
        var history = await session.History.ListByTaskAsync(taskId, token);
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var entry = history[index];
            if (entry.SnapshotBefore is null)
            {
                continue;
            }

            var before = StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(entry.SnapshotBefore);
            if (before is null)
            {
                continue; // unreadable older envelope; keep scanning
            }

            var status = subtaskId is null
                ? before.Task.Status
                : before.Subtasks.FirstOrDefault(subtask => subtask.Id == subtaskId)?.Status;
            if (status is { } candidate && !TaskItemStatusRules.IsTerminal(candidate))
            {
                return candidate;
            }
        }

        return TaskItemStatus.NotStarted;
    }
}
