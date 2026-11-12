using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tasks;

public sealed record AddSubtaskCommand(string OperationId, string TaskId, string Title, string? Notes);

public sealed record UpdateSubtaskCommand(string OperationId, string SubtaskId, long ExpectedVersion, string Title, string? Notes);

public sealed record SetSubtaskStatusCommand(string OperationId, string SubtaskId, long ExpectedVersion, TaskItemStatus Status);

public sealed record ReorderSubtaskCommand(string OperationId, string SubtaskId, long ExpectedVersion, int InsertionIndex);
public sealed record RestoreSubtaskCommand(string OperationId, string TaskId, string SubtaskId, long ExpectedVersion);

public sealed record SubtaskMutationResult(SubtaskRecord Subtask, bool Replayed);

/// <summary>
/// Subtask CRUD, status and manual order (PLAN §15 WP2). Subtasks carry their own status and
/// version; the parent row is not rewritten, but every mutation appends one task-history entry
/// whose aggregate snapshots include the changed subtasks (PLAN §9).
/// </summary>
public sealed class SubtaskCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public async Task<IReadOnlyList<SubtaskRecord>> ListDeletedAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            _ = await RequireTaskAsync(session, taskId, token);
            return (await session.Subtasks.ListByTaskIncludingDeletedAsync(taskId, token)).Where(x => x.DeletedAtUtc is not null).ToList();
        }, ct);

    public async Task<SubtaskMutationResult> RestoreAsync(RestoreSubtaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "subtask.restore", command, now, async inner =>
            {
                var task = await RequireTaskAsync(session, command.TaskId, inner);
                var all = await session.Subtasks.ListByTaskIncludingDeletedAsync(task.Id, inner);
                var subtask = all.SingleOrDefault(x => x.Id == command.SubtaskId) ?? throw AppException.NotFound("That subtask no longer exists.");
                RequireCurrentVersion(subtask, command.ExpectedVersion);
                if (subtask.DeletedAtUtc is null) return new SubtaskMutationResult(subtask, false);
                await ValidateGitHubTitleAsync(session, task.Id, subtask.Title, inner);
                var before = new TaskAggregateSnapshot(task, all);
                var restored = subtask with { DeletedAtUtc = null, Version = subtask.Version + 1, UpdatedAtUtc = now };
                var applied = await ApplySubtaskUpdateAsync(session, restored, subtask.Version, inner);
                await WriteHistoryAsync(session, command.OperationId, task.Id, before, "subtask.restored", now, inner);
                return new SubtaskMutationResult(applied, false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }
    public async Task<SubtaskMutationResult> AddAsync(AddSubtaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "subtask.add", command, now, async inner =>
                {
                    var title = TaskFieldValidation.Title(command.Title, "title");
                    var notes = TaskFieldValidation.Notes(command.Notes, "notes");
                    var task = await RequireTaskAsync(session, command.TaskId, inner);
                    await ValidateGitHubTitleAsync(session, task.Id, title, inner);
                    var beforeSubtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                    var position = await session.Subtasks.GetNextPositionAsync(task.Id, inner);

                    var subtask = new SubtaskRecord
                    {
                        Id = ids.NewId().ToString(),
                        TaskId = task.Id,
                        Title = title,
                        Notes = notes,
                        Status = TaskItemStatus.NotStarted,
                        Position = position,
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Subtasks.AddAsync(subtask, inner);

                    await WriteHistoryAsync(
                        session, command.OperationId, task.Id,
                        new TaskAggregateSnapshot(task, beforeSubtasks),
                        "subtask.added", now, inner);

                    return new SubtaskMutationResult(subtask, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<SubtaskMutationResult> UpdateAsync(UpdateSubtaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "subtask.update", command, now, async inner =>
                {
                    var subtask = await RequireSubtaskAsync(session, command.SubtaskId, inner);
                    RequireCurrentVersion(subtask, command.ExpectedVersion);
                    var before = new TaskAggregateSnapshot(await RequireTaskAsync(session, subtask.TaskId, inner), await session.Subtasks.ListByTaskAsync(subtask.TaskId, inner));
                    var title = TaskFieldValidation.Title(command.Title, "title");
                    var notes = TaskFieldValidation.Notes(command.Notes, "notes");
                    await ValidateGitHubTitleAsync(session, subtask.TaskId, title, inner);

                    var updated = subtask with { Title = title, Notes = notes, Version = subtask.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplySubtaskUpdateAsync(session, updated, subtask.Version, token);
                    await WriteHistoryAsync(session, command.OperationId, subtask.TaskId, before, "subtask.updated", now, inner);

                    return new SubtaskMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    private static async Task ValidateGitHubTitleAsync(IDataSession session, string taskId, string title, CancellationToken ct)
    {
        if ((title.Contains('\r') || title.Contains('\n')) && await session.GitHub.FindLinkByTaskAsync(taskId, ct) is { State: not 4 })
            throw AppException.Validation("title", "A GitHub checklist item must fit on one line.");
    }

    public async Task<SubtaskMutationResult> SetStatusAsync(SetSubtaskStatusCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Status = TaskFieldValidation.Status(command.Status, "status") };

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, "subtask.set-status", normalized, now, async inner =>
                {
                    var subtask = await RequireSubtaskAsync(session, normalized.SubtaskId, inner);
                    RequireCurrentVersion(subtask, normalized.ExpectedVersion);
                    if (subtask.Status == normalized.Status)
                    {
                        return new SubtaskMutationResult(subtask, Replayed: false); // nothing to change
                    }

                    var before = new TaskAggregateSnapshot(await RequireTaskAsync(session, subtask.TaskId, inner), await session.Subtasks.ListByTaskAsync(subtask.TaskId, inner));

                    // Reopening restores the previous nonterminal status when history knows it (PLAN §6).
                    var targetStatus = TaskItemStatusRules.IsTerminal(subtask.Status) && !TaskItemStatusRules.IsTerminal(normalized.Status)
                        ? await TaskMutationSupport.PreviousNonterminalStatusAsync(session, subtask.TaskId, subtask.Id, inner)
                        : normalized.Status;

                    var updated = subtask with
                    {
                        Status = targetStatus,
                        CompletedAtUtc = targetStatus == TaskItemStatus.Done ? now : null,
                        CancelledAtUtc = targetStatus == TaskItemStatus.Cancelled ? now : null,
                        Version = subtask.Version + 1,
                        UpdatedAtUtc = now,
                    };
                    var applied = await ApplySubtaskUpdateAsync(session, updated, subtask.Version, token);
                    await WriteHistoryAsync(session, normalized.OperationId, subtask.TaskId, before, EventKind(subtask.Status, applied.Status), now, inner);

                    return new SubtaskMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<SubtaskMutationResult> DeleteAsync(UpdateSubtaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "subtask.delete", command, now, async inner =>
                {
                    var subtask = await RequireSubtaskAsync(session, command.SubtaskId, inner);
                    RequireCurrentVersion(subtask, command.ExpectedVersion);

                    var before = new TaskAggregateSnapshot(await RequireTaskAsync(session, subtask.TaskId, inner), await session.Subtasks.ListByTaskAsync(subtask.TaskId, inner));

                    var deleted = subtask with { DeletedAtUtc = now, Version = subtask.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplySubtaskUpdateAsync(session, deleted, subtask.Version, token);
                    await WriteHistoryAsync(session, command.OperationId, subtask.TaskId, before, "subtask.deleted", now, inner);

                    return new SubtaskMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<SubtaskMutationResult> ReorderAsync(ReorderSubtaskCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "subtask.reorder", command, now, async inner =>
                {
                    var subtask = await RequireSubtaskAsync(session, command.SubtaskId, inner);
                    RequireCurrentVersion(subtask, command.ExpectedVersion);
                    var siblings = await session.Subtasks.ListByTaskAsync(subtask.TaskId, inner);
                    var before = new TaskAggregateSnapshot(await RequireTaskAsync(session, subtask.TaskId, inner), siblings);

                    var assignments = RankOrdering.Place(
                        [.. siblings.Select(row => (row.Id, row.Position, row.Version))],
                        subtask.Id,
                        command.InsertionIndex);
                    if (assignments.Count == 0)
                    {
                        return new SubtaskMutationResult(subtask, Replayed: false); // already in place
                    }

                    SubtaskRecord moved = subtask;
                    foreach (var assignment in assignments)
                    {
                        var row = siblings.Single(candidate => candidate.Id == assignment.Id);
                        var updated = row with { Position = assignment.NewPosition, Version = row.Version + 1, UpdatedAtUtc = now };
                        var applied = await ApplySubtaskUpdateAsync(session, updated, row.Version, token);
                        if (applied.Id == subtask.Id)
                        {
                            moved = applied;
                        }
                    }

                    await WriteHistoryAsync(session, command.OperationId, subtask.TaskId, before, "subtask.reordered", now, inner);
                    return new SubtaskMutationResult(moved, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    private async Task<T> InWriteAsync<T>(
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body,
        CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);

    private static async Task<TaskRecord> RequireTaskAsync(IDataSession session, string taskId, CancellationToken token) =>
        await session.Tasks.GetAsync(taskId, token)
        ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");

    private static async Task<SubtaskRecord> RequireSubtaskAsync(IDataSession session, string subtaskId, CancellationToken token) =>
        await session.Subtasks.GetAsync(subtaskId, token)
        ?? throw AppException.NotFound("That subtask no longer exists. Refresh and try again.");

    private static void RequireCurrentVersion(SubtaskRecord subtask, long expectedVersion)
    {
        if (subtask.Version != expectedVersion)
        {
            throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");
        }
    }

    private static async Task<SubtaskRecord> ApplySubtaskUpdateAsync(IDataSession session, SubtaskRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Subtasks.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");

    private async Task WriteHistoryAsync(
        IDataSession session,
        string operationId,
        string taskId,
        TaskAggregateSnapshot? before,
        string eventKind,
        DateTimeOffset now,
        CancellationToken token)
    {
        var task = await session.Tasks.GetIncludingDeletedAsync(taskId, token)
            ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
        var subtasks = await session.Subtasks.ListByTaskIncludingDeletedAsync(task.Id, token);

        await TaskMutationSupport.WriteTaskHistoryAsync(
            session, ids, operationId, before,
            new TaskAggregateSnapshot(task, subtasks),
            eventKind, ["Subtasks"], now, token);
    }

    private static string EventKind(TaskItemStatus before, TaskItemStatus after) => after switch
    {
        TaskItemStatus.Done => "subtask.completed",
        TaskItemStatus.Cancelled => "subtask.cancelled",
        _ when TaskItemStatusRules.IsTerminal(before) => "subtask.reopened",
        _ => "subtask.status-changed",
    };
}
