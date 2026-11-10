using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests;

public sealed class LifecycleTests
{
    [Test]
    public async Task ReorderContainer_AboveInbox_KeepsInboxFirst()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var containers = new ContainerCommands(storage.Store, ids, clock);
        await containers.CreateAsync(new(Guid.NewGuid().ToString(), "First", ContainerKind.List, null));
        var second = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Second", ContainerKind.List, null))).Container;

        await containers.ReorderAsync(new ReorderContainerCommand(Guid.NewGuid().ToString(), second.Id, second.Version, 0));

        var rows = await containers.ListPersonalAsync();
        await Assert.That(rows[0].IsInbox).IsTrue();
        await Assert.That(string.Join(",", rows.Skip(1).Select(x => x.Name))).IsEqualTo("Second,First");
    }

    [Test]
    public async Task ReorderTask_InSection_LeavesOtherSectionsUntouched()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var containers = new ContainerCommands(storage.Store, ids, clock);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var section = (await containers.CreateSectionAsync(new(Guid.NewGuid().ToString(), project.Id, "Section"))).Section;
        var outside = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Outside", null))).Task;
        var first = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "First", null))).Task;
        var second = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Second", null))).Task;
        await tasks.MoveAsync(new(Guid.NewGuid().ToString(), first.Id, first.Version, project.Id, section.Id));
        var moved = (await tasks.MoveAsync(new(Guid.NewGuid().ToString(), second.Id, second.Version, project.Id, section.Id))).Task;
        await tasks.ReorderAsync(new(Guid.NewGuid().ToString(), moved.Id, moved.Version, 0));
        var rows = await tasks.ListAsync(project.Id);
        await Assert.That(string.Join(",", rows.Where(x => x.SectionId == section.Id).Select(x => x.Title))).IsEqualTo("Second,First");
        var unchanged = rows.Single(x => x.Id == outside.Id);
        await Assert.That(unchanged.Position).IsEqualTo(outside.Position);
        await Assert.That(unchanged.Version).IsEqualTo(outside.Version);
    }

    [Test]
    public async Task UpdateDetails_AllDayDatesAndInvalidTimedAnchor_PreserveEnteredDates()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Task", null))).Task;
        var planned = new DateOnly(2027, 1, 2);
        var due = new DateOnly(2027, 1, 1);
        var result = await tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, "Task", "", planned, null, null, due, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await Assert.That(result.Warning).IsNotNull();
        var saved = (await tasks.GetAsync(task.Id))!;
        await Assert.That(saved.PlannedDate).IsEqualTo(planned);
        await Assert.That(saved.DueDate).IsEqualTo(due);
        await Assert.That(saved.PlannedTime).IsNull();
        Chaos.Application.Abstractions.AppException? error = null;
        try { await tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString(), task.Id, saved.Version, "Task", "", null, new TimeOnly(9, 0), "UTC", due, null, null, TaskPriority.Normal, null, CapacityCategory.Personal)); }
        catch (Chaos.Application.Abstractions.AppException caught) { error = caught; }
        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.Validation);
        await Assert.That((await tasks.GetAsync(task.Id))!.PlannedDate).IsEqualTo(planned);
    }

    [Test]
    [Arguments(TaskItemStatus.NotStarted)]
    [Arguments(TaskItemStatus.InProgress)]
    [Arguments(TaskItemStatus.WaitingOnSomeone)]
    [Arguments(TaskItemStatus.NeedsReview)]
    [Arguments(TaskItemStatus.Done)]
    [Arguments(TaskItemStatus.Cancelled)]
    public async Task SetStatus_EachBuiltInStatus_PersistsMatchingTerminalTimestamp(TaskItemStatus status)
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Task", null))).Task;
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, status, null));
        var saved = (await tasks.GetAsync(task.Id))!;
        await Assert.That(saved.Status).IsEqualTo(status);
        await Assert.That(saved.CompletedAtUtc is not null).IsEqualTo(status == TaskItemStatus.Done);
        await Assert.That(saved.CancelledAtUtc is not null).IsEqualTo(status == TaskItemStatus.Cancelled);
    }

    [Test]
    [Arguments(OpenSubtaskChoice.CompleteAll, TaskItemStatus.Done)]
    [Arguments(OpenSubtaskChoice.KeepOpen, TaskItemStatus.NotStarted)]
    public async Task CompleteParent_ExplicitSubtaskChoice_AppliesOnlyRequestedChange(OpenSubtaskChoice choice, TaskItemStatus expected)
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var subtasks = new SubtaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Parent", null))).Task;
        await subtasks.AddAsync(new(Guid.NewGuid().ToString(), task.Id, "Child", null));
        Chaos.Application.Abstractions.AppException? error = null;
        try { await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, TaskItemStatus.Done, null)); }
        catch (Chaos.Application.Abstractions.AppException caught) { error = caught; }
        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.Validation);
        await Assert.That((await tasks.GetAsync(task.Id))!.Status).IsEqualTo(TaskItemStatus.NotStarted);
        error = null;
        try { await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, TaskItemStatus.Done, (OpenSubtaskChoice)99)); }
        catch (Chaos.Application.Abstractions.AppException caught) { error = caught; }
        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.Validation);
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, TaskItemStatus.Done, choice));
        var detail = await tasks.GetDetailAsync(task.Id);
        await Assert.That(detail.Task!.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(detail.Subtasks.Single().Status).IsEqualTo(expected);
    }

    [Test]
    public async Task Reorder_NoRankGap_CompactsWithoutLosingOrder()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var first = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "First", null))).Task;
        var second = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Second", null))).Task;
        var third = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Third", null))).Task;
        await storage.Store.ExecuteAsync(async (session, token) =>
        {
            await session.Tasks.UpdateAsync(second with { Position = 1, Version = 2 }, second.Version, token);
            await session.Tasks.UpdateAsync(third with { Position = 2, Version = 2 }, third.Version, token);
            return true;
        });
        await tasks.ReorderAsync(new(Guid.NewGuid().ToString(), third.Id, 2, 1));
        var rows = await tasks.ListInboxAsync();
        await Assert.That(string.Join(",", rows.Select(x => x.Title))).IsEqualTo("First,Third,Second");
        await Assert.That(string.Join(",", rows.Select(x => x.Position))).IsEqualTo("0,1024,2048");
        await Assert.That(rows[0].Id).IsEqualTo(first.Id);
    }

    [Test]
    [Arguments(ContainerContentsChoice.MoveToInbox)]
    [Arguments(ContainerContentsChoice.DeleteContents)]
    public async Task RestoreContainer_AfterRestart_RestoresOnlyItsDeletion(ContainerContentsChoice choice)
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        string containerId; string sectionId; string taskId; long version;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None))
        {
            var containers = new ContainerCommands(storage.Store, ids, clock);
            var tasks = new TaskCommands(storage.Store, ids, clock);
            var children = new SubtaskCommands(storage.Store, ids, clock);
            var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
            containerId = project.Id;
            var section = (await containers.CreateSectionAsync(new(Guid.NewGuid().ToString(), project.Id, "Section"))).Section;
            sectionId = section.Id;
            var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Task", null))).Task;
            taskId = task.Id;
            await tasks.MoveAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, project.Id, section.Id));
            await children.AddAsync(new(Guid.NewGuid().ToString(), task.Id, "Child", null));
            version = (await containers.DeleteAsync(new DeleteContainerCommand(Guid.NewGuid().ToString(), project.Id, project.Version, choice))).Container.Version;
        }
        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var restoredContainers = new ContainerCommands(reopened.Store, ids, clock);
        var restoredTasks = new TaskCommands(reopened.Store, ids, clock);
        await Assert.That((await restoredContainers.ListDeletedPersonalAsync()).Single().Id).IsEqualTo(containerId);
        await restoredContainers.RestoreAsync(new RestoreContainerCommand(Guid.NewGuid().ToString(), containerId, version));
        var detail = await restoredTasks.GetDetailAsync(taskId);
        await Assert.That(detail.Task!.ContainerId).IsEqualTo(containerId);
        await Assert.That(detail.Task.SectionId).IsEqualTo(sectionId);
        await Assert.That(detail.Subtasks.Single().Title).IsEqualTo("Child");
        await Assert.That((await restoredContainers.ListSectionsAsync(containerId)).Single().Id).IsEqualTo(sectionId);
    }

    [Test]
    public async Task RestoreContainer_MovedTaskEdited_RejectsAtomically()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var containers = new ContainerCommands(storage.Store, ids, clock);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Task", null))).Task;
        var deleted = (await containers.DeleteAsync(new DeleteContainerCommand(Guid.NewGuid().ToString(), project.Id, project.Version, ContainerContentsChoice.MoveToInbox))).Container;
        var moved = (await tasks.GetAsync(task.Id))!;
        await tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString(), moved.Id, moved.Version, "Edited", "", null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        Chaos.Application.Abstractions.AppException? error = null;
        try { await containers.RestoreAsync(new RestoreContainerCommand(Guid.NewGuid().ToString(), project.Id, deleted.Version)); }
        catch (Chaos.Application.Abstractions.AppException caught) { error = caught; }
        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.VersionConflict);
        await Assert.That((await containers.ListDeletedPersonalAsync()).Single().Id).IsEqualTo(project.Id);
        await Assert.That((await tasks.GetAsync(task.Id))!.Title).IsEqualTo("Edited");
    }

    [Test]
    public async Task RestoreSection_WithoutLaterEdits_RestoresTaskMembership()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var containers = new ContainerCommands(storage.Store, ids, clock);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var section = (await containers.CreateSectionAsync(new(Guid.NewGuid().ToString(), project.Id, "Section"))).Section;
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Task", null))).Task;
        await tasks.MoveAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, project.Id, section.Id));
        var deleted = (await containers.DeleteAsync(new DeleteSectionCommand(Guid.NewGuid().ToString(), section.Id, section.Version))).Section;
        await Assert.That((await tasks.GetAsync(task.Id))!.SectionId).IsNull();
        await containers.RestoreAsync(new RestoreSectionCommand(Guid.NewGuid().ToString(), section.Id, deleted.Version));
        await Assert.That((await tasks.GetAsync(task.Id))!.SectionId).IsEqualTo(section.Id);
    }

    [Test]
    public async Task RestoreTask_ChildDeletedSeparatelyAtSameInstant_StaysDeletedAfterRestart()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        string taskId;
        long taskVersion;
        string deletedChildId;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None))
        {
            var tasks = new TaskCommands(storage.Store, ids, clock);
            var subtasks = new SubtaskCommands(storage.Store, ids, clock);
            var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Parent", null))).Task;
            taskId = task.Id;
            var separate = (await subtasks.AddAsync(new(Guid.NewGuid().ToString(), taskId, "Separate", null))).Subtask;
            deletedChildId = separate.Id;
            await subtasks.AddAsync(new(Guid.NewGuid().ToString(), taskId, "Together", null));
            await subtasks.DeleteAsync(new(Guid.NewGuid().ToString(), separate.Id, separate.Version, separate.Title, separate.Notes));
            taskVersion = (await tasks.DeleteAsync(new(Guid.NewGuid().ToString(), taskId, task.Version))).Task.Version;
        }
        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var restoredTasks = new TaskCommands(reopened.Store, ids, clock);
        var restoredSubtasks = new SubtaskCommands(reopened.Store, ids, clock);
        await Assert.That((await restoredTasks.ListDeletedAsync(null)).Single().Id).IsEqualTo(taskId);
        await restoredTasks.RestoreAsync(new(Guid.NewGuid().ToString(), taskId, taskVersion));
        var detail = await restoredTasks.GetDetailAsync(taskId);
        await Assert.That(detail.Subtasks.Single().Title).IsEqualTo("Together");
        var deleted = await restoredSubtasks.ListDeletedAsync(taskId);
        await Assert.That(deleted.Single().Id).IsEqualTo(deletedChildId);
        await restoredSubtasks.RestoreAsync(new(Guid.NewGuid().ToString(), taskId, deletedChildId, deleted.Single().Version));
        await Assert.That((await restoredTasks.GetDetailAsync(taskId)).Subtasks.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DeletedTemplate_AfterRestart_CanBeDiscoveredAndRestored()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        string templateId;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None))
        {
            var commands = new TemplateCommands(storage.Store, ids, clock);
            var item = (await commands.CreateAsync(new(Guid.NewGuid().ToString(), "Template", new TemplatePayload { Title = "Task" }, null))).Template;
            templateId = item.Id;
            await commands.DeleteAsync(new(Guid.NewGuid().ToString(), item.Id, item.Version));
        }
        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var templates = new TemplateCommands(reopened.Store, ids, clock);
        var deleted = (await templates.ListDeletedAsync()).Single();
        await Assert.That(deleted.Id).IsEqualTo(templateId);
        await templates.RestoreAsync(new(Guid.NewGuid().ToString(), deleted.Id, deleted.Version));
        await Assert.That((await templates.ListAsync(null)).Single().Id).IsEqualTo(templateId);
        await Assert.That((await templates.ListDeletedAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ProjectProgress_CancelledTasksExcluded_AndDeleteMovesContentsToInbox()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var containers = new ContainerCommands(storage.Store, ids, clock);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var done = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Done", null))).Task;
        var cancelled = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Cancelled", null))).Task;
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), done.Id, done.Version, TaskItemStatus.Done, null));
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), cancelled.Id, cancelled.Version, TaskItemStatus.Cancelled, null));
        var progress = await containers.GetProjectProgressAsync(project.Id);
        await Assert.That(progress.DoneCount).IsEqualTo(1L);
        await Assert.That(progress.TotalCount).IsEqualTo(1L);
        await containers.DeleteAsync(new DeleteContainerCommand(Guid.NewGuid().ToString(), project.Id, project.Version, ContainerContentsChoice.MoveToInbox));
        var inbox = await tasks.ListInboxAsync();
        await Assert.That(inbox.Count).IsEqualTo(2);
        await Assert.That(inbox.Select(x => x.Id).Contains(done.Id)).IsTrue();
        await Assert.That(inbox.Select(x => x.Id).Contains(cancelled.Id)).IsTrue();
    }

    [Test]
    public async Task ProjectDeadline_AndInboxProtection_AreEnforcedInStorage()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var commands = new ContainerCommands(storage.Store, ids, clock);
        var project = (await commands.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var date = new DateOnly(2026, 12, 1);
        var updated = (await commands.SetDeadlineAsync(new(Guid.NewGuid().ToString(), project.Id, project.Version, date))).Container;
        await Assert.That((await commands.ListPersonalAsync()).Single(x => x.Id == project.Id).DeadlineDate).IsEqualTo(date);
        await Assert.That(updated.Version).IsEqualTo(project.Version + 1);
        var inbox = (await commands.ListPersonalAsync()).Single(x => x.IsInbox);
        Chaos.Application.Abstractions.AppException? error = null;
        try { await commands.RenameAsync(new RenameContainerCommand(Guid.NewGuid().ToString(), inbox.Id, inbox.Version, "Other")); }
        catch (Chaos.Application.Abstractions.AppException caught) { error = caught; }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.Validation);
    }

    [Test]
    public async Task ArchiveAndUnarchive_Project_PersistsBothTransitions()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var commands = new ContainerCommands(storage.Store, ids, clock);
        var project = (await commands.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var archived = (await commands.ArchiveAsync(new(Guid.NewGuid().ToString(), project.Id, project.Version))).Container;
        await Assert.That(archived.ArchivedAtUtc).IsNotNull();
        var restored = (await commands.UnarchiveAsync(new(Guid.NewGuid().ToString(), project.Id, archived.Version))).Container;
        await Assert.That(restored.ArchivedAtUtc).IsNull();
        await Assert.That((await commands.ListPersonalAsync()).Single(x => x.Id == project.Id).Version).IsEqualTo(restored.Version);
    }

    [Test]
    public async Task MoveTask_ToProjectSection_PersistsContainerAndRejectsForeignSection()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var containers = new ContainerCommands(storage.Store, ids, clock);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
        var other = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Other", ContainerKind.List, null))).Container;
        var section = (await containers.CreateSectionAsync(new(Guid.NewGuid().ToString(), project.Id, "Phase"))).Section;
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Task", null))).Task;

        var moved = (await tasks.MoveAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, project.Id, section.Id))).Task;
        await Assert.That(moved.ContainerId).IsEqualTo(project.Id);
        await Assert.That(moved.SectionId).IsEqualTo(section.Id);
        await Assert.That((await tasks.ListAsync(project.Id)).Single().Id).IsEqualTo(task.Id);
        Chaos.Application.Abstractions.AppException? error = null;
        try { await tasks.MoveAsync(new(Guid.NewGuid().ToString(), task.Id, moved.Version, other.Id, section.Id)); }
        catch (Chaos.Application.Abstractions.AppException caught) { error = caught; }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.Validation);
        await Assert.That((await tasks.GetAsync(task.Id))!.ContainerId).IsEqualTo(project.Id);
    }

    [Test]
    public async Task SubtaskStatus_Reopen_RestoresPreviousStatusFromHistory()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var subtasks = new SubtaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Task", null))).Task;
        var subtask = (await subtasks.AddAsync(new(Guid.NewGuid().ToString(), task.Id, "Step", null))).Subtask;
        var started = (await subtasks.SetStatusAsync(new(Guid.NewGuid().ToString(), subtask.Id, subtask.Version, TaskItemStatus.InProgress))).Subtask;
        var done = (await subtasks.SetStatusAsync(new(Guid.NewGuid().ToString(), subtask.Id, started.Version, TaskItemStatus.Done))).Subtask;

        var reopened = (await subtasks.SetStatusAsync(new(Guid.NewGuid().ToString(), subtask.Id, done.Version, TaskItemStatus.NotStarted))).Subtask;

        await Assert.That(reopened.Status).IsEqualTo(TaskItemStatus.InProgress);
        await Assert.That(reopened.CompletedAtUtc).IsNull();
    }

    [Test]
    public async Task UpdateDetails_SeparateTimedDates_RoundTripsWithoutChangingEitherAnchor()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Task", null))).Task;
        var planned = new DateOnly(2026, 11, 12);
        var due = new DateOnly(2026, 11, 15);
        await tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, "Task", "", planned, new TimeOnly(9, 0), "UTC", due, new TimeOnly(17, 0), "UTC", TaskPriority.High, 30, CapacityCategory.Work));

        var saved = (await tasks.GetAsync(task.Id))!;
        await Assert.That(saved.PlannedDate).IsEqualTo(planned);
        await Assert.That(saved.DueDate).IsEqualTo(due);
        await Assert.That(saved.PlannedTime).IsEqualTo(new TimeOnly(9, 0));
        await Assert.That(saved.DueTime).IsEqualTo(new TimeOnly(17, 0));
    }

    [Test]
    public async Task ProjectWorkflow_CompleteDeleteRestoreAndRestart_PreservesTaskAndSubtask()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        string taskId;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None))
        {
            var containers = new ContainerCommands(storage.Store, ids, clock);
            var tasks = new TaskCommands(storage.Store, ids, clock);
            var subtasks = new SubtaskCommands(storage.Store, ids, clock);
            var project = (await containers.CreateAsync(new(Guid.NewGuid().ToString(), "Project", ContainerKind.Project, null))).Container;
            var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), project.Id, "Task", null))).Task;
            taskId = task.Id;
            await subtasks.AddAsync(new(Guid.NewGuid().ToString(), task.Id, "Step", null));
            var completed = (await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, TaskItemStatus.Done, OpenSubtaskChoice.CompleteAll))).Task;
            var progress = await containers.GetProjectProgressAsync(project.Id);
            await Assert.That(progress.DoneCount).IsEqualTo(1L);
            await Assert.That(progress.DoneSubtaskCount).IsEqualTo(1L);
            var deleted = (await tasks.DeleteAsync(new(Guid.NewGuid().ToString(), task.Id, completed.Version))).Task;
            await tasks.RestoreAsync(new(Guid.NewGuid().ToString(), task.Id, deleted.Version));
        }

        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var detail = await new TaskCommands(reopened.Store, ids, clock).GetDetailAsync(taskId);
        await Assert.That(detail.Task!.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(detail.Subtasks.Count).IsEqualTo(1);
        await Assert.That(detail.Subtasks[0].Status).IsEqualTo(TaskItemStatus.Done);
    }
}
