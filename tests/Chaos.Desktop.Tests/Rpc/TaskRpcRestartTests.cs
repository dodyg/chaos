using Chaos.Application.Tasks;
using Chaos.Application.Containers;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// First vertical slice against real storage (PLAN §15 WP1): create through the RPC,
/// dispose the storage session (simulated app exit), reopen the data root and read the
/// task back through a fresh RPC service. Errors come back as data, never thrown.
/// </summary>
public sealed class TaskRpcRestartTests
{
    [Test]
    public async Task ProjectAndSubtasks_ThroughRpc_PersistAndReopen()
    {
        using var storage = await StartAsync(TestPaths.NewDataRoot());
        var ids = new SequentialIdGenerator();
        var clock = new FixedTimeProvider(Now);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var taskRpc = new TaskRpcService(tasks);
        var containers = new ContainerRpcService(new ContainerCommands(storage.Store, ids, clock), tasks);
        var subtasks = new SubtaskRpcService(tasks, new SubtaskCommands(storage.Store, ids, clock));
        var project = await containers.CreateAsync(new("project", "Project", "Project"), CancellationToken.None);
        await Assert.That(project.Ok).IsTrue();
        var task = await taskRpc.CreateAsync(new("task", "Task", null, project.Container!.Id), CancellationToken.None);
        var child = await subtasks.AddAsync(new("child", task.Task!.Id, "Step", "notes"), CancellationToken.None);
        var started = await subtasks.SetStatusAsync(new("started", child.Subtask!.Id, child.Subtask.Version, "InProgress"), CancellationToken.None);
        var done = await subtasks.SetStatusAsync(new("done", child.Subtask.Id, started.Subtask!.Version, "Done"), CancellationToken.None);
        var reopened = await subtasks.SetStatusAsync(new("reopen", child.Subtask.Id, done.Subtask!.Version, "NotStarted"), CancellationToken.None);
        await Assert.That(reopened.Subtask!.Status).IsEqualTo("InProgress");
        var listed = await containers.TasksAsync(new(project.Container.Id), CancellationToken.None);
        await Assert.That(listed.Tasks.Single().Id).IsEqualTo(task.Task.Id);
        var children = await subtasks.ListAsync(new(task.Task.Id), CancellationToken.None);
        await Assert.That(children.Subtasks.Single().Notes).IsEqualTo("notes");
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static async Task<ChaosStorageHandle> StartAsync(string dataRoot)
    {
        return await StorageBootstrap.StartAsync(
            StorageOptions.FromDataRoot(dataRoot), "0.1.0",
            new SequentialIdGenerator(), new FixedTimeProvider(Now), CancellationToken.None);
    }

    private static TaskRpcService NewRpc(ChaosStorageHandle storage) =>
        new(new TaskCommands(storage.Store, new SequentialIdGenerator(), new FixedTimeProvider(Now)));

    [Test]
    public async Task CreateThenRestart_GetReturnsPersistedTask()
    {
        var dataRoot = TestPaths.NewDataRoot();
        string taskId;
        var first = await StartAsync(dataRoot);
        using (first)
        {
            var created = await NewRpc(first).CreateAsync(
                new CreateTaskRequest("op-restart-1", "Survives a restart", "written before exit", null), CancellationToken.None);
            await Assert.That(created.Ok).IsTrue();
            await Assert.That(created.Error).IsNull();
            await Assert.That(created.Task!.Status).IsEqualTo("NotStarted");
            await Assert.That(created.Task.Version).IsEqualTo("1");
            taskId = created.Task.Id;
        }

        var second = await StartAsync(dataRoot);
        using (second)
        {
            var fetched = await NewRpc(second).GetAsync(new GetTaskRequest(taskId), CancellationToken.None);
            await Assert.That(fetched.Ok).IsTrue();
            await Assert.That(fetched.Error).IsNull();
            await Assert.That(fetched.Task!.Id).IsEqualTo(taskId);
            await Assert.That(fetched.Task.Title).IsEqualTo("Survives a restart");
            await Assert.That(fetched.Task.Notes).IsEqualTo("written before exit");
            await Assert.That(fetched.Task.Status).IsEqualTo("NotStarted");
            await Assert.That(fetched.Task.Version).IsEqualTo("1");
            var inbox = await NewRpc(second).ListInboxAsync(new ListTasksRequest(), CancellationToken.None);
            await Assert.That(inbox.Ok).IsTrue();
            await Assert.That(inbox.Tasks.Single().Id).IsEqualTo(taskId);
        }
    }

    [Test]
    public async Task EditStatusAndReorder_ThroughRpc_PersistInInbox()
    {
        using var storage = await StartAsync(TestPaths.NewDataRoot());
        var rpc = NewRpc(storage);
        var first = (await rpc.CreateAsync(new("rpc-one", "First", null, null), CancellationToken.None)).Task!;
        var second = (await rpc.CreateAsync(new("rpc-two", "Second", null, null), CancellationToken.None)).Task!;
        var edit = await rpc.EditAsync(new("rpc-edit", first.Id, first.Version, "Edited", "Notes", "2026-11-12", "09:00", "UTC", "2026-11-15", null, null, "High", 30, "Work"), CancellationToken.None);
        await Assert.That(edit.Ok).IsTrue();
        var status = await rpc.SetStatusAsync(new("rpc-status", first.Id, edit.Task!.Version, "Done", null), CancellationToken.None);
        await Assert.That(status.Ok).IsTrue();
        var reordered = await rpc.ReorderAsync(new("rpc-order", second.Id, second.Version, 0), CancellationToken.None);
        await Assert.That(reordered.Ok).IsTrue();

        var inbox = await rpc.ListInboxAsync(new(), CancellationToken.None);
        await Assert.That(inbox.Tasks[0].Id).IsEqualTo(second.Id);
        var saved = inbox.Tasks[1];
        await Assert.That(saved.Title).IsEqualTo("Edited");
        await Assert.That(saved.Status).IsEqualTo("Done");
        await Assert.That(saved.PlannedDate).IsEqualTo("2026-11-12");
        await Assert.That(saved.PlannedTime).IsEqualTo("09:00:00");
        await Assert.That(saved.DueDate).IsEqualTo("2026-11-15");
    }

    [Test]
    public async Task Create_SameOperationId_ReplaysSameTask()
    {
        using var storage = await StartAsync(TestPaths.NewDataRoot());
        var rpc = NewRpc(storage);

        var first = await rpc.CreateAsync(
            new CreateTaskRequest("op-dedupe", "Buy milk", null, null), CancellationToken.None);
        var second = await rpc.CreateAsync(
            new CreateTaskRequest("op-dedupe", "Buy milk", null, null), CancellationToken.None);

        await Assert.That(first.Ok).IsTrue();
        await Assert.That(second.Ok).IsTrue();
        await Assert.That(second.Task!.Id).IsEqualTo(first.Task!.Id);
    }

    [Test]
    public async Task Create_BlankTitle_ReturnsValidationErrorAsData()
    {
        using var storage = await StartAsync(TestPaths.NewDataRoot());

        var response = await NewRpc(storage).CreateAsync(
            new CreateTaskRequest("op-blank", "   ", null, null), CancellationToken.None);

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Task).IsNull();
        await Assert.That(response.Error!.Kind).IsEqualTo("validation");
        await Assert.That(response.Error.Fields.Single().FieldPath).IsEqualTo("title");
        await Assert.That(response.Error.CorrelationId).IsNotEmpty();
    }

    [Test]
    public async Task Get_UnknownTaskId_ReturnsNotFoundAsData()
    {
        using var storage = await StartAsync(TestPaths.NewDataRoot());

        var response = await NewRpc(storage).GetAsync(new GetTaskRequest("no-such-task"), CancellationToken.None);

        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Task).IsNull();
        await Assert.That(response.Error!.Kind).IsEqualTo("notFound");
    }
}
