using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tasks;

public sealed record CaptureTaskCommand(string OperationId, string ContainerId, string Title, string? Notes);

public sealed record CaptureTaskResult(TaskRecord Task, string OperationId, bool Replayed);

public sealed record UpdateTaskDetailsCommand(
    string OperationId,
    string TaskId,
    long ExpectedVersion,
    string Title,
    string? Notes,
    DateOnly? PlannedDate,
    TimeOnly? PlannedTime,
    string? PlannedZoneId,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    string? DueZoneId,
    TaskPriority Priority,
    int? EstimateMinutes,
    CapacityCategory CapacityCategory);

public sealed record SetTaskStatusCommand(
    string OperationId,
    string TaskId,
    long ExpectedVersion,
    TaskItemStatus Status,
    OpenSubtaskChoice? OpenSubtaskChoice);

/// <summary>
/// The explicit choice PLAN §6 requires when completing a parent that still has open
/// subtasks: complete them together, or keep them open. Never a silent child change.
/// </summary>
public enum OpenSubtaskChoice
{
    CompleteAll = 0,
    KeepOpen = 1,
}

public sealed record MoveTaskCommand(string OperationId, string TaskId, long ExpectedVersion, string TargetContainerId, string? TargetSectionId);

public sealed record ReorderTaskCommand(string OperationId, string TaskId, long ExpectedVersion, int InsertionIndex);

public sealed record DeleteTaskCommand(string OperationId, string TaskId, long ExpectedVersion);

/// <summary>A task mutation result: the task as it is after the command, a replay flag and an optional overrideable warning.</summary>
public sealed record TaskMutationResult(TaskRecord Task, bool Replayed, string? Warning);

public sealed record TaskDetail(TaskRecord? Task, IReadOnlyList<SubtaskRecord> Subtasks);

