using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.Containers;

public sealed record CreateContainerCommand(string OperationId, string Name, ContainerKind Kind, string? SpaceId);

public sealed record RenameContainerCommand(string OperationId, string ContainerId, long ExpectedVersion, string Name);

public sealed record ReorderContainerCommand(string OperationId, string ContainerId, long ExpectedVersion, int InsertionIndex);
public sealed record SetProjectDeadlineCommand(string OperationId, string ContainerId, long ExpectedVersion, DateOnly? DeadlineDate);

public sealed record ArchiveContainerCommand(string OperationId, string ContainerId, long ExpectedVersion);

public sealed record DeleteContainerCommand(string OperationId, string ContainerId, long ExpectedVersion, ContainerContentsChoice ContentsChoice);
public sealed record RestoreContainerCommand(string OperationId, string ContainerId, long ExpectedVersion);

/// <summary>
/// What happens to a container's contents when the container is deleted (PLAN §6): move the
/// tasks to the Inbox, or reversibly delete them together with the container. The Inbox
/// itself can never be deleted.
/// </summary>
public enum ContainerContentsChoice
{
    MoveToInbox = 0,
    DeleteContents = 1,
}

public sealed record CreateSectionCommand(string OperationId, string ContainerId, string Title);

public sealed record RenameSectionCommand(string OperationId, string SectionId, long ExpectedVersion, string Title);

public sealed record ReorderSectionCommand(string OperationId, string SectionId, long ExpectedVersion, int InsertionIndex);

public sealed record DeleteSectionCommand(string OperationId, string SectionId, long ExpectedVersion);
public sealed record RestoreSectionCommand(string OperationId, string SectionId, long ExpectedVersion);

public sealed record TaskRecovery(TaskRecord Before, TaskRecord After, IReadOnlyList<SubtaskRecord> ChildrenBefore, IReadOnlyList<SubtaskRecord> ChildrenAfter);
public sealed record SectionRecovery(SectionRecord Before, SectionRecord After);
public sealed record ContainerDeletionRecovery(IReadOnlyList<TaskRecovery> Tasks, IReadOnlyList<SectionRecovery> Sections);
public sealed record SectionDeletionRecovery(IReadOnlyList<TaskRecovery> Tasks);
public sealed record ContainerMutationResult(ContainerRecord Container, bool Replayed, ContainerDeletionRecovery? Recovery = null);

public sealed record SectionMutationResult(SectionRecord Section, bool Replayed, SectionDeletionRecovery? Recovery = null);

/// <summary>
/// Project progress (PLAN §6): Done top-level tasks over all nontombstoned, non-Cancelled
/// top-level tasks. A zero denominator is an empty progress state (null fractions).
/// Subtask progress is separate; cancelled subtasks are excluded from its denominator.
/// </summary>
public sealed record ProjectProgress(
    long DoneCount,
    long TotalCount,
    double? Fraction,
    long DoneSubtaskCount,
    long TotalSubtaskCount,
    double? SubtaskFraction);

