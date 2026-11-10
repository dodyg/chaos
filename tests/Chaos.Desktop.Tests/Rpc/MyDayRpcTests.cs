using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class MyDayRpcTests
{
    private static TaskQueryRequest MyDay(string day) =>
        new("MyDay", null, null, null, null, null, null, null, null, null, null, null, false, false, null, null, null, day);

    [Test]
    public async Task AddThroughRpc_Restart_TaskIsStillOnThatDay()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        string today;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None))
        {
            var ids = new SequentialIdGenerator();
            var tasks = new TaskCommands(storage.Store, ids, clock);
            var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Read the contract", null))).Task;
            var rpc = new MyDayRpcService(new MyDayCommands(storage.Store, ids, clock));
            var clockResult = await rpc.ClockAsync(new(), CancellationToken.None);
            today = clockResult.Clock!.Today;

            var added = await rpc.AddAsync(new(Guid.NewGuid().ToString(), today, task.Id), CancellationToken.None);
            await Assert.That(added.Ok).IsTrue();
            await Assert.That(added.Day!.OpenCount).IsEqualTo(1);
            await Assert.That(added.Day.Editable).IsTrue();

            var invalid = await rpc.AddAsync(new(Guid.NewGuid().ToString(), "10/11/2026", task.Id), CancellationToken.None);
            await Assert.That(invalid.Ok).IsFalse();
            await Assert.That(invalid.Error!.Fields.Single().FieldPath).IsEqualTo("date");
        }

        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var queries = new QueryRpcService(new TaskQueries(reopened.Store, clock));
        var rows = await queries.RunAsync(MyDay(today), CancellationToken.None);
        await Assert.That(rows.Ok).IsTrue();
        await Assert.That(rows.Rows.Single().Task.Title).IsEqualTo("Read the contract");
        var counts = await queries.CountsAsync(new(), CancellationToken.None);
        await Assert.That(counts.Views.Single(view => view.View == "MyDay").Count).IsEqualTo(1);
        var opened = await new MyDayRpcService(new MyDayCommands(reopened.Store, new SequentialIdGenerator(), clock)).OpenAsync(new(today), CancellationToken.None);
        await Assert.That(opened.Day!.OpenCount).IsEqualTo(1);
    }
}