/// <summary>
/// Task lifecycle commands (PLAN §15 WP2) over one store: capture (WP1), the full editor,
/// status/completion with terminal timestamps, container/section moves, manual reorder,
/// reversible deletion/restore, and history with before/after aggregate snapshots. Every
/// mutating command carries a client OperationId: the same operation replayed with the same
/// normalized payload returns the original result; different content is an operation conflict.
/// </summary>
public sealed class TaskCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public async Task<CaptureTaskResult> CaptureAsync(CaptureTaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = Normalize(command);

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, "task.capture", normalized, now, async inner =>
                {
                    var container = await ResolveContainerAsync(session, normalized.ContainerId, inner);
                    var position = await session.Tasks.GetNextPositionAsync(container.Id, inner);

                    var task = new TaskRecord
                    {
                        Id = ids.NewId().ToString(),
                        SpaceId = container.SpaceId,
                        ContainerId = container.Id,
                        Title = normalized.Title,
                        Notes = normalized.Notes ?? string.Empty,
                        Status = TaskItemStatus.NotStarted,
                        Priority = TaskPriority.Normal,
                        Position = position,
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Tasks.AddAsync(task, inner);

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, normalized.OperationId,
                        before: null,
                        after: new TaskAggregateSnapshot(task, []),
                        eventKind: "task.created",
                        changedFields: ["Title", "Notes", "ContainerId"],
                        now, inner);

                    return new CaptureTaskResult(task, normalized.OperationId, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync((session, token) => session.Tasks.GetAsync(taskId, token), ct);

    /// <summary>The task and its active subtasks, or a null task when the ID is unknown.</summary>
    public async Task<TaskDetail> GetDetailAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var task = await session.Tasks.GetAsync(taskId, token);
            return task is null
                ? new TaskDetail(null, [])
                : new TaskDetail(task, await session.Subtasks.ListByTaskAsync(taskId, token));
        }, ct);

    /// <summary>
    /// Targets of internal links (PLAN §9): links hold stable IDs, so a renamed task resolves
    /// to its new title and a deleted one reports Deleted for the recoverable broken-link state.
    /// Unknown IDs are omitted.
    /// </summary>
    public async Task<IReadOnlyList<TaskRecord>> ResolveLinksAsync(IReadOnlyList<string> taskIds, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<TaskRecord>>(async (session, token) =>
        {
            var found = new List<TaskRecord>();
            foreach (var id in taskIds.Distinct(StringComparer.Ordinal).Take(200))
            {
                if (await session.Tasks.GetIncludingDeletedAsync(id, token) is { } task)
                {
                    found.Add(task);
                }
            }

            return found;
        }, ct);

    /// <summary>Active top-level tasks of one container, ordered by (Position, Id).</summary>
    public async Task<IReadOnlyList<TaskRecord>> ListAsync(string containerId, CancellationToken ct = default) =>
        await store.QueryAsync((session, token) => session.Tasks.ListByContainerAsync(containerId, token), ct);

    public async Task<IReadOnlyList<TaskRecord>> ListDeletedAsync(string? containerId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var container = await ResolveContainerAsync(session, containerId, token);
            return await session.Tasks.ListDeletedByContainerAsync(container.Id, token);
        }, ct);

    public async Task<IReadOnlyList<TaskRecord>> ListInboxAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var inbox = await ResolveContainerAsync(session, string.Empty, token);
            return await session.Tasks.ListByContainerAsync(inbox.Id, token);
        }, ct);

    public async Task<TaskMutationResult> UpdateDetailsAsync(UpdateTaskDetailsCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = Normalize(command);

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, "task.update-details", normalized, now, async inner =>
                {
                    var task = await RequireTaskAsync(session, normalized.TaskId, inner);
                    RequireCurrentVersion(task, normalized.ExpectedVersion);
                    var link = await session.GitHub.FindLinkByTaskAsync(task.Id, inner);
                    if (link is { State: not 4 })
                    {
                        if (normalized.Title.Length > 256) throw AppException.Validation("title", "A GitHub issue title can hold at most 256 characters.");
                        var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson)
                            ?? throw AppException.Validation("issue", "The GitHub issue baseline could not be read.");
                        if (baseline.MilestoneDue is not null && (normalized.DueDate != task.DueDate || normalized.DueTime != task.DueTime || normalized.DueZoneId != task.DueZoneId))
                            throw AppException.Validation("dueDate", "Change the milestone due date on GitHub.");
                    }
                    var subtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                    var before = new TaskAggregateSnapshot(task, subtasks);

                    var updated = task with
                    {
                        Title = normalized.Title,
                        Notes = normalized.Notes ?? string.Empty,
                        PlannedDate = normalized.PlannedDate,
                        PlannedTime = normalized.PlannedTime,
                        PlannedZoneId = normalized.PlannedZoneId,
                        DueDate = normalized.DueDate,
                        DueTime = normalized.DueTime,
                        DueZoneId = normalized.DueZoneId,
                        Priority = normalized.Priority,
                        EstimateMinutes = normalized.EstimateMinutes,
                        CapacityCategory = normalized.CapacityCategory,
                        Version = task.Version + 1,
                        UpdatedAtUtc = now,
                    };
                    var applied = await ApplyTaskUpdateAsync(session, updated, task.Version, token);

                    if (applied.RecurrenceOccurrenceKey is not null)
                    {
                        var occurrence = await session.Recurrence.FindByTaskAsync(applied.Id, inner)
                            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence occurrence could not be found.");
                        var snapshot = await Chaos.Application.History.RecordingDataSession.BuildAggregateAsync(session, applied.Id, inner)
                            ?? throw AppException.NotFound("That task no longer exists.");
                        var edit = occurrence with { EditSnapshotJson = StoredJsonFormats.Snapshot(snapshot),
                            Version = occurrence.Version + 1, UpdatedAtUtc = now };
                        if (await session.Recurrence.UpdateOccurrenceAsync(edit, occurrence.Version, inner) is null)
                            throw AppException.VersionConflict("The recurrence changed elsewhere.");
                    }

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, normalized.OperationId, before,
                        new TaskAggregateSnapshot(applied, subtasks),
                        "task.updated",
                        TaskMutationSupport.ChangedTaskFields(task, applied),
                        now, inner);

                    return new TaskMutationResult(applied, Replayed: false, PlannedAfterDueWarning(normalized));
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<TaskMutationResult> SetStatusAsync(SetTaskStatusCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Status = TaskFieldValidation.Status(command.Status, "status") };

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, "task.set-status", normalized, now, async inner =>
                {
                    var task = await RequireTaskAsync(session, normalized.TaskId, inner);
                    RequireCurrentVersion(task, normalized.ExpectedVersion);
                    if (task.Status == normalized.Status)
                    {
                        return new TaskMutationResult(task, Replayed: false, Warning: null); // nothing to change
                    }

                    if (task.RecurrenceOccurrenceKey is not null && (task.IsTerminal || normalized.Status == TaskItemStatus.Cancelled))
                        throw AppException.Validation("status", "Use the recurrence controls to reconcile or end this series.");

                    var subtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                    var before = new TaskAggregateSnapshot(task, subtasks);

                    // Reopening restores the previous nonterminal status when history knows it (PLAN §6).
                    var targetStatus = TaskItemStatusRules.IsTerminal(task.Status) && !TaskItemStatusRules.IsTerminal(normalized.Status)
                        ? await TaskMutationSupport.PreviousNonterminalStatusAsync(session, task.Id, subtaskId: null, inner)
                        : normalized.Status;

                    if (targetStatus == TaskItemStatus.Done && !TaskItemStatusRules.IsTerminal(task.Status))
                    {
                        await ApplyOpenSubtaskChoiceAsync(session, subtasks, normalized.OpenSubtaskChoice, now, inner);
                        subtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                    }

                    var updated = task with
                    {
                        Status = targetStatus,
                        CompletedAtUtc = targetStatus == TaskItemStatus.Done ? now : null,
                        CancelledAtUtc = targetStatus == TaskItemStatus.Cancelled ? now : null,
                        Version = task.Version + 1,
                        UpdatedAtUtc = now,
                    };
                    var applied = await ApplyTaskUpdateAsync(session, updated, task.Version, token);

                    if (applied.RecurrenceOccurrenceKey is not null && targetStatus == TaskItemStatus.Done)
                        await RecurrenceCommands.AdvanceAsync(session, ids, applied, now, inner);

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, normalized.OperationId, before,
                        new TaskAggregateSnapshot(applied, subtasks),
                        EventKindForStatusChange(task.Status, applied.Status),
                        ["Status"],
                        now, inner);

                    return new TaskMutationResult(applied, Replayed: false, Warning: null);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<TaskMutationResult> MoveAsync(MoveTaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "task.move", command, now, async inner =>
                {
                    var task = await RequireTaskAsync(session, command.TaskId, inner);
                    RequireCurrentVersion(task, command.ExpectedVersion);

                    var target = await session.Containers.GetAsync(command.TargetContainerId, inner)
                        ?? throw AppException.NotFound("That list or project no longer exists. Refresh and try again.");
                    if (target.SpaceId != task.SpaceId)
                    {
                        throw AppException.Validation("containerId", "That list or project belongs to a different space. Choose one from this space.");
                    }

                    SectionRecord? section = null;
                    if (command.TargetSectionId is { } sectionId)
                    {
                        section = await session.Sections.GetAsync(sectionId, inner)
                            ?? throw AppException.NotFound("That section no longer exists. Refresh and try again.");
                        if (section.ContainerId != target.Id)
                        {
                            throw AppException.Validation("sectionId", "That section belongs to a different list or project. Pick a section inside the target.");
                        }
                    }

                    var sameSection = section?.Id == task.SectionId;
                    if (target.Id == task.ContainerId && sameSection)
                    {
                        return new TaskMutationResult(task, Replayed: false, Warning: null); // already there
                    }

                    var updated = task with
                    {
                        ContainerId = target.Id,
                        SectionId = section?.Id,
                        Version = task.Version + 1,
                        UpdatedAtUtc = now,
                    };
                    var applied = await ApplyTaskUpdateAsync(session, updated, task.Version, token);

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, command.OperationId,
                        new TaskAggregateSnapshot(task, await session.Subtasks.ListByTaskAsync(task.Id, inner)),
                        new TaskAggregateSnapshot(applied, await session.Subtasks.ListByTaskAsync(task.Id, inner)),
                        "task.moved",
                        TaskMutationSupport.ChangedTaskFields(task, applied),
                        now, inner);

                    return new TaskMutationResult(applied, Replayed: false, Warning: null);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<TaskMutationResult> ReorderAsync(ReorderTaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "task.reorder", command, now, async inner =>
                {
                    var task = await RequireTaskAsync(session, command.TaskId, inner);
                    RequireCurrentVersion(task, command.ExpectedVersion);
                    var siblings = (await session.Tasks.ListByContainerAsync(task.ContainerId, inner))
                        .Where(row => row.SectionId == task.SectionId).ToList();

                    var assignments = RankOrdering.Place(
                        [.. siblings.Select(row => (row.Id, row.Position, row.Version))],
                        task.Id,
                        command.InsertionIndex);
                    if (assignments.Count == 0)
                    {
                        return new TaskMutationResult(task, Replayed: false, Warning: null); // already in place
                    }

                    TaskRecord moved = task;
                    foreach (var assignment in assignments)
                    {
                        var row = siblings.Single(candidate => candidate.Id == assignment.Id);
                        var updated = row with
                        {
                            Position = assignment.NewPosition,
                            Version = row.Version + 1,
                            UpdatedAtUtc = now,
                        };
                        var applied = await ApplyTaskUpdateAsync(session, updated, row.Version, token);
                        if (applied.Id == task.Id)
                        {
                            moved = applied;
                        }

                        await TaskMutationSupport.WriteTaskHistoryAsync(
                            session, ids, command.OperationId,
                            new TaskAggregateSnapshot(row, await session.Subtasks.ListByTaskAsync(row.Id, inner)),
                            new TaskAggregateSnapshot(applied, await session.Subtasks.ListByTaskAsync(row.Id, inner)),
                            "task.reordered",
                            ["Position"],
                            now, inner);
                    }

                    return new TaskMutationResult(moved, Replayed: false, Warning: null);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    /// <summary>Reversible deletion (PLAN §1 decision 10): the parent and its subtasks are tombstoned together.</summary>
    public async Task<TaskMutationResult> DeleteAsync(DeleteTaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "task.delete", command, now, async inner =>
                {
                    var task = await RequireTaskAsync(session, command.TaskId, inner);
                    RequireCurrentVersion(task, command.ExpectedVersion);
                    if (task.RecurrenceOccurrenceKey is not null)
                    {
                        var occurrence = await session.Recurrence.FindByTaskAsync(task.Id, inner)
                            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence occurrence could not be found.");
                        var series = await session.Recurrence.GetSeriesAsync(occurrence.SeriesId, inner)
                            ?? throw new AppException(AppErrorKind.StorageUnavailable, "The recurrence series could not be found.");
                        if (series.EndedAtUtc is null)
                            throw AppException.Validation("taskId", "Choose whether to skip this occurrence or end the series.");
                    }
                    var subtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                    var before = new TaskAggregateSnapshot(task, subtasks);

                    var tombstonedSubtasks = new List<SubtaskRecord>(subtasks.Count);
                    foreach (var subtask in subtasks)
                    {
                        var updated = subtask with { DeletedAtUtc = now, Version = subtask.Version + 1, UpdatedAtUtc = now };
                        tombstonedSubtasks.Add(await ApplySubtaskUpdateAsync(session, updated, subtask.Version, token));
                    }

                    var deleted = task with { DeletedAtUtc = now, Version = task.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplyTaskUpdateAsync(session, deleted, task.Version, token);

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, command.OperationId, before,
                        new TaskAggregateSnapshot(applied, tombstonedSubtasks),
                        "task.deleted",
                        ["DeletedAtUtc"],
                        now, inner);

                    return new TaskMutationResult(applied, Replayed: false, Warning: null);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    /// <summary>
    /// Restores a tombstoned task and the subtasks recorded as active immediately before
    /// its deletion; separately deleted subtasks remain deleted even when clocks coincide.
    /// </summary>
    public async Task<TaskMutationResult> RestoreAsync(DeleteTaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "task.restore", command, now, async inner =>
                {
                    var task = await session.Tasks.GetIncludingDeletedAsync(command.TaskId, inner)
                        ?? throw AppException.NotFound("No task with that ID exists on this computer.");
                    RequireCurrentVersion(task, command.ExpectedVersion);
                    if (task.DeletedAtUtc is null)
                    {
                        return new TaskMutationResult(task, Replayed: false, Warning: null); // already active
                    }

                    _ = await session.Containers.GetAsync(task.ContainerId, inner) ?? throw AppException.Validation("containerId", "Restore the task's list or project first.");
                    var allSubtasks = await session.Subtasks.ListByTaskIncludingDeletedAsync(task.Id, inner);
                    var before = new TaskAggregateSnapshot(task, allSubtasks.Where(subtask => subtask.DeletedAtUtc is null).ToList());
                    var history = await session.History.ListByTaskAsync(task.Id, inner);
                    // The entry that produced this tombstoned version: a delete, a container
                    // deletion with contents, or an undo of a restore. Its before-snapshot names
                    // the subtasks that were active right before.
                    var snapshot = history
                        .Where(entry => entry.AggregateVersion == task.Version && entry.SnapshotBefore is not null)
                        .Select(entry => StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(entry.SnapshotBefore!))
                        .LastOrDefault(candidate => candidate is not null && candidate.Task.DeletedAtUtc is null);
                    var childIds = snapshot?.Subtasks.Where(child => child.DeletedAtUtc is null).Select(child => child.Id).ToHashSet(StringComparer.Ordinal) ?? [];

                    var restoredSubtasks = new List<SubtaskRecord>();
                    foreach (var subtask in allSubtasks)
                    {
                        if (subtask.DeletedAtUtc is null || !childIds.Contains(subtask.Id))
                        {
                            continue;
                        }

                        var updated = subtask with { DeletedAtUtc = null, Version = subtask.Version + 1, UpdatedAtUtc = now };
                        restoredSubtasks.Add(await ApplySubtaskUpdateAsync(session, updated, subtask.Version, token));
                    }

                    var restored = task with { DeletedAtUtc = null, Version = task.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplyTaskUpdateAsync(session, restored, task.Version, token);
                    if (task.RecurrenceOccurrenceKey is { } occurrenceId && await session.Recurrence.GetOccurrenceAsync(occurrenceId, inner) is { DeletedAtUtc: not null } occurrence)
                    {
                        var restoredOccurrence = occurrence with { DeletedAtUtc = null, Version = occurrence.Version + 1, UpdatedAtUtc = now };
                        if (await session.Recurrence.UpdateOccurrenceAsync(restoredOccurrence, occurrence.Version, inner) is null)
                            throw AppException.VersionConflict("The recurrence changed elsewhere.");
                    }

                    await TaskMutationSupport.WriteTaskHistoryAsync(
                        session, ids, command.OperationId, before,
                        new TaskAggregateSnapshot(applied, restoredSubtasks),
                        "task.restored",
                        ["DeletedAtUtc"],
                        now, inner);

                    return new TaskMutationResult(applied, Replayed: false, Warning: null);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    /// <summary>Trims and limit-checks the title; the payload hash covers the normalized text, so whitespace-only retries replay.</summary>
    internal static CaptureTaskCommand Normalize(CaptureTaskCommand command)
    {
        string title;
        try
        {
            title = TaskTitle.Normalize(command.Title);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation("title", error.Message);
        }

        return command with { Title = title };
    }

    /// <summary>Field-level validation of the full editor payload (PLAN §6 limits and anchor rules).</summary>
    internal static UpdateTaskDetailsCommand Normalize(UpdateTaskDetailsCommand command)
    {
        var planned = TaskFieldValidation.Anchor(
            command.PlannedDate, command.PlannedTime, command.PlannedZoneId,
            "plannedDate", "plannedTime", "plannedZoneId");
        var due = TaskFieldValidation.Anchor(
            command.DueDate, command.DueTime, command.DueZoneId,
            "dueDate", "dueTime", "dueZoneId");

        return command with
        {
            Title = TaskFieldValidation.Title(command.Title, "title"),
            Notes = TaskFieldValidation.Notes(command.Notes, "notes"),
            PlannedDate = planned.Date,
            PlannedTime = planned.Time,
            PlannedZoneId = planned.ZoneId,
            DueDate = due.Date,
            DueTime = due.Time,
            DueZoneId = due.ZoneId,
            Priority = TaskFieldValidation.Priority(command.Priority, "priority"),
            EstimateMinutes = TaskFieldValidation.Estimate(command.EstimateMinutes, "estimateMinutes"),
            CapacityCategory = TaskFieldValidation.Category(command.CapacityCategory, "capacityCategory"),
        };
    }

    /// <summary>A planned date after a due date warns but stays as entered (PLAN §5: never silently swap).</summary>
    internal static string? PlannedAfterDueWarning(UpdateTaskDetailsCommand normalized) =>
        normalized.PlannedDate is { } planned && normalized.DueDate is { } due && planned > due
            ? "The planned date is after the due date. Both values are kept as entered."
            : null;

    private async Task<T> InWriteAsync<T>(
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body,
        CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);

    private static async Task<TaskRecord> RequireTaskAsync(IDataSession session, string taskId, CancellationToken token) =>
        await session.Tasks.GetAsync(taskId, token)
        ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");

    private static void RequireCurrentVersion(TaskRecord task, long expectedVersion)
    {
        if (task.Version != expectedVersion)
        {
            throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");
        }
    }

    private static async Task<TaskRecord> ApplyTaskUpdateAsync(IDataSession session, TaskRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Tasks.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");

    private static async Task<SubtaskRecord> ApplySubtaskUpdateAsync(IDataSession session, SubtaskRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Subtasks.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");

    private static async Task ApplyOpenSubtaskChoiceAsync(
        IDataSession session,
        IReadOnlyList<SubtaskRecord> subtasks,
        OpenSubtaskChoice? choice,
        DateTimeOffset now,
        CancellationToken token)
    {
        var open = subtasks.Where(subtask => !subtask.IsTerminal).ToList();
        if (open.Count == 0)
        {
            return;
        }

        if (choice is null)
        {
            throw AppException.Validation(
                "openSubtaskChoice",
                "This task still has open subtasks. Choose \"completeAll\" to complete them together, or \"keepOpen\" to complete only this task.");
        }

        if (!Enum.IsDefined(choice.Value))
        {
            throw AppException.Validation("openSubtaskChoice", "Choose whether to complete open subtasks or keep them open.");
        }

        if (choice == OpenSubtaskChoice.KeepOpen)
        {
            return;
        }

        foreach (var subtask in open)
        {
            var updated = subtask with
            {
                Status = TaskItemStatus.Done,
                CompletedAtUtc = now,
                CancelledAtUtc = null,
                Version = subtask.Version + 1,
                UpdatedAtUtc = now,
            };
            await ApplySubtaskUpdateAsync(session, updated, subtask.Version, token);
        }
    }

    private static string EventKindForStatusChange(TaskItemStatus before, TaskItemStatus after) => after switch
    {
        TaskItemStatus.Done => "task.completed",
        TaskItemStatus.Cancelled => "task.cancelled",
        _ when TaskItemStatusRules.IsTerminal(before) => "task.reopened",
        _ => "task.status-changed",
    };

    private static async Task<ProfileRecord> RequirePrimaryProfile(IDataSession session, CancellationToken token) =>
        await session.Profiles.GetPrimaryAsync(token)
        ?? throw new AppException(
            AppErrorKind.StorageUnavailable,
            "No local profile exists yet. Restart Chaos to complete first-run setup.");

    private static async Task<ContainerRecord> ResolveContainerAsync(IDataSession session, string? containerId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            var profile = await RequirePrimaryProfile(session, token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No personal space exists yet. Restart Chaos to complete first-run setup.");
            return await session.Containers.GetInboxAsync(space.Id, token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No Inbox exists yet. Restart Chaos to complete first-run setup.");
        }

        return await session.Containers.GetAsync(containerId, token)
            ?? throw AppException.NotFound("That list or project no longer exists. Refresh and try again.");
    }

    /// <summary>SHA-256 lowercase hex over canonical camelCase JSON of the normalized command.</summary>
    internal static string CanonicalHash(CaptureTaskCommand normalizedCommand) => StoredJsonFormats.Hash(normalizedCommand);
}
