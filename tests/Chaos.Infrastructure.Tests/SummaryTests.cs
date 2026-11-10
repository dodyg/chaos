using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Summaries;
using Chaos.Application.Tasks;
using Chaos.Domain.Summaries;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests;

/// <summary>Plain summaries and the journal on real SQLite with a fake clock (PLAN §15 WP11 tests).</summary>
public sealed class SummaryTests
{
    // Tuesday 10 November 2026; weeks start on Sunday (8–14 November).
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly DateOnly WeekStart = new(2026, 11, 8);
    private static readonly DateTimeOffset Morning = At(Today, 9);

    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0) => new(date.Year, date.Month, date.Day, hour, minute, 0, TimeSpan.Zero);

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
        public required ContainerCommands Containers { get; init; }
        public required DiaryCommands Diary { get; init; }
        public required MyDayCommands Days { get; init; }
        public required MyWeekCommands Weeks { get; init; }
        public required SummaryCommands Summaries { get; init; }
        public required TaskQueries Queries { get; init; }

        public static async Task<Host> StartAsync(DateTimeOffset? start = null, string zone = "UTC")
        {
            var clock = new SettableClock(start ?? Morning);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
            Execute(storage.DatabasePath, $"UPDATE Profiles SET ZoneId = '{zone}', FirstWeekday = 0, QuietHoursEnabled = 0;");
            return new Host
            {
                Storage = storage,
                Clock = clock,
                Tasks = new TaskCommands(storage.Store, ids, clock),
                Containers = new ContainerCommands(storage.Store, ids, clock),
                Diary = new DiaryCommands(storage.Store, ids, clock),
                Days = new MyDayCommands(storage.Store, ids, clock),
                Weeks = new MyWeekCommands(storage.Store, ids, clock),
                Summaries = new SummaryCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
            };
        }

        public async Task<TaskRecord> TaskAsync(string title, string containerId = "", DateOnly? planned = null, DateOnly? due = null)
        {
            var task = (await Tasks.CaptureAsync(new(Op(), containerId, title, null))).Task;
            return planned is null && due is null ? task
                : (await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, title, "", planned, null, null, due, null, null, TaskPriority.Normal, null, CapacityCategory.Personal))).Task;
        }

        public async Task<TaskRecord> CurrentAsync(string taskId) => (await Tasks.GetAsync(taskId))!;

        public async Task<TaskRecord> SetStatusAsync(string taskId, TaskItemStatus status) =>
            (await Tasks.SetStatusAsync(new(Op(), taskId, (await CurrentAsync(taskId)).Version, status, null))).Task;

        public async Task RenameAsync(string taskId, string title)
        {
            var task = await CurrentAsync(taskId);
            await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, title, task.Notes, task.PlannedDate, null, null, task.DueDate, null, null, task.Priority, task.EstimateMinutes, task.CapacityCategory));
        }

        public async Task<SummaryView> GenerateAsync(SummaryKind kind, DateOnly date) => await Summaries.GenerateAsync(new GenerateSummaryCommand(Op(), kind, date));

        public async Task<SummaryView?> DailyAsync(DateOnly date) => await Summaries.FindAsync(SummaryKind.Daily, date);

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
    public async Task Daily_CompletedThenReopened_KeepsTheCompletionEventAnnotatedAsReopened()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Draft report");
        host.Clock.Now = At(Today, 10);
        await host.SetStatusAsync(task.Id, TaskItemStatus.Done);
        host.Clock.Now = At(Today, 11);
        await host.SetStatusAsync(task.Id, TaskItemStatus.InProgress);
        host.Clock.Now = At(Today.AddDays(1), 9);

        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);

        var completion = summary.Facts.Completions.Single();
        await Assert.That(completion.Task.Title).IsEqualTo("Draft report");
        await Assert.That(completion.CompletedAtUtc).IsEqualTo(At(Today, 10));
        await Assert.That(completion.ReopenedAfter).IsTrue();
        await Assert.That(completion.StillDone).IsFalse();
        await Assert.That(summary.Counts.CompletionEvents).IsEqualTo(1);
        await Assert.That(summary.Counts.StillDone).IsEqualTo(0);
        await Assert.That(summary.Summary.IsPartial).IsFalse();
    }

    [Test]
    public async Task CapturedLabels_RenameAndDeleteLater_DoNotRewriteTheStoredSummary()
    {
        using var host = await Host.StartAsync();
        var list = (await host.Containers.CreateAsync(new CreateContainerCommand(Op(), "Errands", ContainerKind.List, null))).Container;
        var task = await host.TaskAsync("Buy stamps", list.Id);
        host.Clock.Now = At(Today, 10);
        await host.SetStatusAsync(task.Id, TaskItemStatus.Done);
        host.Clock.Now = At(Today.AddDays(1), 8);
        var stored = await host.GenerateAsync(SummaryKind.Daily, Today);

        await host.RenameAsync(task.Id, "Something else");
        await host.Containers.RenameAsync(new RenameContainerCommand(Op(), list.Id, list.Version, "Renamed list"));
        await host.Tasks.DeleteAsync(new DeleteTaskCommand(Op(), task.Id, (await host.CurrentAsync(task.Id)).Version));
        var reread = await host.Summaries.GetAsync(stored.Summary.Id);

        await Assert.That(reread.Facts.Completions.Single().Task.Title).IsEqualTo("Buy stamps");
        await Assert.That(reread.Facts.Completions.Single().Task.ContainerName).IsEqualTo("Errands");
    }

    [Test]
    public async Task Regenerated_PastDay_SeesTheTitleTheTaskHadThen()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Old name");
        host.Clock.Now = At(Today, 10);
        await host.SetStatusAsync(task.Id, TaskItemStatus.Done);
        host.Clock.Now = At(Today.AddDays(1), 9);
        await host.RenameAsync(task.Id, "New name");

        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);

        await Assert.That(summary.Facts.Completions.Single().Task.Title).IsEqualTo("Old name");
    }

    [Test]
    public async Task GenerateDue_IsIdempotent_AndExplicitRegenerationAddsARevisionKeepingTheOldOne()
    {
        using var host = await Host.StartAsync();
        await host.TaskAsync("Something");
        host.Clock.Now = At(Today, 18, 5);

        var first = await host.Summaries.GenerateDueAsync();
        var second = await host.Summaries.GenerateDueAsync();
        var daily = await host.DailyAsync(Today);

        await Assert.That(first.Count(row => row.Kind == SummaryKind.Daily)).IsEqualTo(1);
        await Assert.That(second.Count).IsEqualTo(0);
        await Assert.That(daily!.Summary.IsPartial).IsTrue();
        await Assert.That(daily.Summary.CapturedThroughUtc).IsEqualTo(At(Today, 18, 5));

        host.Clock.Now = At(Today, 22);
        var operation = Op();
        var regenerated = await host.Summaries.GenerateAsync(new GenerateSummaryCommand(operation, SummaryKind.Daily, Today));
        var replayed = await host.Summaries.GenerateAsync(new GenerateSummaryCommand(operation, SummaryKind.Daily, Today));

        await Assert.That(regenerated.Summary.Revision).IsEqualTo(2L);
        await Assert.That(replayed.Summary.Id).IsEqualTo(regenerated.Summary.Id);
        await Assert.That(regenerated.Revisions.Count).IsEqualTo(2);
        await Assert.That(regenerated.Summary.CapturedThroughUtc).IsEqualTo(At(Today, 22));
        await Assert.That(host.Scalar("SELECT count(*) FROM Summaries WHERE Kind = 1;")).IsEqualTo(2L);
        var journal = await host.Summaries.JournalAsync(null, 20);
        await Assert.That(journal.Entries.Count(entry => entry.Summary.Kind == SummaryKind.Daily)).IsEqualTo(1);
        await Assert.That(journal.Entries.Single(entry => entry.Summary.Kind == SummaryKind.Daily).Summary.Revision).IsEqualTo(2L);
    }

    [Test]
    public async Task GenerateDue_BeforeItsTimes_MakesNothingForToday()
    {
        using var host = await Host.StartAsync(At(Today, 7));

        var created = await host.Summaries.GenerateDueAsync();

        await Assert.That(created.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MissedPeriods_AfterDaysClosed_AreGeneratedOnce_AndOnlyTheNewestPrompts()
    {
        using var host = await Host.StartAsync(At(Today.AddDays(-4), 9));
        await host.TaskAsync("Earlier task");
        host.Clock.Now = At(Today, 9); // Chaos was closed since the 6th

        var created = await host.Summaries.GenerateDueAsync();
        var again = await host.Summaries.GenerateDueAsync();
        var prompts = await host.Summaries.PromptsAsync();

        var dailies = created.Where(row => row.Kind == SummaryKind.Daily).ToList();
        await Assert.That(dailies.Select(row => row.PeriodStart).SequenceEqual([Today.AddDays(-4), Today.AddDays(-3), Today.AddDays(-2), Today.AddDays(-1)])).IsTrue();
        await Assert.That(dailies.All(row => row.Trigger == SummaryTrigger.CatchUp && !row.IsPartial)).IsTrue();
        await Assert.That(dailies.Count(row => row.PromptAtUtc is not null)).IsEqualTo(1);
        await Assert.That(created.Count(row => row.Kind == SummaryKind.Morning)).IsEqualTo(1);
        await Assert.That(again.Count).IsEqualTo(0);
        await Assert.That(prompts.Single(prompt => prompt.Summary.Kind == SummaryKind.Daily).Summary.PeriodStart).IsEqualTo(Today.AddDays(-1));

        await host.Summaries.DismissPromptAsync(prompts.Single(prompt => prompt.Summary.Kind == SummaryKind.Daily).Summary.Id);
        await Assert.That((await host.Summaries.PromptsAsync()).Any(prompt => prompt.Summary.Kind == SummaryKind.Daily)).IsFalse();
    }

    [Test]
    public async Task Prompt_DuringQuietHours_WaitsUntilTheyEnd()
    {
        using var host = await Host.StartAsync();
        Execute(host.Storage.DatabasePath, "UPDATE Profiles SET QuietHoursEnabled = 1, QuietStart = '17:00:00', QuietEnd = '20:00:00';");
        host.Clock.Now = At(Today, 18);

        var daily = (await host.Summaries.GenerateDueAsync()).Single(row => row.Kind == SummaryKind.Daily);

        await Assert.That(daily.PromptAtUtc).IsEqualTo(At(Today, 20));
        await Assert.That((await host.Summaries.PromptsAsync()).Any(prompt => prompt.Summary.Kind == SummaryKind.Daily)).IsFalse();
        host.Clock.Now = At(Today, 20, 1);
        await Assert.That((await host.Summaries.PromptsAsync()).Any(prompt => prompt.Summary.Kind == SummaryKind.Daily)).IsTrue();
    }

    [Test]
    public async Task PeriodBoundaries_FallBackDay_AreTwentyFiveHoursOfLocalTime()
    {
        // 1 November 2026 in New York lasts 25 hours: 04:00Z to 05:00Z the next day.
        var day = new DateOnly(2026, 11, 1);
        using var host = await Host.StartAsync(new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero), "America/New_York");
        var inside = await host.TaskAsync("Late evening");
        var outside = await host.TaskAsync("Next morning");
        host.Clock.Now = new DateTimeOffset(2026, 11, 2, 4, 30, 0, TimeSpan.Zero); // 23:30 EST on the 1st
        await host.SetStatusAsync(inside.Id, TaskItemStatus.Done);
        host.Clock.Now = new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero); // 00:30 EST on the 2nd
        await host.SetStatusAsync(outside.Id, TaskItemStatus.Done);

        var summary = await host.GenerateAsync(SummaryKind.Daily, day);

        await Assert.That(summary.Summary.PeriodEndUtc - summary.Summary.PeriodStartUtc).IsEqualTo(TimeSpan.FromHours(25));
        await Assert.That(summary.Facts.Completions.Select(item => item.Task.Title).SequenceEqual(["Late evening"])).IsTrue();
    }

    [Test]
    public async Task ProjectProgress_ShowsHowFarItMoved_IncludingATaskMovedOut()
    {
        using var host = await Host.StartAsync(At(Today.AddDays(-1), 9));
        var project = (await host.Containers.CreateAsync(new CreateContainerCommand(Op(), "Website", ContainerKind.Project, null))).Container;
        var design = await host.TaskAsync("Design", project.Id);
        await host.TaskAsync("Build", project.Id);
        var copy = await host.TaskAsync("Copy", project.Id);
        host.Clock.Now = At(Today, 10);
        await host.SetStatusAsync(design.Id, TaskItemStatus.Done);
        var current = await host.CurrentAsync(copy.Id);
        var inboxId = await host.Storage.Store.QueryAsync(async (session, ct) =>
            (await session.Containers.GetInboxAsync((await session.Spaces.GetPersonalByOwnerAsync((await session.Profiles.GetPrimaryAsync(ct))!.Id, ct))!.Id, ct))!.Id);
        await host.Tasks.MoveAsync(new MoveTaskCommand(Op(), copy.Id, current.Version, inboxId, null));
        host.Clock.Now = At(Today.AddDays(1), 9);

        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);

        var progress = summary.Facts.Projects.Single();
        await Assert.That(progress.Name).IsEqualTo("Website");
        await Assert.That((progress.DoneBefore, progress.CountedBefore)).IsEqualTo((0, 3));
        await Assert.That((progress.DoneAfter, progress.CountedAfter)).IsEqualTo((1, 2));
    }

    [Test]
    public async Task CarryOver_DailyUnfinished_AddsToTomorrowOnce_AndLeavesTaskDatesAlone()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Call the bank", planned: Today, due: Today.AddDays(5));
        await host.Days.AddAsync(new DayItemCommand(Op(), Today, task.Id));
        host.Clock.Now = At(Today, 19);
        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);
        var preview = await host.Summaries.PreviewCarryOverAsync(summary.Summary.Id);

        var first = await host.Summaries.CarryOverAsync(new SummaryCarryOverCommand(Op(), summary.Summary.Id, [task.Id]));
        var second = await host.Summaries.CarryOverAsync(new SummaryCarryOverCommand(Op(), summary.Summary.Id, [task.Id]));
        var after = await host.Summaries.PreviewCarryOverAsync(summary.Summary.Id);

        await Assert.That(summary.Facts.Unfinished.Single().Source).IsEqualTo(UnfinishedSource.MyDay);
        await Assert.That(preview.Target).IsEqualTo(CarryOverTarget.Day);
        await Assert.That(preview.TargetDate).IsEqualTo(Today.AddDays(1));
        await Assert.That(preview.Candidates.Single().AlreadyPlanned).IsFalse();
        await Assert.That(first.Added).IsEqualTo(1);
        await Assert.That(second.Added).IsEqualTo(0);
        await Assert.That(second.Skipped).IsEqualTo(1);
        await Assert.That(after.Candidates.Single().AlreadyPlanned).IsTrue();
        await Assert.That(host.Scalar($"SELECT count(*) FROM DayPlanItems i JOIN DayPlans p ON p.Id = i.DayPlanId WHERE p.LocalDate = '2026-11-11' AND i.TaskId = '{task.Id}' AND i.RemovedAtUtc IS NULL;")).IsEqualTo(1L);
        var unchanged = await host.CurrentAsync(task.Id);
        await Assert.That(unchanged.PlannedDate).IsEqualTo(Today);
        await Assert.That(unchanged.DueDate).IsEqualTo(Today.AddDays(5));
        // Today's historical membership stays.
        await Assert.That(host.Scalar($"SELECT count(*) FROM DayPlanItems i JOIN DayPlans p ON p.Id = i.DayPlanId WHERE p.LocalDate = '2026-11-10' AND i.TaskId = '{task.Id}' AND i.RemovedAtUtc IS NULL;")).IsEqualTo(1L);
    }

    [Test]
    public async Task CarryOver_TaskNotInTheSummary_IsRefused()
    {
        using var host = await Host.StartAsync();
        var stranger = await host.TaskAsync("Not planned");
        host.Clock.Now = At(Today, 19);
        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);

        await Assert.That(async () => await host.Summaries.CarryOverAsync(new SummaryCarryOverCommand(Op(), summary.Summary.Id, [stranger.Id])))
            .Throws<AppException>();
    }

    [Test]
    public async Task Week_OfFixtureActivity_ProducesAnUnderstandableJournalAndFeedsTheNextReview()
    {
        // Sunday 8 November: plan the week, then work through it day by day.
        using var host = await Host.StartAsync(At(WeekStart, 8));
        var project = (await host.Containers.CreateAsync(new CreateContainerCommand(Op(), "Garden", ContainerKind.Project, null))).Container;
        var plant = await host.TaskAsync("Plant bulbs", project.Id);
        var shed = await host.TaskAsync("Tidy shed", project.Id);
        var letter = await host.TaskAsync("Write letter");
        var tax = await host.TaskAsync("File taxes", due: WeekStart.AddDays(9));
        var started = (await host.Weeks.StartDraftAsync(new(Op(), WeekStart))).Week;
        var saved = (await host.Weeks.SaveDraftAsync(new(Op(), WeekStart, started.DraftRevision, new WeekDraft(
            [new WeekDraftGoal(Guid.NewGuid().ToString(), "Garden ready for winter", project.Id, [plant.Id, shed.Id], WeekGoalOutcome.Pending)],
            [new(plant.Id, WeekStart.AddDays(1)), new(shed.Id, WeekStart.AddDays(3)), new(letter.Id, WeekStart.AddDays(2))], [], [], 5)))).Week;
        await host.Weeks.ConfirmAsync(new(Op(), WeekStart, saved.DraftRevision));

        host.Clock.Now = At(WeekStart.AddDays(1), 10);
        await host.SetStatusAsync(plant.Id, TaskItemStatus.Done);
        await host.Diary.AddAsync(new AddDiaryEntryCommand(Op(), plant.Id, "Planted **forty** tulips by the fence.", null));
        host.Clock.Now = At(WeekStart.AddDays(2), 10);
        await host.SetStatusAsync(letter.Id, TaskItemStatus.Cancelled);
        host.Clock.Now = At(WeekStart.AddDays(6), 18, 30); // Saturday evening: the weekly summary is due
        var created = await host.Summaries.GenerateDueAsync();
        var weekly = await host.Summaries.GetAsync(created.Single(row => row.Kind == SummaryKind.Weekly).Id);

        await Assert.That(weekly.Summary.PeriodStart).IsEqualTo(WeekStart);
        await Assert.That(weekly.Summary.PeriodEnd).IsEqualTo(WeekStart.AddDays(6));
        await Assert.That(weekly.Facts.Completions.Select(item => item.Task.Title).SequenceEqual(["Plant bulbs"])).IsTrue();
        await Assert.That(weekly.Facts.Projects.Single().DoneAfter).IsEqualTo(1);
        await Assert.That(weekly.Facts.Unfinished.Select(item => item.Task.Title).SequenceEqual(["Tidy shed"])).IsTrue();
        await Assert.That(weekly.Facts.Week!.AssignedCount).IsEqualTo(3);
        await Assert.That(weekly.Facts.Week.DoneCount).IsEqualTo(1);
        await Assert.That(weekly.Facts.Week.CancelledCount).IsEqualTo(1);
        await Assert.That(weekly.Facts.Week.Goals.Single().DoneCount).IsEqualTo(1);
        await Assert.That(weekly.Facts.Deadlines.Select(item => item.Title).SequenceEqual(["File taxes"])).IsTrue();
        await Assert.That(weekly.Facts.DiaryUpdates.Single().Excerpt).IsEqualTo("Planted **forty** tulips by the fence.");
        await Assert.That(weekly.Facts.Suggested.Select(item => item.Task.Title).Contains("Tidy shed")).IsTrue();
        await Assert.That(created.Count(row => row.Kind == SummaryKind.Daily)).IsEqualTo(7);

        // The partial weekly summary is regenerated once the week has ended, then renamed tasks keep the captured title.
        host.Clock.Now = At(WeekStart.AddDays(7), 9);
        var complete = await host.GenerateAsync(SummaryKind.Weekly, WeekStart);
        await host.RenameAsync(plant.Id, "Renamed afterwards");
        var nextWeek = await host.Weeks.OpenAsync(WeekStart.AddDays(7));

        await Assert.That(complete.Summary.IsPartial).IsFalse();
        await Assert.That(nextWeek.LookBack.JournalSummaryId).IsEqualTo(complete.Summary.Id);
        await Assert.That(nextWeek.LookBack.JournalCompleted!.Single().Title).IsEqualTo("Plant bulbs");
        await Assert.That(nextWeek.LookBack.CompletedCount).IsEqualTo(1L);
        await Assert.That(nextWeek.LookBack.UnfinishedTaskIds.SequenceEqual([shed.Id])).IsTrue();

        // Weekly carry-over goes into next week's draft pool, once; the task's dates stay.
        var carried = await host.Summaries.CarryOverAsync(new SummaryCarryOverCommand(Op(), complete.Summary.Id, [shed.Id]));
        var again = await host.Summaries.CarryOverAsync(new SummaryCarryOverCommand(Op(), complete.Summary.Id, [shed.Id]));
        var planned = await host.Weeks.OpenAsync(WeekStart.AddDays(7));

        await Assert.That(carried.Target).IsEqualTo(CarryOverTarget.Week);
        await Assert.That(carried.TargetDate).IsEqualTo(WeekStart.AddDays(7));
        await Assert.That(carried.Added).IsEqualTo(1);
        await Assert.That(again.Added).IsEqualTo(0);
        await Assert.That(planned.Draft!.Assignments.Single(item => item.TaskId == shed.Id).Day).IsNull();
        await Assert.That(planned.Confirmed.Placements.Count).IsEqualTo(0);
        await Assert.That((await host.CurrentAsync(shed.Id)).PlannedDate).IsNull();
    }

    [Test]
    public async Task Morning_ShowsTheDaysPlanDueTasksAndOverdueCount()
    {
        using var host = await Host.StartAsync(At(Today.AddDays(-2), 9));
        var chosen = await host.TaskAsync("Chosen");
        await host.TaskAsync("Due today", due: Today);
        await host.TaskAsync("Late", due: Today.AddDays(-1));
        host.Clock.Now = At(Today, 7);
        await host.Days.AddAsync(new DayItemCommand(Op(), Today, chosen.Id));
        host.Clock.Now = At(Today, 8);

        var morning = (await host.Summaries.GenerateDueAsync()).Single(row => row.Kind == SummaryKind.Morning);
        var view = await host.Summaries.GetAsync(morning.Id);

        await Assert.That(view.Facts.Selected.Select(task => task.Title).SequenceEqual(["Chosen"])).IsTrue();
        await Assert.That(view.Facts.Deadlines.Select(task => task.Title).SequenceEqual(["Due today"])).IsTrue();
        await Assert.That(view.Facts.OverdueCount).IsEqualTo(1);
        await Assert.That(async () => await host.Summaries.PreviewCarryOverAsync(morning.Id)).Throws<AppException>();
    }

    [Test]
    public async Task Settings_TurningSummariesOff_StopsScheduledGeneration()
    {
        using var host = await Host.StartAsync();
        var settings = await host.Summaries.GetSettingsAsync();
        await Assert.That(settings.EveningTime).IsEqualTo(new TimeOnly(18, 0));
        await Assert.That(settings.MorningTime).IsEqualTo(new TimeOnly(8, 0));

        await host.Summaries.UpdateSettingsAsync(new UpdateSummarySettingsCommand(Op(), settings.Version, null, null, null));
        host.Clock.Now = At(Today, 23);

        await Assert.That((await host.Summaries.GenerateDueAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task GenerateFuturePeriod_IsRefused()
    {
        using var host = await Host.StartAsync();

        await Assert.That(async () => await host.GenerateAsync(SummaryKind.Daily, Today.AddDays(1))).Throws<AppException>();
    }
}
