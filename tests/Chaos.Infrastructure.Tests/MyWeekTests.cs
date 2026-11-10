using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests;

/// <summary>My Week against real SQLite (PLAN §15 WP9 tests). The profile's week starts on Sunday.</summary>
public sealed class MyWeekTests
{
    // Tuesday 10 November 2026; the week runs Sunday 8 – Saturday 14.
    private static readonly DateTimeOffset Morning = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly DateOnly WeekStart = new(2026, 11, 8);
    private static readonly DateOnly LastWeekStart = new(2026, 11, 1);

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class Host : IDisposable
    {
        public required ChaosStorageHandle Storage { get; init; }
        public required SettableClock Clock { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required MyDayCommands Days { get; init; }
        public required MyWeekCommands Weeks { get; init; }
        public required TaskQueries Queries { get; init; }

        public static async Task<Host> StartAsync(string? root = null, SettableClock? clock = null)
        {
            root ??= TestPaths.NewDataRoot();
            clock ??= new SettableClock(Morning);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            Execute(storage.DatabasePath, "UPDATE Profiles SET ZoneId = 'UTC', FirstWeekday = 0;");
            return new Host
            {
                Storage = storage,
                Clock = clock,
                Tasks = new TaskCommands(storage.Store, ids, clock),
                Days = new MyDayCommands(storage.Store, ids, clock),
                Weeks = new MyWeekCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
            };
        }

        public async Task<TaskRecord> TaskAsync(string title, DateOnly? planned = null, DateOnly? due = null, int? estimate = null, CapacityCategory category = CapacityCategory.Personal)
        {
            var task = (await Tasks.CaptureAsync(new(Op(), "", title, null))).Task;
            return (await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, title, "", planned, null, null, due, null, null, TaskPriority.Normal, estimate, category))).Task;
        }

        /// <summary>Starts the week's draft, saves <paramref name="draft"/> over it and confirms.</summary>
        public async Task<WeekOverview> PlanAsync(DateOnly weekStart, WeekDraft draft)
        {
            var started = (await Weeks.StartDraftAsync(new(Op(), weekStart))).Week;
            var saved = (await Weeks.SaveDraftAsync(new(Op(), weekStart, started.DraftRevision, draft))).Week;
            return (await Weeks.ConfirmAsync(new(Op(), weekStart, saved.DraftRevision))).Week;
        }

