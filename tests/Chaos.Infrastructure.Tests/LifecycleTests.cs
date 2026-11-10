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
