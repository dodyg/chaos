using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;

namespace Chaos.Application.History;

/// <summary>The task as it was at one point in its history, or Existed = false before it was created.</summary>
public sealed record TaskAsOf(bool Existed, TaskAggregateSnapshot? Snapshot, TaskHistoryRecord? Entry);

/// <summary>Task fields that can be brought back from history (PLAN §9: restoring is a new recorded mutation).</summary>
public enum RestorableTaskField
{
    Title = 0,
    Notes = 1,
    Priority = 2,
    Estimate = 3,
    Category = 4,
    Planned = 5,
    Due = 6,
}

public sealed record RestoreTaskFieldsCommand(
    string OperationId,
    string TaskId,
    long ExpectedVersion,
    string HistoryEntryId,
    IReadOnlyList<RestorableTaskField> Fields);

/// <summary>
/// The read-only history viewer and "restore this value" (PLAN §9). History is append-only;
/// each entry carries before/after aggregate snapshots, so the state as of any instant or
/// version is the after-snapshot of the newest entry at or before it.
/// </summary>
public sealed class TaskHistoryCommands(IApplicationStore store, TaskCommands tasks)
{
    /// <summary>All history of one task, oldest first, including events from before a deletion.</summary>
    public async Task<IReadOnlyList<TaskHistoryRecord>> ListAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            _ = await session.Tasks.GetIncludingDeletedAsync(taskId, token)
                ?? throw AppException.NotFound("No task with that ID exists on this computer.");
            return await session.History.ListByTaskAsync(taskId, token);
        }, ct);

    /// <summary>The task as of an instant: before its creation it did not exist; after deletion it shows as deleted.</summary>
    public async Task<TaskAsOf> AsOfAsync(string taskId, DateTimeOffset at, CancellationToken ct = default)
    {
        var history = await ListAsync(taskId, ct);
        var entry = history.LastOrDefault(candidate => candidate.EffectiveAtUtc <= at);
        return Read(entry);
    }

    /// <summary>The task right after one history entry (the viewer's version selection).</summary>
    public async Task<TaskAsOf> AtEntryAsync(string taskId, string historyEntryId, CancellationToken ct = default)
    {
        var history = await ListAsync(taskId, ct);
        var entry = history.SingleOrDefault(candidate => candidate.Id == historyEntryId)
            ?? throw AppException.NotFound("That history entry does not belong to this task.");
        return Read(entry);
    }

    /// <summary>
    /// Copies the chosen fields from a historical version onto the current task through the
    /// ordinary editor command: validation, version check, history and undo all apply.
    /// </summary>
    public async Task<TaskMutationResult> RestoreFieldsAsync(RestoreTaskFieldsCommand command, CancellationToken ct = default)
    {
        if (command.Fields.Count == 0)
        {
            throw AppException.Validation("fields", "Choose at least one value to restore.");
        }

        var version = await AtEntryAsync(command.TaskId, command.HistoryEntryId, ct);
        var old = version.Snapshot?.Task ?? throw AppException.Validation("historyEntryId", "That version has no saved task values.");
        var current = await tasks.GetAsync(command.TaskId, ct)
            ?? throw AppException.NotFound("That task no longer exists. Restore it before restoring earlier values.");
        if (current.Version != command.ExpectedVersion)
        {
            throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");
        }

        bool Has(RestorableTaskField field) => command.Fields.Contains(field);
        var source = new UpdateTaskDetailsCommand(
            command.OperationId,
            current.Id,
            command.ExpectedVersion,
            Has(RestorableTaskField.Title) ? old.Title : current.Title,
            Has(RestorableTaskField.Notes) ? old.Notes : current.Notes,
            Has(RestorableTaskField.Planned) ? old.PlannedDate : current.PlannedDate,
            Has(RestorableTaskField.Planned) ? old.PlannedTime : current.PlannedTime,
            Has(RestorableTaskField.Planned) ? old.PlannedZoneId : current.PlannedZoneId,
            Has(RestorableTaskField.Due) ? old.DueDate : current.DueDate,
            Has(RestorableTaskField.Due) ? old.DueTime : current.DueTime,
            Has(RestorableTaskField.Due) ? old.DueZoneId : current.DueZoneId,
            Has(RestorableTaskField.Priority) ? old.Priority : current.Priority,
            Has(RestorableTaskField.Estimate) ? old.EstimateMinutes : current.EstimateMinutes,
            Has(RestorableTaskField.Category) ? old.CapacityCategory : current.CapacityCategory);
        return await tasks.UpdateDetailsAsync(source, ct);
    }

    private static TaskAsOf Read(TaskHistoryRecord? entry)
    {
        if (entry is null)
        {
            return new TaskAsOf(Existed: false, Snapshot: null, Entry: null);
        }

        var snapshot = entry.SnapshotAfter is { } json ? StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(json) : null;
        return new TaskAsOf(Existed: true, snapshot, entry);
    }
}