        public async Task<IReadOnlyList<string>> DayTitlesAsync(DateOnly day) =>
            [.. (await Queries.RunAsync(new TaskQuerySpec { View = TaskView.MyDay, Day = day })).Rows.Select(row => row.Task.Title)];

        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Storage.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose() => Storage.Dispose();
    }

    private static string Op() => Guid.NewGuid().ToString();

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static WeekDraft Draft(
        IReadOnlyList<WeekDraftAssignment>? assignments = null,
        IReadOnlyList<WeekDraftGoal>? goals = null,
        IReadOnlyList<WeekDraftLeftover>? leftovers = null,
        IReadOnlyList<WeekDraftOverride>? overrides = null) =>
        new(goals ?? [], assignments ?? [], leftovers ?? [], overrides ?? [], 5);

    private static WeekDraftGoal Goal(string title, params string[] taskIds) => new(Guid.NewGuid().ToString(), title, null, taskIds, WeekGoalOutcome.Pending);

    [Test]
    public async Task Confirm_SeedsTodayOnce_KeepsTaskDates_AndRemovalIsNotReseeded()
    {
        using var host = await Host.StartAsync();
        var report = await host.TaskAsync("Write report", planned: Today.AddDays(3), due: Today.AddDays(4), estimate: 60);
        var call = await host.TaskAsync("Call the bank", estimate: 15);

        var week = await host.PlanAsync(WeekStart, Draft([new(report.Id, Today), new(call.Id, Today.AddDays(1))]));
        await Assert.That(week.State).IsEqualTo(WeekPlanState.Confirmed);
        await Assert.That(week.ConfirmedRevision).IsEqualTo(1L);

        // The week assignment never changes the task's own planned/due dates.
        var saved = (await host.Tasks.GetAsync(report.Id))!;
        await Assert.That(saved.PlannedDate).IsEqualTo(Today.AddDays(3));
        await Assert.That(saved.DueDate).IsEqualTo(Today.AddDays(4));
        await Assert.That(saved.Version).IsEqualTo(report.Version);

        var opened = await host.Days.OpenAsync(null);
        await Assert.That(opened.OpenCount).IsEqualTo(1);
        await Assert.That(await host.DayTitlesAsync(Today)).IsEquivalentTo(["Write report"]);

        await host.Days.RemoveAsync(new(Op(), Today, report.Id));
        await host.Days.OpenAsync(null);
        await Assert.That(await host.DayTitlesAsync(Today)).IsEmpty();
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlans;")).IsEqualTo(1L);
        await Assert.That(host.Scalar("SELECT SeededWeekRevision FROM DayPlans;")).IsEqualTo(1L);

        // Tomorrow is seeded from the same confirmed week when it is first opened.
        await host.Days.OpenAsync(Today.AddDays(1));
        await Assert.That(await host.DayTitlesAsync(Today.AddDays(1))).IsEquivalentTo(["Call the bank"]);
    }

    [Test]
    public async Task DiscardDraft_LeavesTheConfirmedPlanExactlyAsItWas()
    {
        using var host = await Host.StartAsync();
        var a = await host.TaskAsync("Plan sprint", estimate: 30);
        var b = await host.TaskAsync("Pay invoices", estimate: 20);
        await host.PlanAsync(WeekStart, Draft([new(a.Id, Today.AddDays(1))], [Goal("Ship the sprint plan", a.Id)]));

        var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
        await Assert.That(started.Draft!.Assignments.Single().TaskId).IsEqualTo(a.Id);
        var edited = (await host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft([new(b.Id, Today.AddDays(2))])))).Week;
        await Assert.That(edited.Confirmed.Placements.Single().TaskId).IsEqualTo(a.Id);

        var cancelled = (await host.Weeks.DiscardDraftAsync(new(Op(), WeekStart, edited.DraftRevision))).Week;
        await Assert.That(cancelled.Draft).IsNull();
        await Assert.That(cancelled.ConfirmedRevision).IsEqualTo(1L);
        await Assert.That(cancelled.Confirmed.Placements.Select(item => (item.TaskId, item.Day))).IsEquivalentTo([(a.Id, (DateOnly?)Today.AddDays(1))]);
        await Assert.That(cancelled.Confirmed.Goals.Single().Title).IsEqualTo("Ship the sprint plan");
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM WeekPlanRevisions;")).IsEqualTo(1L);
    }

    [Test]
    public async Task Confirm_WithADeletedTask_RefusesEverything_IncludingLeftoverCancellation()
    {
        var clock = new SettableClock(Morning.AddDays(-7));
        using var host = await Host.StartAsync(clock: clock);
        var leftover = await host.TaskAsync("Old errand");
        await host.PlanAsync(LastWeekStart, Draft([new(leftover.Id, Today.AddDays(-6))]));

        clock.Now = Morning;
        var doomed = await host.TaskAsync("Soon deleted");
        var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
        await Assert.That(started.LookBack.UnfinishedTaskIds).IsEquivalentTo([leftover.Id]);
        var saved = (await host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision,
            Draft([new(doomed.Id, Today)], leftovers: [new(leftover.Id, LeftoverDecision.Cancel, null)])))).Week;
        await host.Tasks.DeleteAsync(new(Op(), doomed.Id, doomed.Version));

        var refused = await Assert.ThrowsAsync<AppException>(() => host.Weeks.ConfirmAsync(new(Op(), WeekStart, saved.DraftRevision)));
        await Assert.That(refused!.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Tasks.GetAsync(leftover.Id))!.Status).IsEqualTo(TaskItemStatus.NotStarted);
        var after = await host.Weeks.OpenAsync(null);
        await Assert.That(after.State).IsEqualTo(WeekPlanState.Draft);
        await Assert.That(after.Draft).IsNotNull();
        await Assert.That(host.Scalar($"SELECT COUNT(*) FROM WeekAssignments WHERE WeekPlanId = '{after.PlanId}';")).IsEqualTo(0L);
    }

    [Test]
    public async Task SaveDraft_StaleRevision_IsAVersionConflict()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Read");
        var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
        await host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft([new(task.Id, null)])));
        var stale = await Assert.ThrowsAsync<AppException>(() => host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft())));
        await Assert.That(stale!.Kind).IsEqualTo(AppErrorKind.VersionConflict);
    }

    [Test]
    public async Task Leftovers_CarryCancelLaterAndRemove_AreAppliedOnlyOnConfirm()
    {
        var clock = new SettableClock(Morning.AddDays(-7));
        using var host = await Host.StartAsync(clock: clock);
        var carry = await host.TaskAsync("Carry me", estimate: 25);
        var cancel = await host.TaskAsync("Cancel me");
        var later = await host.TaskAsync("Later me", planned: Today.AddDays(-6));
        var remove = await host.TaskAsync("Remove me");
        var done = await host.TaskAsync("Done already");
        await host.PlanAsync(LastWeekStart, Draft([new(carry.Id, Today.AddDays(-6)), new(cancel.Id, null), new(later.Id, Today.AddDays(-5)), new(remove.Id, null), new(done.Id, null)]));
        await host.Tasks.SetStatusAsync(new(Op(), done.Id, (await host.Tasks.GetAsync(done.Id))!.Version, TaskItemStatus.Done, null));

        clock.Now = Morning;
        var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
        await Assert.That(started.LookBack.HadPlan).IsTrue();
        await Assert.That(started.LookBack.AssignedCount).IsEqualTo(5);
        await Assert.That(started.LookBack.AssignedDone).IsEqualTo(1);
        await Assert.That(started.LookBack.UnfinishedTaskIds).IsEquivalentTo([carry.Id, cancel.Id, later.Id, remove.Id]);

        var laterDate = Today.AddDays(20);
        var saved = (await host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft(leftovers:
        [
            new(carry.Id, LeftoverDecision.Carry, null),
            new(cancel.Id, LeftoverDecision.Cancel, null),
            new(later.Id, LeftoverDecision.Later, laterDate),
            new(remove.Id, LeftoverDecision.Remove, null),
        ])))).Week;
        await Assert.That((await host.Tasks.GetAsync(cancel.Id))!.Status).IsEqualTo(TaskItemStatus.NotStarted);

        var week = (await host.Weeks.ConfirmAsync(new(Op(), WeekStart, saved.DraftRevision))).Week;
        await Assert.That(week.Confirmed.Placements.Select(item => (item.TaskId, item.Day, item.Origin))).IsEquivalentTo([(carry.Id, (DateOnly?)null, WeekAssignmentOrigin.Carried)]);
        await Assert.That(week.Confirmed.Pool.PlannedMinutes).IsEqualTo(25);
        await Assert.That((await host.Tasks.GetAsync(cancel.Id))!.Status).IsEqualTo(TaskItemStatus.Cancelled);
        await Assert.That((await host.Tasks.GetAsync(later.Id))!.PlannedDate).IsEqualTo(laterDate);
        var untouched = (await host.Tasks.GetAsync(remove.Id))!;
        await Assert.That(untouched.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await Assert.That(untouched.Version).IsEqualTo(remove.Version);
        await Assert.That(host.Scalar($"SELECT COUNT(*) FROM TaskHistory WHERE TaskId = '{cancel.Id}' AND EventKind = 'task.cancelled';")).IsEqualTo(1L);
    }

    [Test]
    public async Task Capacity_UnknownEstimates_ZeroAvailability_WorkAllowance_AndGoalsCountTasksOnce()
    {
        using var host = await Host.StartAsync();
        var work = await host.TaskAsync("Quarterly report", estimate: 300, category: CapacityCategory.Work);
        var unknown = await host.TaskAsync("Sort photos");
        var weekend = await host.TaskAsync("Fix the bike", estimate: 60);
        var saturday = WeekStart.AddDays(6);

        var week = await host.PlanAsync(WeekStart, Draft(
            [new(work.Id, Today), new(unknown.Id, Today), new(weekend.Id, saturday)],
            [Goal("Close the quarter", work.Id), Goal("Get the report out", work.Id)],
            overrides: [new(Today, 240, 120)]));

        var tuesday = week.Confirmed.Days.Single(day => day.Date == Today);
        await Assert.That(tuesday.IsOverride).IsTrue();
        await Assert.That(tuesday.Load.PlannedMinutes).IsEqualTo(300);
        await Assert.That(tuesday.Load.UnknownEstimates).IsEqualTo(1);
        await Assert.That(tuesday.Load.OverTotal).IsTrue();
        await Assert.That(tuesday.Load.OverWork).IsTrue();
        var sat = week.Confirmed.Days.Single(day => day.Date == saturday);
        await Assert.That(sat.Availability.TotalMinutes).IsEqualTo(0);
        await Assert.That(sat.Load.OverTotal).IsTrue();
        await Assert.That(week.Confirmed.Days.Sum(day => day.Load.PlannedMinutes)).IsEqualTo(360);
        await Assert.That(week.CheckIn!.OverloadedDays).IsEquivalentTo([Today, saturday]);

        await host.Tasks.SetStatusAsync(new(Op(), work.Id, (await host.Tasks.GetAsync(work.Id))!.Version, TaskItemStatus.Done, null));
        var after = await host.Weeks.OpenAsync(null);
        await Assert.That(after.Confirmed.Goals.All(goal => goal is { DoneCount: 1, CountedCount: 1 })).IsTrue();
        await Assert.That(after.Confirmed.Days.Single(day => day.Date == Today).Load.PlannedMinutes).IsEqualTo(0);
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM WeekAvailabilityOverrides;")).IsEqualTo(1L);
    }

    [Test]
    public async Task Reconfirm_ExistingDay_ShowsAComparison_PreservingManualAndRemovedChoices()
    {
        using var host = await Host.StartAsync();
        var seeded = await host.TaskAsync("Seeded task");
        var moved = await host.TaskAsync("Moved later");
        var manual = await host.TaskAsync("Picked by hand");
        var dropped = await host.TaskAsync("Removed by me");
        var added = await host.TaskAsync("New for today");
        await host.PlanAsync(WeekStart, Draft([new(seeded.Id, Today), new(moved.Id, Today), new(dropped.Id, Today)]));
        await host.Days.OpenAsync(null);
        await host.Days.AddAsync(new(Op(), Today, manual.Id));
        await host.Days.RemoveAsync(new(Op(), Today, dropped.Id));

        var week = await host.PlanAsync(WeekStart, Draft([new(seeded.Id, Today), new(moved.Id, Today.AddDays(2)), new(dropped.Id, Today), new(added.Id, Today)]));
        var pending = week.PendingReconciliation.Single();
        await Assert.That(pending.Date).IsEqualTo(Today);
        await Assert.That(pending.AddTaskIds).IsEquivalentTo([added.Id]);
        await Assert.That(pending.RemoveTaskIds).IsEquivalentTo([moved.Id]);
        await Assert.That(await host.DayTitlesAsync(Today)).IsEquivalentTo(["Seeded task", "Moved later", "Picked by hand"]);

        var offTheList = await Assert.ThrowsAsync<AppException>(() =>
            host.Weeks.ApplyReconciliationAsync(new(Op(), Today, pending.WeekPlanId, pending.Revision, [manual.Id], [])));
        await Assert.That(offTheList!.Kind).IsEqualTo(AppErrorKind.Validation);

        var applied = (await host.Weeks.ApplyReconciliationAsync(new(Op(), Today, pending.WeekPlanId, pending.Revision, [added.Id], [moved.Id]))).Week;
        await Assert.That(applied.PendingReconciliation).IsEmpty();
        await Assert.That(await host.DayTitlesAsync(Today)).IsEquivalentTo(["Seeded task", "Picked by hand", "New for today"]);
        await Assert.That(host.Scalar("SELECT SeededWeekRevision FROM DayPlans;")).IsEqualTo(2L);
    }

    [Test]
    public async Task SaveDraft_RefusesNewWorkOnEarlierDays_AndTheSameTaskTwice()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Laundry");
        var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
        var past = await Assert.ThrowsAsync<AppException>(() => host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft([new(task.Id, WeekStart)]))));
        await Assert.That(past!.Fields.Single().FieldPath).IsEqualTo("assignments[0].day");
        var twice = await Assert.ThrowsAsync<AppException>(() => host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft([new(task.Id, Today), new(task.Id, null)]))));
        await Assert.That(twice!.Fields.Single().FieldPath).IsEqualTo("assignments[1].taskId");
        var outside = await Assert.ThrowsAsync<AppException>(() => host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft([new(task.Id, WeekStart.AddDays(7))]))));
        await Assert.That(outside!.Fields.Single().FieldPath).IsEqualTo("assignments[0].day");
    }

    [Test]
    public async Task CheckIn_ListsSlippedAssignments_AndGoalOutcomeIsManual()
    {
        var clock = new SettableClock(Morning);
        using var host = await Host.StartAsync(clock: clock);
        var slip = await host.TaskAsync("Slipping task");
        var week = await host.PlanAsync(WeekStart, Draft([new(slip.Id, Today)], [Goal("Rest more")]));
        var goal = week.Confirmed.Goals.Single();
        await Assert.That(goal.CountedCount).IsEqualTo(0);

        var assessed = (await host.Weeks.SetGoalOutcomeAsync(new(Op(), goal.Id, goal.Version, WeekGoalOutcome.Achieved))).Week;
        await Assert.That(assessed.Confirmed.Goals.Single().Outcome).IsEqualTo(WeekGoalOutcome.Achieved);

        clock.Now = Morning.AddDays(2);
        var midweek = await host.Weeks.OpenAsync(null);
        await Assert.That(midweek.CheckIn!.Midweek).IsTrue();
        await Assert.That(midweek.CheckIn.Slipped.Select(item => (item.TaskId, item.Day))).IsEquivalentTo([(slip.Id, Today)]);
        // The check-in changes nothing by itself.
        await Assert.That((await host.Tasks.GetAsync(slip.Id))!.Version).IsEqualTo(slip.Version);
    }

    [Test]
    public async Task ChangeWeekStart_RequiresRemap_KeepsHistory_AndMovesWorkIntoNewDrafts()
    {
        var clock = new SettableClock(Morning.AddDays(-7));
        var root = TestPaths.NewDataRoot();
        using var host = await Host.StartAsync(root, clock);
        var old = await host.TaskAsync("Last week");
        await host.PlanAsync(LastWeekStart, Draft([new(old.Id, Today.AddDays(-6))]));

        clock.Now = Morning;
        var wednesday = await host.TaskAsync("Wednesday work");
        var saturday = await host.TaskAsync("Saturday work");
        var pool = await host.TaskAsync("Some day");
        await host.PlanAsync(WeekStart, Draft([new(wednesday.Id, Today.AddDays(1)), new(saturday.Id, WeekStart.AddDays(6)), new(pool.Id, null)], [Goal("Keep going")]));

        var preview = await host.Weeks.PreviewWeekStartAsync(DayOfWeek.Monday);
        await Assert.That(preview.Conflicts.Select(item => item.WeekStart)).IsEquivalentTo([WeekStart]);
        await Assert.That(preview.NewCurrentWeekStart).IsEqualTo(new DateOnly(2026, 11, 9));
        var refused = await Assert.ThrowsAsync<AppException>(() => host.Weeks.ChangeWeekStartAsync(new(Op(), DayOfWeek.Monday, Remap: false)));
        await Assert.That(refused!.Fields.Single().FieldPath).IsEqualTo("remap");

        var changed = await host.Weeks.ChangeWeekStartAsync(new(Op(), DayOfWeek.Monday, Remap: true));
        await Assert.That(changed.DraftWeekStarts).IsEquivalentTo([new DateOnly(2026, 11, 9)]);

        var current = await host.Weeks.OpenAsync(null);
        await Assert.That(current.WeekStart).IsEqualTo(new DateOnly(2026, 11, 9));
        await Assert.That(current.State).IsEqualTo(WeekPlanState.Draft);
        await Assert.That(current.Draft!.Assignments.Select(item => (item.TaskId, item.Day)))
            .IsEquivalentTo([(wednesday.Id, (DateOnly?)Today.AddDays(1)), (saturday.Id, (DateOnly?)WeekStart.AddDays(6)), (pool.Id, (DateOnly?)null)]);
        await Assert.That(current.Draft.Goals.Single().Title).IsEqualTo("Keep going");

        // Historical plans keep their dates; the superseded one keeps its rows and revision.
        await Assert.That((await host.Weeks.OpenAsync(Today.AddDays(-6))).WeekStart).IsEqualTo(LastWeekStart);
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM WeekPlans WHERE State = 2;")).IsEqualTo(1L);
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM WeekPlanRevisions;")).IsEqualTo(2L);

        var confirmed = (await host.Weeks.ConfirmAsync(new(Op(), current.WeekStart, current.DraftRevision))).Week;
        await Assert.That(confirmed.State).IsEqualTo(WeekPlanState.Confirmed);
        await Assert.That(host.Scalar("SELECT FirstWeekday FROM Profiles;")).IsEqualTo(1L);
    }

    [Test]
    public async Task ConfirmedWeek_SurvivesRestart_AndReplayReturnsTheSameRevision()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new SettableClock(Morning);
        string confirmOp = Op();
        using (var host = await Host.StartAsync(root, clock))
        {
            var task = await host.TaskAsync("Durable", estimate: 10);
            var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
            var saved = (await host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, Draft([new(task.Id, Today.AddDays(1))], [Goal("Stay steady", task.Id)])))).Week;
            await host.Weeks.ConfirmAsync(new(confirmOp, WeekStart, saved.DraftRevision));
            var replay = await host.Weeks.ConfirmAsync(new(confirmOp, WeekStart, saved.DraftRevision));
            await Assert.That(replay.Replayed).IsTrue();
            await Assert.That(replay.Week.ConfirmedRevision).IsEqualTo(1L);
        }

        using var reopened = await Host.StartAsync(root, clock);
        var week = await reopened.Weeks.OpenAsync(null);
        await Assert.That(week.State).IsEqualTo(WeekPlanState.Confirmed);
        await Assert.That(week.Confirmed.Placements.Single().Day).IsEqualTo(Today.AddDays(1));
        await Assert.That(week.Confirmed.Goals.Single().Title).IsEqualTo("Stay steady");
        await Assert.That(week.Tasks.Single().Task.Title).IsEqualTo("Durable");
    }
}
