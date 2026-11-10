using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class RecurrenceRpcTests
{
    [Test]
    public async Task Template_RecurrenceDefault_RoundTripsAndInstantiates()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var templates = new TemplateRpcService(new TemplateCommands(storage.Store, ids, clock));
        var rule = new RecurrenceRuleDto("FixedSchedule", "Week", 1, "2000-01-01", null, "UTC", [], null, null, null, null, "Never", null, null);
        var payload = new TemplatePayloadResponse("Routine", "", "Normal", null, "Personal", 1, null, null, null, null, null, [], rule);
        var saved = await templates.SaveAsync(new(Guid.NewGuid().ToString(), null, null, "Routine template", payload), CancellationToken.None);
        await Assert.That(saved.Template!.Payload.RecurrenceDefault!.Frequency).IsEqualTo("Week");
        var created = await templates.InstantiateAsync(new(Guid.NewGuid().ToString(), saved.Template.Id, null), CancellationToken.None);
        var taskCommands = new TaskCommands(storage.Store, ids, clock);
        var detail = await new RecurrenceCommands(storage.Store, ids, clock).GetForTaskAsync(created.Task!.Id);
        await Assert.That(detail!.Rule.Frequency).IsEqualTo(Chaos.Domain.Recurrence.RecurrenceFrequency.Week);
        await Assert.That(detail.Occurrences.Count).IsEqualTo(1);
    }

    [Test]
    public async Task StartCompleteAndRestart_PreservesOccurrenceHistory()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        string taskId;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None))
        {
            var ids = new SequentialIdGenerator();
            var tasks = new TaskCommands(storage.Store, ids, clock);
            var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
            taskId = task.Id;
            var rpc = new RecurrenceRpcService(new RecurrenceCommands(storage.Store, ids, clock), tasks);
            var rule = new RecurrenceRuleDto("FixedSchedule", "Day", 1, "2026-11-01", null, "UTC", [], null, null, null, null, "Count", 2, null);
            var preview = await rpc.PreviewAsync(new(rule, "2026-11-30", 10), CancellationToken.None);
            await Assert.That(preview.Slots.Count).IsEqualTo(2);
            var started = await rpc.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version.ToString(), rule), CancellationToken.None);
            await Assert.That(started.Ok).IsTrue();
            await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, long.Parse(started.Task!.Version), Chaos.Domain.Tasks.TaskItemStatus.Done, null));
        }
        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var ids2 = new SequentialIdGenerator();
        var commands = new TaskCommands(reopened.Store, ids2, clock);
        var view = await new RecurrenceRpcService(new RecurrenceCommands(reopened.Store, ids2, clock), commands).GetAsync(new(taskId), CancellationToken.None);
        await Assert.That(view.Ok).IsTrue();
        await Assert.That(view.Occurrences.Count).IsEqualTo(2);
        await Assert.That(view.Occurrences.Count(x => x.State == "Active")).IsEqualTo(1);
    }
}
