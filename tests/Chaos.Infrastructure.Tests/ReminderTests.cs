using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Domain.Reminders;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests;

/// <summary>Reminder engine and outside-person follow-up against real SQLite with a fake clock and presenter (PLAN §15 WP10 tests).</summary>
public sealed class ReminderTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A presenter that records calls; it can schedule with a pretend OS, refuse, or throw.</summary>
    private sealed class FakeAdapter(bool schedules = false) : INotificationAdapter
    {
        public List<NotificationRequest> Scheduled { get; } = [];
        public List<string> Cancelled { get; } = [];
        public bool FailNextSchedule { get; set; }

        public NotificationCapabilities Capabilities { get; } = new("Fake", schedules, true, NotificationPermission.Granted, "Test presenter.");

        public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct)
        {
            if (FailNextSchedule)
            {
                FailNextSchedule = false;
                throw new InvalidOperationException("The notification platform is unavailable.");
            }

            Scheduled.Add(request);
            return Task.FromResult(new NotificationScheduleResult(true, $"os-{Scheduled.Count}", null));
        }

        public Task CancelAsync(string osNotificationId, CancellationToken ct)
        {
            Cancelled.Add(osNotificationId);
            return Task.CompletedTask;
        }

        public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(NotificationShowOutcome.InApp);
    }

    private sealed class Host : IDisposable
    {
        public required ChaosStorageHandle Storage { get; init; }
        public required SettableClock Clock { get; init; }
        public required FakeAdapter Adapter { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required ReminderCommands Reminders { get; init; }
        public required WaitingCommands Waiting { get; init; }
        public required DiaryCommands Diary { get; init; }
        public required UndoCommands Undo { get; init; }
        public required TaskQueries Queries { get; init; }
        public required MyDayCommands Days { get; init; }

        public static async Task<Host> StartAsync(string? root = null, SettableClock? clock = null, FakeAdapter? adapter = null)
        {
            root ??= TestPaths.NewDataRoot();
            clock ??= new SettableClock(Morning);
            adapter ??= new FakeAdapter();
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            Execute(storage.DatabasePath, "UPDATE Profiles SET ZoneId = 'UTC', DefaultReminderTime = '09:00:00';");
            var tasks = new TaskCommands(storage.Store, ids, clock);
            return new Host
            {
                Storage = storage,
                Clock = clock,
                Adapter = adapter,
                Tasks = tasks,
                Reminders = new ReminderCommands(storage.Store, ids, clock, tasks, adapter),
                Waiting = new WaitingCommands(storage.Store, ids, clock),
                Diary = new DiaryCommands(storage.Store, ids, clock),
                Undo = new UndoCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
                Days = new MyDayCommands(storage.Store, ids, clock),
            };
        }

        public async Task<TaskRecord> TaskAsync(string title, DateOnly? due = null, TimeOnly? dueTime = null, DateOnly? planned = null)
        {
            var task = (await Tasks.CaptureAsync(new(Op(), "", title, null))).Task;
            return due is null && planned is null ? task : await EditDatesAsync(task, due, dueTime, planned);
        }

        public async Task<TaskRecord> EditDatesAsync(TaskRecord task, DateOnly? due, TimeOnly? dueTime = null, DateOnly? planned = null) =>
            (await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, task.Title, task.Notes, planned, null, null,
                due, dueTime, dueTime is null ? null : "UTC", TaskPriority.Normal, null, CapacityCategory.Personal))).Task;

        public async Task<TaskRecord> CurrentAsync(string taskId) => (await Tasks.GetAsync(taskId))!;

        public Task<IReadOnlyList<NotificationJobRecord>> JobsAsync(string taskId) =>
            Storage.Store.QueryAsync<IReadOnlyList<NotificationJobRecord>>(async (session, ct) =>
            {
                var rules = await session.ReminderRules.ListByTaskAsync(taskId, ct);
                var follow = await session.Waiting.FindActiveFollowUpAsync(taskId, ct);
                var all = new List<NotificationJobRecord>();
                foreach (var rule in rules) all.AddRange(await session.Notifications.ListBySourceAsync(NotificationSourceKind.ReminderRule, rule.Id, ct));
                if (follow is not null) all.AddRange(await session.Notifications.ListBySourceAsync(NotificationSourceKind.FollowUp, follow.Id, ct));
                return all;
            });

        public async Task<NotificationJobRecord?> PendingAsync(string taskId) =>
            (await JobsAsync(taskId)).SingleOrDefault(job => job.State == NotificationJobState.Pending);

        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Storage.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public void Dispose() => Storage.Dispose();
    }

    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static string Op() => Guid.NewGuid().ToString();

    private static ReminderRuleInput BeforeDue(int minutes, int? insistent = null) => new(ReminderAnchor.Due, -minutes, null, insistent);

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Test]
    public async Task AddRule_RelativeToTimedDue_SchedulesOnePendingDeliveryAtTheTarget()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Dentist", Today.AddDays(2), new TimeOnly(14, 0));

        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(60)));

        var pending = await host.PendingAsync(task.Id);
        await Assert.That(pending!.TargetAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 12, 13, 0, 0, TimeSpan.Zero));
        await Assert.That(pending.DeliverAtUtc).IsEqualTo(pending.TargetAtUtc);
        await Assert.That(pending.Generation).IsEqualTo(1L);
        var views = await host.Reminders.ListForTaskAsync(task.Id);
        await Assert.That(views.Single().NextDeliveryAtUtc).IsEqualTo(pending.DeliverAtUtc);
    }

    [Test]
    public async Task AddRule_DateOnlyDue_UsesTheDefaultReminderTimeNotMidnightUtc()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Rent", Today.AddDays(3));

        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0)));

        await Assert.That((await host.PendingAsync(task.Id))!.TargetAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task ChangedAnchor_ReplacesTheDelivery_AndAnOldCallbackIsStale()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Report", Today, new TimeOnly(10, 0));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(60)));
        var delivered = (await host.Reminders.DeliverDueAsync()).Single();

        await host.EditDatesAsync(await host.CurrentAsync(task.Id), Today.AddDays(1), new TimeOnly(10, 0));
        var pending = await host.PendingAsync(task.Id);
        var stale = await host.Reminders.ActAsync(new NotificationActionCommand(Op(), delivered.Job.Id, delivered.Job.Generation, NotificationAction.Done));

        await Assert.That(pending!.Generation).IsEqualTo(2L);
        await Assert.That(pending.TargetAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(stale.Outcome).IsEqualTo(NotificationActionOutcome.Stale);
        await Assert.That((await host.CurrentAsync(task.Id)).Status).IsEqualTo(TaskItemStatus.NotStarted);
    }

    [Test]
    public async Task RemovedAnchor_SuspendsTheRuleAndCancelsItsDelivery()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Visa", Today.AddDays(5));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(1440)));

        await host.EditDatesAsync(await host.CurrentAsync(task.Id), null);

        await Assert.That(await host.PendingAsync(task.Id)).IsNull();
        await Assert.That((await host.Reminders.ListForTaskAsync(task.Id)).Single().Suspension).IsEqualTo(ReminderSuspension.NoDueDate);
    }

    [Test]
    public async Task TwoReminders_OnOneTask_EachGetTheirOwnDelivery()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Flight", Today.AddDays(1), new TimeOnly(18, 0));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(1440)));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(120)));

        var pending = (await host.JobsAsync(task.Id)).Where(job => job.State == NotificationJobState.Pending).Select(job => job.TargetAtUtc).Order().ToList();

        await Assert.That(pending).IsEquivalentTo([new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 11, 16, 0, 0, TimeSpan.Zero)]);
    }

    [Test]
    public async Task Complete_CancelsPendingDeliveries_AndUndoRestoresThem()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Pay bill", Today.AddDays(2));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0)));
        var completion = Op();

        await host.Tasks.SetStatusAsync(new SetTaskStatusCommand(completion, task.Id, (await host.CurrentAsync(task.Id)).Version, TaskItemStatus.Done, null));
        var afterDone = await host.PendingAsync(task.Id);
        await host.Undo.UndoAsync(new UndoOperationCommand(Op(), completion));
        var afterUndo = await host.PendingAsync(task.Id);

        await Assert.That(afterDone).IsNull();
        await Assert.That(afterUndo!.TargetAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task QuietHours_CrossingMidnight_HoldDeliveryUntilTheyEnd_KeepingTheTarget()
    {
        using var host = await Host.StartAsync();
        var settings = await host.Reminders.GetSettingsAsync();
        await host.Reminders.UpdateSettingsAsync(new UpdateReminderSettingsCommand(Op(), settings.Version, new TimeOnly(9, 0), true, new TimeOnly(22, 0), new TimeOnly(7, 0)));
        var task = await host.TaskAsync("Late call");
        var target = new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.Zero);

        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, new ReminderRuleInput(ReminderAnchor.Absolute, null, target, null)));
        host.Clock.Now = target.AddMinutes(5);
        var duringQuiet = await host.Reminders.DeliverDueAsync();
        host.Clock.Now = new DateTimeOffset(2026, 11, 11, 7, 0, 0, TimeSpan.Zero);
        var afterQuiet = await host.Reminders.DeliverDueAsync();

        await Assert.That(duringQuiet.Count).IsEqualTo(0);
        await Assert.That(afterQuiet.Single().Job.TargetAtUtc).IsEqualTo(target);
        await Assert.That(afterQuiet.Single().Job.DeliverAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 11, 7, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task ChangingQuietHours_ReschedulesPendingDeliveries()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Night task");
        var target = new DateTimeOffset(2026, 11, 10, 23, 0, 0, TimeSpan.Zero);
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, new ReminderRuleInput(ReminderAnchor.Absolute, null, target, null)));
        var before = await host.PendingAsync(task.Id);

        var settings = await host.Reminders.GetSettingsAsync();
        await host.Reminders.UpdateSettingsAsync(new UpdateReminderSettingsCommand(Op(), settings.Version, new TimeOnly(9, 0), true, new TimeOnly(22, 0), new TimeOnly(6, 30)));
        var after = await host.PendingAsync(task.Id);

        await Assert.That(before!.DeliverAtUtc).IsEqualTo(target);
        await Assert.That(after!.DeliverAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 11, 6, 30, 0, TimeSpan.Zero));
        await Assert.That(after.Generation).IsEqualTo(before.Generation + 1);
    }

    [Test]
    public async Task Insistent_RepeatsOnARollingHorizon_CoalescesMissedRepeats_AndStopsWhenAcknowledged()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Medicine", Today, new TimeOnly(9, 0));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0, insistent: 5)));

        var first = (await host.Reminders.DeliverDueAsync()).Single();
        var repeat = await host.PendingAsync(task.Id);
        host.Clock.Now = Morning.AddHours(3); // asleep for hours: many repeats were missed
        var resumed = await host.Reminders.DeliverDueAsync();
        var alerts = await host.Reminders.AlertsAsync();
        var next = await host.PendingAsync(task.Id);
        await host.Reminders.ActAsync(new NotificationActionCommand(Op(), resumed.Single().Job.Id, resumed.Single().Job.Generation, NotificationAction.Dismiss));

        await Assert.That(repeat!.DeliverAtUtc).IsEqualTo(Morning.AddMinutes(5));
        await Assert.That(resumed.Count).IsEqualTo(1);
        await Assert.That(alerts.Count).IsEqualTo(1);
        await Assert.That(alerts.Single().Job.Id).IsEqualTo(resumed.Single().Job.Id);
        await Assert.That(alerts.Single().Late).IsTrue();
        await Assert.That((await host.JobsAsync(task.Id)).Single(job => job.Id == first.Job.Id).ActionTaken).IsEqualTo("replaced");
        await Assert.That(next!.DeliverAtUtc).IsEqualTo(Morning.AddHours(3).AddMinutes(5));
        await Assert.That(await host.PendingAsync(task.Id)).IsNull();
    }

    [Test]
    public async Task Snooze_DeliversAgainLater_KeepingTheTargetAndTheDueDate()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Call bank", Today, new TimeOnly(9, 0));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0)));
        var delivered = (await host.Reminders.DeliverDueAsync()).Single();

        var snoozed = await host.Reminders.ActAsync(new NotificationActionCommand(Op(), delivered.Job.Id, delivered.Job.Generation, NotificationAction.Snooze, SnoozeChoice.OneHour));
        host.Clock.Now = Morning.AddMinutes(30);
        var early = await host.Reminders.DeliverDueAsync();
        host.Clock.Now = Morning.AddHours(1);
        var again = (await host.Reminders.DeliverDueAsync()).Single();
        var current = await host.CurrentAsync(task.Id);

        await Assert.That(snoozed.Outcome).IsEqualTo(NotificationActionOutcome.Snoozed);
        await Assert.That(snoozed.SnoozedUntilUtc).IsEqualTo(Morning.AddHours(1));
        await Assert.That(early.Count).IsEqualTo(0);
        await Assert.That(again.Job.TargetAtUtc).IsEqualTo(delivered.Job.TargetAtUtc);
        await Assert.That(again.Job.Reason).IsEqualTo(NotificationReason.Snoozed);
        await Assert.That(current.DueDate).IsEqualTo(Today);
        await Assert.That(current.DueTime).IsEqualTo(new TimeOnly(9, 0));
    }

    [Test]
    public async Task DoneAction_CompletesThroughTheTaskCommandExactlyOnce()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Submit form", Today, new TimeOnly(9, 0));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0)));
        var delivered = (await host.Reminders.DeliverDueAsync()).Single();
        var click = new NotificationActionCommand(Op(), delivered.Job.Id, delivered.Job.Generation, NotificationAction.Done);

        var first = await host.Reminders.ActAsync(click);
        var completedVersion = (await host.CurrentAsync(task.Id)).Version;
        var retried = await host.Reminders.ActAsync(click);
        var secondButton = await host.Reminders.ActAsync(click with { OperationId = Op() });
        var history = await host.Storage.Store.QueryAsync((session, ct) => session.History.ListByTaskAsync(task.Id, ct));

        await Assert.That(first.Outcome).IsEqualTo(NotificationActionOutcome.Completed);
        await Assert.That(retried.Replayed).IsTrue();
        await Assert.That(secondButton.Outcome).IsEqualTo(NotificationActionOutcome.AlreadyHandled);
        await Assert.That((await host.CurrentAsync(task.Id)).Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That((await host.CurrentAsync(task.Id)).Version).IsEqualTo(completedVersion);
        await Assert.That(history.Count(entry => entry.EventKind == "task.completed")).IsEqualTo(1);
    }

    [Test]
    public async Task DoneAction_WithOpenSubtasks_AsksForTheChoiceBeforeChangingAnything()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Pack", Today, new TimeOnly(9, 0));
        await new SubtaskCommands(host.Storage.Store, new GuidIds(), host.Clock).AddAsync(new AddSubtaskCommand(Op(), task.Id, "Charger", null));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0)));
        var delivered = (await host.Reminders.DeliverDueAsync()).Single();

        AppException? refused = null;
        try { await host.Reminders.ActAsync(new NotificationActionCommand(Op(), delivered.Job.Id, delivered.Job.Generation, NotificationAction.Done)); }
        catch (AppException error) { refused = error; }
        var kept = await host.Reminders.ActAsync(new NotificationActionCommand(Op(), delivered.Job.Id, delivered.Job.Generation, NotificationAction.Done, OpenSubtaskChoice: OpenSubtaskChoice.KeepOpen));

        await Assert.That(refused!.Fields.Single().FieldPath).IsEqualTo("openSubtaskChoice");
        await Assert.That(kept.Outcome).IsEqualTo(NotificationActionOutcome.Completed);
        await Assert.That((await host.CurrentAsync(task.Id)).Status).IsEqualTo(TaskItemStatus.Done);
    }

    [Test]
    public async Task Restart_DeliversWhatWasMissedOnce_AndMarksItLate()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new SettableClock(Morning);
        string taskId;
        using (var host = await Host.StartAsync(root, clock))
        {
            var task = await host.TaskAsync("Water plants", Today, new TimeOnly(12, 0));
            taskId = task.Id;
            await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(0)));
        }

        clock.Now = Morning.AddDays(1);
        using (var reopened = await Host.StartAsync(root, clock))
        {
            await reopened.Reminders.ResyncAllAsync();
            var delivered = await reopened.Reminders.DeliverDueAsync();
            var again = await reopened.Reminders.DeliverDueAsync();
            var alert = (await reopened.Reminders.AlertsAsync()).Single();

            await Assert.That(delivered.Single().Job.TaskId).IsEqualTo(taskId);
            await Assert.That(again.Count).IsEqualTo(0);
            await Assert.That(alert.Late).IsTrue();
        }
    }

    [Test]
    public async Task OsReconciliation_SchedulesFutureDeliveries_CancelsReplacedOnes_AndRetriesFailures()
    {
        var adapter = new FakeAdapter(schedules: true) { FailNextSchedule = true };
        using var host = await Host.StartAsync(adapter: adapter);
        var task = await host.TaskAsync("Renew passport", Today.AddDays(3), new TimeOnly(10, 0));
        await host.Reminders.AddAsync(new AddReminderCommand(Op(), task.Id, BeforeDue(60)));

        var failed = await host.Reminders.ReconcileOsAsync();
        var failedJob = await host.PendingAsync(task.Id);
        var retried = await host.Reminders.ReconcileOsAsync();
        var scheduledJob = await host.PendingAsync(task.Id);
        await host.EditDatesAsync(await host.CurrentAsync(task.Id), Today.AddDays(4), new TimeOnly(10, 0));
        var replaced = await host.Reminders.ReconcileOsAsync();

        await Assert.That(failed.Failed).IsEqualTo(1);
        await Assert.That(failedJob!.OsState).IsEqualTo(NotificationOsState.Failed);
        await Assert.That(failedJob.OsError).IsEqualTo("The notification platform is unavailable.");
        await Assert.That(retried.Scheduled).IsEqualTo(1);
        await Assert.That(scheduledJob!.OsNotificationId).IsEqualTo("os-1");
        await Assert.That(replaced.Cancelled).IsEqualTo(1);
        await Assert.That(replaced.Scheduled).IsEqualTo(1);
        await Assert.That(adapter.Cancelled).IsEquivalentTo(["os-1"]);
        await Assert.That(adapter.Scheduled.Last().Generation).IsEqualTo(2L);
    }

    [Test]
    public async Task Waiting_FixedFollowUp_AppearsInMyDayPrivately_AndIsNotified()
    {
        var adapter = new FakeAdapter();
        using var host = await Host.StartAsync(adapter: adapter);
        var task = await host.TaskAsync("Contract");
        var followAt = new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

        var set = await host.Waiting.SetAsync(new SetWaitingCommand(Op(), task.Id, task.Version, "  Omar  ", new FollowUpSpec(FollowUpKind.Fixed, followAt, null, null)));
        var dueToday = await host.Waiting.DueAsync(null);
        var dueYesterday = await host.Waiting.DueAsync(Today.AddDays(-1));
        host.Clock.Now = followAt;
        var delivered = (await host.Reminders.DeliverDueAsync()).Single();

        await Assert.That(set.View.Task.Status).IsEqualTo(TaskItemStatus.WaitingOnSomeone);
        await Assert.That(set.View.Waiting!.Person).IsEqualTo("Omar");
        await Assert.That(set.View.FollowUpDueAtUtc).IsEqualTo(followAt);
        await Assert.That(dueToday.Single().Person).IsEqualTo("Omar");
        await Assert.That(dueToday.Single().Overdue).IsFalse();
        await Assert.That(dueYesterday.Count).IsEqualTo(0);
        await Assert.That(delivered.Request.IsFollowUp).IsTrue();
        await Assert.That(delivered.Request.Body).Contains("Follow up with Omar");
        await Assert.That(adapter.Scheduled.Count).IsEqualTo(0); // nothing leaves the device
    }

    [Test]
    [Arguments(FollowUpKind.AfterWaiting, 2, DurationUnit.Days, "2026-11-12T09:00:00Z")]
    [Arguments(FollowUpKind.AfterWaiting, 3, DurationUnit.Hours, "2026-11-10T12:00:00Z")]
    [Arguments(FollowUpKind.BeforeDue, 1, DurationUnit.Days, "2026-11-14T17:00:00Z")]
    [Arguments(FollowUpKind.AfterInactivity, 4, DurationUnit.Days, "2026-11-14T09:00:00Z")]
    public async Task FollowUpRules_ComputeTheirDueInstant(FollowUpKind kind, int amount, DurationUnit unit, string expected)
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Quote", Today.AddDays(5), new TimeOnly(17, 0));

        var set = await host.Waiting.SetAsync(new SetWaitingCommand(Op(), task.Id, task.Version, "Lina", new FollowUpSpec(kind, null, amount, unit)));

        await Assert.That(set.View.FollowUpDueAtUtc).IsEqualTo(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That((await host.PendingAsync(task.Id))!.TargetAtUtc).IsEqualTo(set.View.FollowUpDueAtUtc!.Value);
    }

    [Test]
    public async Task InactivityFollowUp_ResetsOnDiaryUpdate_ButNotOnViewingOrMyDayReorder()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Invoice");
        var other = await host.TaskAsync("Other");
        await host.Waiting.SetAsync(new SetWaitingCommand(Op(), task.Id, task.Version, "Supplier", new FollowUpSpec(FollowUpKind.AfterInactivity, null, 3, DurationUnit.Days)));
        var initial = (await host.Waiting.GetAsync(task.Id)).FollowUpDueAtUtc;

        host.Clock.Now = Morning.AddDays(1);
        await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Waiting });
        await host.Tasks.GetDetailAsync(task.Id);
        await host.Days.AddAsync(new DayItemCommand(Op(), Today.AddDays(1), task.Id));
        await host.Days.AddAsync(new DayItemCommand(Op(), Today.AddDays(1), other.Id));
        await host.Days.MoveAsync(new DayMoveCommand(Op(), Today.AddDays(1), task.Id, 1));
        await host.Reminders.DeliverDueAsync();
        var afterViewing = (await host.Waiting.GetAsync(task.Id)).FollowUpDueAtUtc;

        host.Clock.Now = Morning.AddDays(2);
        await host.Diary.AddAsync(new AddDiaryEntryCommand(Op(), task.Id, "They said next week.", null));
        var afterDiary = (await host.Waiting.GetAsync(task.Id)).FollowUpDueAtUtc;

        await Assert.That(initial).IsEqualTo(Morning.AddDays(3));
        await Assert.That(afterViewing).IsEqualTo(initial);
        await Assert.That(afterDiary).IsEqualTo(Morning.AddDays(5));
        await Assert.That((await host.PendingAsync(task.Id))!.TargetAtUtc).IsEqualTo(Morning.AddDays(5));
    }

    [Test]
    public async Task LogFollowUp_WritesAPrivateDiaryUpdate_AndResolvesAOneOffFollowUp()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Lease");
        await host.Waiting.SetAsync(new SetWaitingCommand(Op(), task.Id, task.Version, "Landlord", new FollowUpSpec(FollowUpKind.Fixed, Morning.AddHours(2), null, null)));

        var logged = await host.Waiting.LogAsync(new LogFollowUpCommand(Op(), task.Id, "Called, will answer Friday."));
        var diary = await host.Diary.ListAsync(task.Id, includeDeleted: false);

        await Assert.That(diary.Single().RawMarkdown).IsEqualTo("Followed up with Landlord: Called, will answer Friday.");
        await Assert.That(logged.View.FollowUp).IsNull();
        await Assert.That(logged.View.Waiting!.Person).IsEqualTo("Landlord");
        await Assert.That(await host.PendingAsync(task.Id)).IsNull();
    }

    [Test]
    public async Task ClearingWaitingOrCompleting_ResolvesTheFollowUp_KeepingHistory_AndUndoBringsItBack()
    {
        using var host = await Host.StartAsync();
        var first = await host.TaskAsync("Permit");
        var second = await host.TaskAsync("Refund");
        await host.Waiting.SetAsync(new SetWaitingCommand(Op(), first.Id, first.Version, "City office", new FollowUpSpec(FollowUpKind.AfterWaiting, null, 1, DurationUnit.Days)));
        await host.Waiting.SetAsync(new SetWaitingCommand(Op(), second.Id, second.Version, "Shop", new FollowUpSpec(FollowUpKind.AfterWaiting, null, 1, DurationUnit.Days)));

        var clear = Op();
        var cleared = await host.Waiting.ClearAsync(new ClearWaitingCommand(clear, first.Id, (await host.CurrentAsync(first.Id)).Version));
        await host.Tasks.SetStatusAsync(new SetTaskStatusCommand(Op(), second.Id, (await host.CurrentAsync(second.Id)).Version, TaskItemStatus.Done, null));
        var completedView = await host.Waiting.GetAsync(second.Id);
        var resolvedRows = host.Scalar("SELECT COUNT(*) FROM FollowUps WHERE ResolvedAtUtc IS NOT NULL;");
        await host.Undo.UndoAsync(new UndoOperationCommand(Op(), clear));
        var restored = await host.Waiting.GetAsync(first.Id);

        await Assert.That(cleared.View.Task.Status).IsEqualTo(TaskItemStatus.InProgress);
        await Assert.That(cleared.View.Waiting).IsNull();
        await Assert.That(completedView.FollowUp).IsNull();
        await Assert.That(resolvedRows).IsEqualTo(2L);
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM NotificationJobs WHERE State = 0;")).IsEqualTo(1L);
        await Assert.That(restored.Task.Status).IsEqualTo(TaskItemStatus.WaitingOnSomeone);
        await Assert.That(restored.Waiting!.Person).IsEqualTo("City office");
        await Assert.That(restored.FollowUp).IsNotNull();
    }

    [Test]
    public async Task WaitingView_GroupsByPerson_AndSearchFindsThePersonsName()
    {
        using var host = await Host.StartAsync();
        foreach (var (title, person) in new[] { ("Quote", "Zainab"), ("Keys", "Adam"), ("Parts", "Zainab") })
        {
            var task = await host.TaskAsync(title);
            await host.Waiting.SetAsync(new SetWaitingCommand(Op(), task.Id, task.Version, person, null));
        }

        var unnamed = await host.TaskAsync("Unnamed wait");
        await host.Tasks.SetStatusAsync(new SetTaskStatusCommand(Op(), unnamed.Id, unnamed.Version, TaskItemStatus.WaitingOnSomeone, null));

        var waiting = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Waiting });
        var search = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "zain" });

        await Assert.That(string.Join("|", waiting.Rows.Select(row => row.WaitingPerson ?? "-"))).IsEqualTo("Adam|Zainab|Zainab|-");
        await Assert.That(search.Rows.Select(row => row.Task.Title).Order()).IsEquivalentTo(["Parts", "Quote"]);
        await Assert.That(search.Rows.All(row => row.Matches.Contains(SearchMatchKind.WaitingPerson))).IsTrue();
    }

    [Test]
    public async Task QuickAdd_WaitingWithoutPerson_FollowUpBecomesAReminder()
    {
        using var host = await Host.StartAsync();
        var capture = new Chaos.Application.Capture.CaptureCommands(host.Storage.Store, new GuidIds(), host.Clock);
        var created = await capture.CreateAsync(new Chaos.Application.Capture.CreateCapturedTasksCommand(Op(),
            [new Chaos.Application.Capture.CaptureTaskDraft { Title = "Check parcel", FollowUpDate = Today.AddDays(1) }]));
        var task = created.Tasks.Single();

        await Assert.That(task.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await Assert.That((await host.Reminders.ListForTaskAsync(task.Id)).Single().TargetAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
    }
}