/// <summary>
/// List/project container and section commands (PLAN §15 WP2): create, rename, manual order,
/// archive/unarchive, reversible deletion with an explicit contents choice, sections with
/// order and task-safe deletion, and project progress. The Inbox is protected: it can never
/// be created, renamed, reordered, archived or deleted through these commands.
/// </summary>
public sealed class ContainerCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public async Task<ContainerMutationResult> CreateAsync(CreateContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "container.create", command, now, async inner =>
                {
                    var name = TaskFieldValidation.Title(command.Name, "name");
                    if (command.Kind is ContainerKind.Inbox || !Enum.IsDefined(command.Kind))
                    {
                        throw AppException.Validation("kind", "Create either a list or a project. The Inbox exists by itself.");
                    }

                    var space = await ResolveSpaceAsync(session, command.SpaceId, profile.Id, inner);
                    await RequireFreeNameAsync(session, space.Id, name, selfId: null, inner);

                    var container = new ContainerRecord
                    {
                        Id = ids.NewId().ToString(),
                        SpaceId = space.Id,
                        Kind = command.Kind,
                        Name = name,
                        IsInbox = false,
                        Position = await session.Containers.GetNextPositionAsync(space.Id, inner),
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Containers.AddAsync(container, inner);

                    return new ContainerMutationResult(container, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<ContainerMutationResult> RenameAsync(RenameContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "container.rename", command, now, async inner =>
                {
                    var container = await RequireContainerAsync(session, command.ContainerId, inner);
                    RequireCurrentVersion(container, command.ExpectedVersion);
                    RequireNotInbox(container, "renamed");
                    var name = TaskFieldValidation.Title(command.Name, "name");
                    await RequireFreeNameAsync(session, container.SpaceId, name, selfId: container.Id, inner);

                    var updated = container with { Name = name, Version = container.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplyUpdateAsync(session, updated, container.Version, token);

                    return new ContainerMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<ContainerMutationResult> ReorderAsync(ReorderContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "container.reorder", command, now, async inner =>
                {
                    var container = await RequireContainerAsync(session, command.ContainerId, inner);
                    RequireCurrentVersion(container, command.ExpectedVersion);
                    RequireNotInbox(container, "reordered");
                    var siblings = await session.Containers.ListBySpaceAsync(container.SpaceId, inner);

                    // The Inbox keeps its place at the top: a list or project can never be placed above it.
                    var inboxIndex = siblings.ToList().FindIndex(row => row.IsInbox);
                    var assignments = RankOrdering.Place(
                        [.. siblings.Select(row => (row.Id, row.Position, row.Version))],
                        container.Id,
                        Math.Max(command.InsertionIndex, inboxIndex + 1));
                    if (assignments.Count == 0)
                    {
                        return new ContainerMutationResult(container, Replayed: false); // already in place
                    }

                    ContainerRecord moved = container;
                    foreach (var assignment in assignments)
                    {
                        var row = siblings.Single(candidate => candidate.Id == assignment.Id);
                        var updated = row with { Position = assignment.NewPosition, Version = row.Version + 1, UpdatedAtUtc = now };
                        var applied = await ApplyUpdateAsync(session, updated, row.Version, token);
                        if (applied.Id == container.Id)
                        {
                            moved = applied;
                        }
                    }

                    return new ContainerMutationResult(moved, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<ContainerMutationResult> ArchiveAsync(ArchiveContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await ApplyArchiveAsync(command, archiving: true, ct);
    }

    public async Task<ContainerMutationResult> SetDeadlineAsync(SetProjectDeadlineCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "project.deadline", command, now, async inner =>
            {
                var container = await RequireContainerAsync(session, command.ContainerId, inner);
                RequireCurrentVersion(container, command.ExpectedVersion);
                if (container.Kind != ContainerKind.Project) throw AppException.Validation("containerId", "Only projects have deadlines.");
                if (container.DeadlineDate == command.DeadlineDate) return new ContainerMutationResult(container, false);
                var updated = container with { DeadlineDate = command.DeadlineDate, Version = container.Version + 1, UpdatedAtUtc = now };
                return new ContainerMutationResult(await ApplyUpdateAsync(session, updated, container.Version, inner), false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<ContainerMutationResult> UnarchiveAsync(ArchiveContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await ApplyArchiveAsync(command, archiving: false, ct);
    }

    private async Task<ContainerMutationResult> ApplyArchiveAsync(ArchiveContainerCommand command, bool archiving, CancellationToken ct)
    {
        var kind = archiving ? "container.archive" : "container.unarchive";

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, kind, command, now, async inner =>
                {
                    var container = await RequireContainerAsync(session, command.ContainerId, inner);
                    RequireCurrentVersion(container, command.ExpectedVersion);
                    RequireNotInbox(container, archiving ? "archived" : "unarchived");

                    var changed = archiving != (container.ArchivedAtUtc is not null);
                    if (!changed)
                    {
                        return new ContainerMutationResult(container, Replayed: false); // already in that state
                    }

                    var updated = container with
                    {
                        ArchivedAtUtc = archiving ? now : null,
                        Version = container.Version + 1,
                        UpdatedAtUtc = now,
                    };
                    var applied = await ApplyUpdateAsync(session, updated, container.Version, token);

                    return new ContainerMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<ContainerMutationResult> DeleteAsync(DeleteContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(command.ContentsChoice)) throw AppException.Validation("contentsChoice", "Choose what to do with the tasks in this list.");

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "container.delete", command, now, async inner =>
                {
                    var container = await RequireContainerAsync(session, command.ContainerId, inner);
                    RequireCurrentVersion(container, command.ExpectedVersion);
                    RequireNotInbox(container, "deleted");

                    var tasks = await session.Tasks.ListByContainerAsync(container.Id, inner);
                    var taskRecovery = new List<TaskRecovery>();
                    var sectionRecovery = new List<SectionRecovery>();
                    if (command.ContentsChoice == ContainerContentsChoice.MoveToInbox)
                    {
                        var inbox = await session.Containers.GetInboxAsync(container.SpaceId, inner)
                            ?? throw new AppException(
                                AppErrorKind.StorageUnavailable,
                                "No Inbox exists yet. Restart Chaos to complete first-run setup.");

                        var position = await session.Tasks.GetNextPositionAsync(inbox.Id, inner);
                        foreach (var task in tasks)
                        {
                            var moved = task with
                            {
                                ContainerId = inbox.Id,
                                SectionId = null, // sections of the deleted container cannot follow the task
                                Position = position,
                                Version = task.Version + 1,
                                UpdatedAtUtc = now,
                            };
                            position += RankOrdering.Gap;
                            var applied = await TaskUpdateAsync(session, moved, task.Version, token);
                            taskRecovery.Add(new TaskRecovery(task, applied, [], []));

                            await WriteTaskHistoryAsync(session, command.OperationId, task, applied, "task.moved", now, inner);
                        }
                    }
                    else
                    {
                        foreach (var task in tasks)
                        {
                            var subtasks = await session.Subtasks.ListByTaskAsync(task.Id, inner);
                            var tombstoned = new List<SubtaskRecord>(subtasks.Count);
                            foreach (var subtask in subtasks)
                            {
                                var updatedSubtask = subtask with { DeletedAtUtc = now, Version = subtask.Version + 1, UpdatedAtUtc = now };
                                tombstoned.Add(await SubtaskUpdateAsync(session, updatedSubtask, subtask.Version, token));
                            }

                            var deleted = task with { DeletedAtUtc = now, Version = task.Version + 1, UpdatedAtUtc = now };
                            var applied = await TaskUpdateAsync(session, deleted, task.Version, token);
                            taskRecovery.Add(new TaskRecovery(task, applied, subtasks, tombstoned));

                            await WriteTaskHistorySnapshotAsync(
                                session, command.OperationId,
                                new TaskAggregateSnapshot(task, subtasks),
                                new TaskAggregateSnapshot(applied, tombstoned),
                                "task.deleted", now, inner);
                        }
                    }

                    foreach (var section in await session.Sections.ListByContainerAsync(container.Id, inner))
                    {
                        var updated = section with { DeletedAtUtc = now, Version = section.Version + 1, UpdatedAtUtc = now };
                        var appliedSection = await session.Sections.UpdateAsync(updated, section.Version, token)
                            ?? throw AppException.VersionConflict("This list changed elsewhere while you were working. Refresh and try again.");
                        sectionRecovery.Add(new SectionRecovery(section, appliedSection));
                    }

                    var containerDeleted = container with { DeletedAtUtc = now, Version = container.Version + 1, UpdatedAtUtc = now };
                    var deletedContainer = await ApplyUpdateAsync(session, containerDeleted, container.Version, token);

                    return new ContainerMutationResult(deletedContainer, Replayed: false, new ContainerDeletionRecovery(taskRecovery, sectionRecovery));
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<ContainerMutationResult> RestoreAsync(RestoreContainerCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "container.restore", command, now, async inner =>
            {
                var container = await session.Containers.GetIncludingDeletedAsync(command.ContainerId, inner) ?? throw AppException.NotFound("That list or project no longer exists.");
                RequireCurrentVersion(container, command.ExpectedVersion);
                if (container.DeletedAtUtc is null) return new ContainerMutationResult(container, false);
                var receipts = await session.Receipts.ListByKindAsync(profile.Id, "container.delete", inner);
                var deletion = receipts.Select(r => StoredJsonFormats.ReadSnapshot<StoredResultEnvelope<ContainerMutationResult>>(r.ResultJson)?.Result)
                    .FirstOrDefault(r => r?.Container.Id == container.Id && r.Container.Version == container.Version);
                var recovery = deletion?.Recovery ?? throw AppException.Validation("containerId", "This deletion has no recovery record. Restore a backup to recover its contents.");
                await RequireFreeNameAsync(session, container.SpaceId, container.Name, container.Id, inner);
                foreach (var section in recovery.Sections)
                {
                    var current = await session.Sections.GetIncludingDeletedAsync(section.After.Id, inner) ?? throw AppException.VersionConflict("A section changed after deletion. Refresh before restoring.");
                    RequireCurrentVersion(current, section.After.Version);
                }
                await ValidateRecoveryTasksAsync(session, recovery.Tasks, inner);
                var restored = await ApplyUpdateAsync(session, container with { DeletedAtUtc = null, Version = container.Version + 1, UpdatedAtUtc = now }, container.Version, inner);
                foreach (var section in recovery.Sections)
                    await ApplySectionUpdateAsync(session, section.Before with { Version = section.After.Version + 1, UpdatedAtUtc = now }, section.After.Version, inner);
                await RestoreRecoveryTasksAsync(session, recovery.Tasks, command.OperationId, now, inner);
                return new ContainerMutationResult(restored, false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<SectionMutationResult> CreateSectionAsync(CreateSectionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "section.create", command, now, async inner =>
                {
                    var title = TaskFieldValidation.Title(command.Title, "title");
                    var container = await RequireContainerAsync(session, command.ContainerId, inner);

                    var section = new SectionRecord
                    {
                        Id = ids.NewId().ToString(),
                        ContainerId = container.Id,
                        Title = title,
                        Position = await session.Sections.GetNextPositionAsync(container.Id, inner),
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    };
                    await session.Sections.AddAsync(section, inner);

                    return new SectionMutationResult(section, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<SectionMutationResult> RenameAsync(RenameSectionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "section.rename", command, now, async inner =>
                {
                    var section = await RequireSectionAsync(session, command.SectionId, inner);
                    RequireCurrentVersion(section, command.ExpectedVersion);
                    var title = TaskFieldValidation.Title(command.Title, "title");

                    var updated = section with { Title = title, Version = section.Version + 1, UpdatedAtUtc = now };
                    var applied = await ApplySectionUpdateAsync(session, updated, section.Version, token);

                    return new SectionMutationResult(applied, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<SectionMutationResult> ReorderAsync(ReorderSectionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "section.reorder", command, now, async inner =>
                {
                    var section = await RequireSectionAsync(session, command.SectionId, inner);
                    RequireCurrentVersion(section, command.ExpectedVersion);
                    var siblings = await session.Sections.ListByContainerAsync(section.ContainerId, inner);

                    var assignments = RankOrdering.Place(
                        [.. siblings.Select(row => (row.Id, row.Position, row.Version))],
                        section.Id,
                        command.InsertionIndex);
                    if (assignments.Count == 0)
                    {
                        return new SectionMutationResult(section, Replayed: false); // already in place
                    }

                    SectionRecord moved = section;
                    foreach (var assignment in assignments)
                    {
                        var row = siblings.Single(candidate => candidate.Id == assignment.Id);
                        var updated = row with { Position = assignment.NewPosition, Version = row.Version + 1, UpdatedAtUtc = now };
                        var applied = await ApplySectionUpdateAsync(session, updated, row.Version, token);
                        if (applied.Id == section.Id)
                        {
                            moved = applied;
                        }
                    }

                    return new SectionMutationResult(moved, Replayed: false);
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    /// <summary>
    /// Reversible section deletion (PLAN §1 decision 10): the tasks inside return to the
    /// container's unsectioned root, keeping their contents, order inside the container, history.
    /// </summary>
    public async Task<SectionMutationResult> DeleteAsync(DeleteSectionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (envelope, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "section.delete", command, now, async inner =>
                {
                    var section = await RequireSectionAsync(session, command.SectionId, inner);
                    RequireCurrentVersion(section, command.ExpectedVersion);

                    var tasks = await session.Tasks.ListByContainerAsync(section.ContainerId, inner);
                    var recovery = new List<TaskRecovery>();
                    foreach (var task in tasks.Where(task => task.SectionId == section.Id))
                    {
                        var updated = task with { SectionId = null, Version = task.Version + 1, UpdatedAtUtc = now };
                        var applied = await TaskUpdateAsync(session, updated, task.Version, token);
                        recovery.Add(new TaskRecovery(task, applied, [], []));

                        await WriteTaskHistoryAsync(session, command.OperationId, task, applied, "task.section-cleared", now, inner);
                    }

                    var deleted = section with { DeletedAtUtc = now, Version = section.Version + 1, UpdatedAtUtc = now };
                    var appliedSection = await ApplySectionUpdateAsync(session, deleted, section.Version, token);

                    return new SectionMutationResult(appliedSection, Replayed: false, new SectionDeletionRecovery(recovery));
                }, token);

            return replayed ? envelope with { Replayed = true } : envelope;
        }, ct);
    }

    public async Task<SectionMutationResult> RestoreAsync(RestoreSectionCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "section.restore", command, now, async inner =>
            {
                var section = await session.Sections.GetIncludingDeletedAsync(command.SectionId, inner) ?? throw AppException.NotFound("That section no longer exists.");
                RequireCurrentVersion(section, command.ExpectedVersion);
                if (section.DeletedAtUtc is null) return new SectionMutationResult(section, false);
                _ = await RequireContainerAsync(session, section.ContainerId, inner);
                var receipts = await session.Receipts.ListByKindAsync(profile.Id, "section.delete", inner);
                var deletion = receipts.Select(r => StoredJsonFormats.ReadSnapshot<StoredResultEnvelope<SectionMutationResult>>(r.ResultJson)?.Result)
                    .FirstOrDefault(r => r?.Section.Id == section.Id && r.Section.Version == section.Version);
                var recovery = deletion?.Recovery ?? throw AppException.Validation("sectionId", "This deletion has no recovery record. Restore a backup to recover its contents.");
                await ValidateRecoveryTasksAsync(session, recovery.Tasks, inner);
                var restored = await ApplySectionUpdateAsync(session, section with { DeletedAtUtc = null, Version = section.Version + 1, UpdatedAtUtc = now }, section.Version, inner);
                await RestoreRecoveryTasksAsync(session, recovery.Tasks, command.OperationId, now, inner);
                return new SectionMutationResult(restored, false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private static async Task ValidateRecoveryTasksAsync(IDataSession session, IReadOnlyList<TaskRecovery> tasks, CancellationToken token)
    {
        foreach (var item in tasks)
        {
            var current = await session.Tasks.GetIncludingDeletedAsync(item.After.Id, token) ?? throw AppException.VersionConflict("A task changed after deletion. Refresh before restoring.");
            RequireCurrentVersion(current, item.After.Version);
            if (item.ChildrenAfter.Count == 0) continue;
            var children = await session.Subtasks.ListByTaskIncludingDeletedAsync(item.After.Id, token);
            foreach (var child in item.ChildrenAfter)
            {
                var currentChild = children.SingleOrDefault(x => x.Id == child.Id) ?? throw AppException.VersionConflict("A subtask changed after deletion. Refresh before restoring.");
                RequireCurrentVersion(currentChild, child.Version);
            }
        }
    }

    private async Task RestoreRecoveryTasksAsync(IDataSession session, IReadOnlyList<TaskRecovery> tasks, string operationId, DateTimeOffset now, CancellationToken token)
    {
        foreach (var item in tasks)
        {
            var beforeChildren = await session.Subtasks.ListByTaskIncludingDeletedAsync(item.After.Id, token);
            foreach (var child in item.ChildrenBefore)
            {
                var after = item.ChildrenAfter.Single(x => x.Id == child.Id);
                await SubtaskUpdateAsync(session, child with { Version = after.Version + 1, UpdatedAtUtc = now }, after.Version, token);
            }
            var restored = await TaskUpdateAsync(session, item.Before with { Version = item.After.Version + 1, UpdatedAtUtc = now }, item.After.Version, token);
            var afterChildren = await session.Subtasks.ListByTaskIncludingDeletedAsync(item.After.Id, token);
            await WriteTaskHistorySnapshotAsync(session, operationId, new TaskAggregateSnapshot(item.After, beforeChildren), new TaskAggregateSnapshot(restored, afterChildren), "task.restored", now, token);
        }
    }

    public async Task<ProjectProgress> GetProjectProgressAsync(string containerId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var container = await session.Containers.GetAsync(containerId, token)
                ?? throw AppException.NotFound("That project no longer exists. Refresh and try again.");
            if (container.Kind != ContainerKind.Project)
            {
                throw AppException.Validation("containerId", "Progress is available for projects. Choose a project.");
            }

            var tasks = await session.Tasks.ListByContainerAsync(containerId, token);
            var counted = tasks.Where(task => task.Status != TaskItemStatus.Cancelled).ToList();
            var done = counted.Count(task => task.Status == TaskItemStatus.Done);

            long subtasksDone = 0;
            long subtasksTotal = 0;
            foreach (var task in counted)
            {
                foreach (var subtask in await session.Subtasks.ListByTaskAsync(task.Id, token))
                {
                    if (subtask.Status == TaskItemStatus.Cancelled)
                    {
                        continue;
                    }

                    subtasksTotal++;
                    if (subtask.Status == TaskItemStatus.Done)
                    {
                        subtasksDone++;
                    }
                }
            }

            return new ProjectProgress(
                done,
                counted.Count,
                counted.Count == 0 ? null : (double)done / counted.Count,
                subtasksDone,
                subtasksTotal,
                subtasksTotal == 0 ? null : (double)subtasksDone / subtasksTotal);
        }, ct);

    public async Task<IReadOnlyList<ContainerRecord>> ListAsync(string spaceId, CancellationToken ct = default) =>
        await store.QueryAsync((session, token) => session.Containers.ListBySpaceAsync(spaceId, token), ct);

    public async Task<IReadOnlyList<ContainerRecord>> ListPersonalAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet.");
            var space = await ResolveSpaceAsync(session, null, profile.Id, token);
            return await session.Containers.ListBySpaceAsync(space.Id, token);
        }, ct);

    public async Task<IReadOnlyList<ContainerRecord>> ListDeletedPersonalAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet.");
            var space = await ResolveSpaceAsync(session, null, profile.Id, token);
            return await session.Containers.ListDeletedBySpaceAsync(space.Id, token);
        }, ct);

    public async Task<IReadOnlyList<SectionRecord>> ListSectionsAsync(string containerId, CancellationToken ct = default) =>
        await store.QueryAsync((session, token) => session.Sections.ListByContainerAsync(containerId, token), ct);

    public async Task<IReadOnlyList<SectionRecord>> ListDeletedSectionsAsync(string containerId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) => { _ = await RequireContainerAsync(session, containerId, token); return await session.Sections.ListDeletedByContainerAsync(containerId, token); }, ct);

    private async Task<T> InWriteAsync<T>(
        Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body,
        CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);

    private static async Task<SpaceRecord> ResolveSpaceAsync(IDataSession session, string? spaceId, string profileId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(spaceId))
        {
            return await session.Spaces.GetPersonalByOwnerAsync(profileId, token)
                ?? throw new AppException(
                    AppErrorKind.StorageUnavailable,
                    "No personal space exists yet. Restart Chaos to complete first-run setup.");
        }

        return await session.Spaces.GetAsync(spaceId, token)
            ?? throw AppException.NotFound("That space no longer exists. Refresh and try again.");
    }

    private static async Task<ContainerRecord> RequireContainerAsync(IDataSession session, string containerId, CancellationToken token) =>
        await session.Containers.GetAsync(containerId, token)
        ?? throw AppException.NotFound("That list or project no longer exists. Refresh and try again.");

    private static async Task<SectionRecord> RequireSectionAsync(IDataSession session, string sectionId, CancellationToken token) =>
        await session.Sections.GetAsync(sectionId, token)
        ?? throw AppException.NotFound("That section no longer exists. Refresh and try again.");

    private static void RequireCurrentVersion(AggregateRecord aggregate, long expectedVersion)
    {
        if (aggregate.Version != expectedVersion)
        {
            throw AppException.VersionConflict("This list changed elsewhere while you were working. Refresh and try again.");
        }
    }

    private static void RequireNotInbox(ContainerRecord container, string what)
    {
        if (container.IsInbox)
        {
            throw AppException.Validation("containerId", $"The Inbox cannot be {what}. It is the default place for captured tasks.");
        }
    }

    /// <summary>
    /// Names are unique among the active containers of one space (PLAN §5 partial index).
    /// The normalized form mirrors the SQLite store's (trim, invariant lower case).
    /// </summary>
    private static async Task RequireFreeNameAsync(IDataSession session, string spaceId, string name, string? selfId, CancellationToken token)
    {
        var normalized = name.Trim().ToLowerInvariant();
        var existing = await session.Containers.ListBySpaceAsync(spaceId, token);
        if (existing.Any(candidate => candidate.Id != selfId && candidate.Name.Trim().ToLowerInvariant() == normalized))
        {
            throw AppException.Validation("name", "A list or project with that name already exists. Choose a different name.");
        }
    }

    private static async Task<ContainerRecord> ApplyUpdateAsync(IDataSession session, ContainerRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Containers.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This list changed elsewhere while you were working. Refresh and try again.");

    private static async Task<SectionRecord> ApplySectionUpdateAsync(IDataSession session, SectionRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Sections.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This list changed elsewhere while you were working. Refresh and try again.");

    private static async Task<TaskRecord> TaskUpdateAsync(IDataSession session, TaskRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Tasks.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");

    private static async Task<SubtaskRecord> SubtaskUpdateAsync(IDataSession session, SubtaskRecord updated, long expectedVersion, CancellationToken token) =>
        await session.Subtasks.UpdateAsync(updated, expectedVersion, token)
        ?? throw AppException.VersionConflict("This task changed elsewhere while you were working. Refresh and try again.");

    private async Task WriteTaskHistoryAsync(
        IDataSession session,
        string operationId,
        TaskRecord before,
        TaskRecord after,
        string eventKind,
        DateTimeOffset now,
        CancellationToken token) => await TaskMutationSupport.WriteTaskHistoryAsync(
            session,
            ids,
            operationId,
            new TaskAggregateSnapshot(before, await session.Subtasks.ListByTaskIncludingDeletedAsync(before.Id, token)),
            new TaskAggregateSnapshot(after, await session.Subtasks.ListByTaskIncludingDeletedAsync(after.Id, token)),
            eventKind,
            TaskMutationSupport.ChangedTaskFields(before, after),
            now,
            token);

    private async Task WriteTaskHistorySnapshotAsync(
        IDataSession session,
        string operationId,
        TaskAggregateSnapshot before,
        TaskAggregateSnapshot after,
        string eventKind,
        DateTimeOffset now,
        CancellationToken token) => await TaskMutationSupport.WriteTaskHistoryAsync(
            session,
            ids,
            operationId,
            before,
            after,
            eventKind,
            TaskMutationSupport.ChangedTaskFields(before.Task, after.Task),
            now,
            token);
}
