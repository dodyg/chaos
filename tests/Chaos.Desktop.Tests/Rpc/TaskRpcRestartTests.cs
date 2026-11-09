using Chaos.Application.Tasks;
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
        }
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
