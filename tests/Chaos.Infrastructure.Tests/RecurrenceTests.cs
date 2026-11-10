using Chaos.Application.Tasks;
using Chaos.Application.History;
using Chaos.Application.Abstractions;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Tests;

public sealed class RecurrenceTests
{
    [Test]
    public async Task EditOccurrence_RecordsExceptionAndDoesNotChangeNextTemplate()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Original", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        var start = await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        var edited = (await tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString(), task.Id, started.Version, "Only this time", "",
            started.PlannedDate, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal))).Task;
        using var connection = new SqliteConnectionSource(storage.DatabasePath).Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM RecurrenceExceptions WHERE SeriesId=$id AND Kind=2";
        query.Parameters.AddWithValue("$id", start.Series.Id);
        await Assert.That(Convert.ToInt64(await query.ExecuteScalarAsync())).IsEqualTo(1L);
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, edited.Version, TaskItemStatus.Done, null));
        var successor = (await tasks.ListInboxAsync()).Single(x => x.Id != task.Id);
        await Assert.That(successor.Title).IsEqualTo("Original");
    }

    [Test]
    public async Task ReviseFuture_ChangesScheduleWithoutChangingNominalIdentity()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var daily = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        var start = await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, daily));
        var started = (await tasks.GetAsync(task.Id))!;
        var weekly = daily with { Frequency = RecurrenceFrequency.Week, AnchorDate = new(2026, 11, 11), Weekdays = [DayOfWeek.Friday] };
        var reviseOperation = Guid.NewGuid().ToString();
        var revised = await recurrence.ReviseFutureAsync(new(reviseOperation, task.Id, started.Version, weekly));
        var detail = (await recurrence.GetForTaskAsync(task.Id))!;
        await Assert.That(detail.Occurrences.Single().NominalKey).IsEqualTo(start.Occurrence.NominalKey);
        await Assert.That(detail.Occurrences.Single().ScheduleKey).IsEqualTo("2026-11-13T00:00:00");
        await Assert.That(revised.Task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 13));
        await new UndoCommands(storage.Store, ids, clock).UndoAsync(new(Guid.NewGuid().ToString(), reviseOperation));
        var restored = (await tasks.GetAsync(task.Id))!;
        var restoredDetail = (await recurrence.GetForTaskAsync(task.Id))!;
        await Assert.That(restored.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 10));
        await Assert.That(restoredDetail.Series.Revision).IsEqualTo(1L);
        revised = await recurrence.ReviseFutureAsync(new(Guid.NewGuid().ToString(), task.Id, restored.Version, weekly));
        await Assert.That((await recurrence.GetForTaskAsync(task.Id))!.Series.Revision).IsEqualTo(3L);
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, revised.Task.Version, TaskItemStatus.Done, null));
        var successor = (await tasks.ListInboxAsync()).Single(x => x.Id != task.Id);
        await Assert.That(successor.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 20));
    }

    [Test]
    public async Task UndoEnd_AfterCompletion_RefusesToReopenSeries()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        var endOperation = Guid.NewGuid().ToString();
        await recurrence.EndAsync(new(endOperation, task.Id, started.Version));
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, started.Version, TaskItemStatus.Done, null));
        await Assert.That(async () => await new UndoCommands(storage.Store, ids, clock).UndoAsync(new(Guid.NewGuid().ToString(), endOperation))).Throws<AppException>();
        await Assert.That((await tasks.ListInboxAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Project_CountLimit_ReturnsReadOnlyCandidatesWithoutMaterializing()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Count, 3, null);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var projected = await recurrence.ProjectAsync(task.Id, new(2026, 12, 1), 10);
        await Assert.That(projected.Count).IsEqualTo(2);
        await Assert.That(projected.All(item => item.Projected)).IsTrue();
        await Assert.That((await tasks.ListInboxAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Complete_AfterCompletionTimed_ShiftsBothAnchorsByLocalCompletion()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 15, 30, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var edited = (await tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, "Repeat", "",
            new(2026, 11, 1), new(9, 0), "UTC", new(2026, 11, 2), new(10, 0), "UTC", TaskPriority.Normal, null, CapacityCategory.Personal))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.AfterCompletion, RecurrenceFrequency.Day, 1, new(2026, 11, 1), new(9, 0),
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        await new RecurrenceCommands(storage.Store, ids, clock).StartAsync(new(Guid.NewGuid().ToString(), task.Id, edited.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, started.Version, TaskItemStatus.Done, null));
        var successor = (await tasks.ListInboxAsync()).Single(x => x.Id != task.Id);
        await Assert.That(successor.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 11));
        await Assert.That(successor.PlannedTime).IsEqualTo(new TimeOnly(15, 30));
        await Assert.That(successor.DueDate).IsEqualTo(new DateOnly(2026, 11, 12));
        await Assert.That(successor.DueTime).IsEqualTo(new TimeOnly(16, 30));
    }

    [Test]
    public async Task Move_ThenUndo_PreservesNominalIdentityAndRemovesException()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        var start = await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        var operation = Guid.NewGuid().ToString();
        await recurrence.MoveAsync(new(operation, task.Id, started.Version, new(2026, 11, 15), null));
        var moved = await storage.Store.QueryAsync((s, ct) => s.Recurrence.GetOccurrenceAsync(start.Occurrence.Id, ct));
        await Assert.That(moved!.NominalKey).IsEqualTo(start.Occurrence.NominalKey);
        await Assert.That((await tasks.GetAsync(task.Id))!.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 15));
        using var connection = new SqliteConnectionSource(storage.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM RecurrenceExceptions WHERE SeriesId=$id AND Kind=1";
        command.Parameters.AddWithValue("$id", start.Series.Id);
        await Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync())).IsEqualTo(1L);
        await new UndoCommands(storage.Store, ids, clock).UndoAsync(new(Guid.NewGuid().ToString(), operation));
        await Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync())).IsEqualTo(0L);
        await Assert.That((await tasks.GetAsync(task.Id))!.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 10));
    }

    [Test]
    public async Task UndoCompletion_SuccessorHasDiary_RefusesAtomically()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 1), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        await new RecurrenceCommands(storage.Store, ids, clock).StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        var operation = Guid.NewGuid().ToString();
        await tasks.SetStatusAsync(new(operation, task.Id, started.Version, TaskItemStatus.Done, null));
        var successor = (await tasks.ListInboxAsync()).Single(x => x.Id != task.Id);
        await new DiaryCommands(storage.Store, ids, clock).AddAsync(new(Guid.NewGuid().ToString(), successor.Id, "A later note", null));
        await Assert.That(async () => await new UndoCommands(storage.Store, ids, clock).UndoAsync(new(Guid.NewGuid().ToString(), operation))).Throws<AppException>();
        await Assert.That((await tasks.GetAsync(task.Id))!.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(await tasks.GetAsync(successor.Id)).IsNotNull();
    }

    [Test]
    public async Task Skip_CountEnd_ConsumesTheLastOccurrence()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Count, 1, null);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        var start = await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        await recurrence.SkipAsync(new(Guid.NewGuid().ToString(), task.Id, started.Version));
        var occurrences = await storage.Store.QueryAsync((s, ct) => s.Recurrence.ListOccurrencesAsync(start.Series.Id, ct));
        await Assert.That(occurrences.Count).IsEqualTo(1);
        await Assert.That(occurrences.Single().State).IsEqualTo(Chaos.Application.Contracts.OccurrenceState.Skipped);
        await Assert.That((await tasks.GetAsync(task.Id))!.Status).IsEqualTo(TaskItemStatus.Cancelled);
        using var connection = new SqliteConnectionSource(storage.DatabasePath).Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM RecurrenceExceptions WHERE SeriesId=$id AND Kind=0";
        query.Parameters.AddWithValue("$id", start.Series.Id);
        await Assert.That(Convert.ToInt64(await query.ExecuteScalarAsync())).IsEqualTo(1L);
    }

    [Test]
    public async Task UndoCompletion_UntouchedSuccessor_RestoresOriginalOccurrence()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 1), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var start = await new RecurrenceCommands(storage.Store, ids, clock).StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        var operationId = Guid.NewGuid().ToString();
        await tasks.SetStatusAsync(new(operationId, task.Id, started.Version, TaskItemStatus.Done, null));
        await new UndoCommands(storage.Store, ids, clock).UndoAsync(new(Guid.NewGuid().ToString(), operationId));
        var occurrences = await storage.Store.QueryAsync((s, ct) => s.Recurrence.ListOccurrencesAsync(start.Series.Id, ct));
        await Assert.That((await tasks.GetAsync(task.Id))!.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await Assert.That(occurrences.Count).IsEqualTo(1);
        await Assert.That(occurrences.Single().State).IsEqualTo(Chaos.Application.Contracts.OccurrenceState.Active);
        var restored = (await tasks.GetAsync(task.Id))!;
        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, restored.Version, TaskItemStatus.Done, null));
        var afterRetry = await storage.Store.QueryAsync((s, ct) => s.Recurrence.ListOccurrencesAsync(start.Series.Id, ct));
        await Assert.That(afterRetry.Count).IsEqualTo(2);
        await Assert.That(afterRetry.Count(x => x.State == Chaos.Application.Contracts.OccurrenceState.Active)).IsEqualTo(1);
    }

    [Test]
    public async Task Complete_LateAndRetried_CreatesOnlyOneSuccessor()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var recurrence = new RecurrenceCommands(storage.Store, ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 1), null,
            "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var start = await recurrence.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version, rule));
        var started = (await tasks.GetAsync(task.Id))!;
        var operation = new SetTaskStatusCommand(Guid.NewGuid().ToString(), task.Id, started.Version, TaskItemStatus.Done, null);
        await tasks.SetStatusAsync(operation);
        var retry = await tasks.SetStatusAsync(operation);
        var rows = await tasks.ListInboxAsync();
        var occurrences = await storage.Store.QueryAsync((s, ct) => s.Recurrence.ListOccurrencesAsync(start.Series.Id, ct));
        await Assert.That(retry.Replayed).IsTrue();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.Single(x => x.Id != task.Id).PlannedDate).IsEqualTo(new DateOnly(2026, 11, 10));
        await Assert.That(occurrences.Count(x => x.State == Chaos.Application.Contracts.OccurrenceState.Active)).IsEqualTo(1);
    }
}
