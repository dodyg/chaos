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

/// <summary>My Day against real SQLite (PLAN §15 WP8 tests).</summary>
public sealed class MyDayTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);

    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Host : IDisposable
    {
        public required string Root { get; init; }
        public required ChaosStorageHandle Storage { get; init; }
        public required SettableClock Clock { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required MyDayCommands Days { get; init; }
        public required TaskQueries Queries { get; init; }

        public static async Task<Host> StartAsync(string? root = null, SettableClock? clock = null, string zone = "UTC")
        {
            root ??= TestPaths.NewDataRoot();
            clock ??= new SettableClock(Morning);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            Execute(storage.DatabasePath, $"UPDATE Profiles SET ZoneId = '{zone}';");
            return new Host
            {
                Root = root,
                Storage = storage,
                Clock = clock,
                Tasks = new TaskCommands(storage.Store, ids, clock),
                Days = new MyDayCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
            };
        }

        public async Task<TaskRecord> TaskAsync(string title, DateOnly? planned = null, DateOnly? due = null, int? estimate = null)
        {
            var task = (await Tasks.CaptureAsync(new(Op(), "", title, null))).Task;
            if (planned is null && due is null && estimate is null)
            {
                return task;
            }

            return (await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, title, "", planned, null, null, due, null, null, TaskPriority.Normal, estimate, CapacityCategory.Personal))).Task;
        }

        public async Task<IReadOnlyList<string>> DayTitlesAsync(DateOnly day, bool finished = false) =>
            [.. (await Queries.RunAsync(new TaskQuerySpec
            {
                View = TaskView.MyDay,
                Day = day,
                Statuses = finished ? [TaskItemStatus.Done, TaskItemStatus.Cancelled] : [],
            })).Rows.Select(row => row.Task.Title)];

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

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Test]
    public async Task Open_ConcurrentCalls_CreateOnePlanForTheDate()
    {
        using var host = await Host.StartAsync();
        var opened = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => host.Days.OpenAsync(null))));
        await Assert.That(opened.All(day => day.Exists && day.Date == Today)).IsTrue();
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlans;")).IsEqualTo(1L);
    }

    [Test]
    public async Task Open_EarlierDate_ReadsWithoutCreatingAPlan()
    {
        using var host = await Host.StartAsync();
        var earlier = await host.Days.OpenAsync(Today.AddDays(-3));
        await Assert.That(earlier.Exists).IsFalse();
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlans;")).IsEqualTo(0L);
    }

    [Test]
    public async Task NewDay_StartsEmpty_YesterdayStaysReviewableAfterRestart()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new SettableClock(Morning);
        using (var host = await Host.StartAsync(root, clock))
        {
            var task = await host.TaskAsync("Write report", estimate: 45);
            var added = await host.Days.AddAsync(new(Op(), Today, task.Id));
            await Assert.That(added.Summary.OpenCount).IsEqualTo(1);
            await Assert.That(added.Summary.RemainingMinutes).IsEqualTo(45);

            clock.Now = Morning.AddDays(1);
            var tomorrow = await host.Days.OpenAsync(null);
            await Assert.That(tomorrow.Date).IsEqualTo(Today.AddDays(1));
            await Assert.That(tomorrow.OpenCount).IsEqualTo(0);
        }

        using var reopened = await Host.StartAsync(root, clock);
        var yesterday = await reopened.Days.OpenAsync(Today);
        await Assert.That(yesterday.OpenCount).IsEqualTo(1);
        await Assert.That(await reopened.DayTitlesAsync(Today)).IsEquivalentTo(["Write report"]);
        await Assert.That(await reopened.DayTitlesAsync(Today.AddDays(1))).IsEmpty();
        await Assert.That(reopened.Scalar("SELECT COUNT(*) FROM DayPlans;")).IsEqualTo(2L);
    }

    [Test]
    public async Task Add_KeepsPlannedAndDueDates_AndRejectsEarlierDays()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Plan trip", planned: Today.AddDays(5), due: Today.AddDays(7));
        await host.Days.AddAsync(new(Op(), Today, task.Id));
        var saved = (await host.Tasks.GetAsync(task.Id))!;
        await Assert.That(saved.Version).IsEqualTo(task.Version);
        await Assert.That(saved.PlannedDate).IsEqualTo(Today.AddDays(5));
        await Assert.That(saved.DueDate).IsEqualTo(Today.AddDays(7));

        var error = await Assert.ThrowsAsync<AppException>(() => host.Days.AddAsync(new(Op(), Today.AddDays(-1), task.Id)));
        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.Validation);
    }

    [Test]
    public async Task Add_SameOperationReplays_DifferentContentConflicts()
    {
        using var host = await Host.StartAsync();
        var first = await host.TaskAsync("First");
        var second = await host.TaskAsync("Second");
        var operation = Op();
        await host.Days.AddAsync(new(operation, Today, first.Id));
        var replay = await host.Days.AddAsync(new(operation, Today, first.Id));
        await Assert.That(replay.Replayed).IsTrue();
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlanItems;")).IsEqualTo(1L);
        var conflict = await Assert.ThrowsAsync<AppException>(() => host.Days.AddAsync(new(operation, Today, second.Id)));
        await Assert.That(conflict!.Kind).IsEqualTo(AppErrorKind.OperationConflict);
    }

    [Test]
    public async Task Remove_Persists_AndReopeningNeverPutsTheTaskBack()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Call the bank", due: Today);
        await host.Days.AddAsync(new(Op(), Today, task.Id));
        var removed = await host.Days.RemoveAsync(new(Op(), Today, task.Id));
        await Assert.That(removed.Changed).IsEqualTo(1);

        await host.Days.OpenAsync(null);
        await Assert.That(await host.DayTitlesAsync(Today)).IsEmpty();
        // The removed row is kept so nothing re-adds it silently; the task is suggested again instead.
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlanItems WHERE RemovedAtUtc IS NOT NULL;")).IsEqualTo(1L);
        var suggestions = await host.Days.SuggestAsync();
        await Assert.That(suggestions.Single().Task.Id).IsEqualTo(task.Id);

        var readded = await host.Days.AddAsync(new(Op(), Today, task.Id));
        await Assert.That(readded.Changed).IsEqualTo(1);
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlanItems;")).IsEqualTo(1L);
        await Assert.That(await host.DayTitlesAsync(Today)).IsEquivalentTo(["Call the bank"]);
    }

    [Test]
    public async Task CarryOver_AddsTomorrowOnce_AndKeepsTodaysMembership()
    {
        using var host = await Host.StartAsync();
        var open = await host.TaskAsync("Unfinished");
        var done = await host.TaskAsync("Finished");
        await host.Days.AddAsync(new(Op(), Today, open.Id));
        await host.Days.AddAsync(new(Op(), Today, done.Id));
        await host.Tasks.SetStatusAsync(new(Op(), done.Id, done.Version, TaskItemStatus.Done, null));

        var first = await host.Days.CarryOverAsync(new(Op(), Today, null));
        var second = await host.Days.CarryOverAsync(new(Op(), Today, null));
        await Assert.That(first.Changed).IsEqualTo(1);
        await Assert.That(second.Changed).IsEqualTo(0);
        await Assert.That(await host.DayTitlesAsync(Today.AddDays(1))).IsEquivalentTo(["Unfinished"]);
        await Assert.That(await host.DayTitlesAsync(Today)).IsEquivalentTo(["Unfinished"]);
        await Assert.That(await host.DayTitlesAsync(Today, finished: true)).IsEquivalentTo(["Finished"]);
        await Assert.That(host.Scalar("SELECT COUNT(*) FROM DayPlanItems WHERE Origin = 2;")).IsEqualTo(1L);
    }

    [Test]
    public async Task Move_ReordersOpenItems_AndTheViewFollowsManualOrder()
    {
        using var host = await Host.StartAsync();
        foreach (var title in new[] { "A", "B", "C" })
        {
            await host.Days.AddAsync(new(Op(), Today, (await host.TaskAsync(title)).Id));
        }

        var c = (await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.MyDay })).Rows.Single(row => row.Task.Title == "C").Task;
        await host.Days.MoveAsync(new(Op(), Today, c.Id, 0));
        await Assert.That(string.Join(",", await host.DayTitlesAsync(Today))).IsEqualTo("C,A,B");
    }

    [Test]
    public async Task Counts_MyDayCountsOpenItemsOnly_AndFinishedLeaveRemainingEstimate()
    {
        using var host = await Host.StartAsync();
        var a = await host.TaskAsync("A", estimate: 30);
        var b = await host.TaskAsync("B", estimate: 20);
        var c = await host.TaskAsync("C");
        foreach (var task in new[] { a, b, c })
        {
            await host.Days.AddAsync(new(Op(), Today, task.Id));
        }

        await host.Tasks.SetStatusAsync(new(Op(), b.Id, b.Version, TaskItemStatus.Cancelled, null));
        var day = await host.Days.OpenAsync(null);
        await Assert.That(day.OpenCount).IsEqualTo(2);
        await Assert.That(day.FinishedCount).IsEqualTo(1);
        await Assert.That(day.RemainingMinutes).IsEqualTo(30);
        await Assert.That(day.UnknownEstimates).IsEqualTo(1);
        var counts = await host.Queries.CountsAsync();
        await Assert.That(counts.Views[TaskView.MyDay]).IsEqualTo(2L);
    }

    [Test]
    public async Task Suggestions_AreExplicit_DistinctAndOrderedByReason()
    {
        var clock = new SettableClock(Morning.AddDays(-1));
        using var host = await Host.StartAsync(clock: clock);
        var yesterdayWork = await host.TaskAsync("Left from yesterday");
        await host.Days.AddAsync(new(Op(), Today.AddDays(-1), yesterdayWork.Id));
        clock.Now = Morning;

        await host.TaskAsync("Planned in the past", planned: Today.AddDays(-2));
        await host.TaskAsync("Overdue", due: Today.AddDays(-1));
        await host.TaskAsync("Due today and planned today", planned: Today, due: Today);
        await host.TaskAsync("Planned today", planned: Today);
        await host.TaskAsync("Due in two days", due: Today.AddDays(2));
        await host.TaskAsync("Due in three days", due: Today.AddDays(3));
        var chosen = await host.TaskAsync("Already chosen", due: Today);
        await host.Days.AddAsync(new(Op(), Today, chosen.Id));
        var finished = await host.TaskAsync("Finished overdue", due: Today.AddDays(-4));
        await host.Tasks.SetStatusAsync(new(Op(), finished.Id, finished.Version, TaskItemStatus.Done, null));

        var suggestions = await host.Days.SuggestAsync();
        await Assert.That(string.Join(" | ", suggestions.Select(item => $"{item.Task.Title}:{item.Reason}"))).IsEqualTo(
            "Overdue:Overdue | Due today and planned today:DueToday | Planned today:PlannedToday | Due in two days:DueSoon | Left from yesterday:UnfinishedYesterday");

        // Suggesting inserted nothing: only the task chosen explicitly is on today.
        await Assert.That(await host.DayTitlesAsync(Today)).IsEquivalentTo(["Already chosen"]);
    }

    [Test]
    public async Task Clock_FollowsProfileZoneAcrossADstChange()
    {
        // New York springs forward on 2026-03-08; that local day is 23 hours long.
        var clock = new SettableClock(new DateTimeOffset(2026, 3, 9, 3, 30, 0, TimeSpan.Zero));
        using var host = await Host.StartAsync(clock: clock, zone: "America/New_York");
        var before = await host.Days.ClockAsync();
        await Assert.That(before.Today).IsEqualTo(new DateOnly(2026, 3, 8));
        await Assert.That(before.NextDayStartsAtUtc).IsEqualTo(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero));
        var task = await host.TaskAsync("Late evening");
        await host.Days.AddAsync(new(Op(), before.Today, task.Id));

        clock.Now = before.NextDayStartsAtUtc.AddSeconds(1);
        var after = await host.Days.OpenAsync(null);
        await Assert.That(after.Date).IsEqualTo(new DateOnly(2026, 3, 9));
        await Assert.That(after.OpenCount).IsEqualTo(0);
        await Assert.That((await host.Days.OpenAsync(new DateOnly(2026, 3, 8))).OpenCount).IsEqualTo(1);
    }
}
