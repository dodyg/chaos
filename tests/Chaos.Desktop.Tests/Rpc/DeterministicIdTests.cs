using Chaos.Application.Abstractions;
using Chaos.Application.Pages;
using Chaos.Application.Planning;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Sync;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// PLAN2 §7.5: rows with a natural key get name-based ids, so two fresh data roots that create
/// the same real thing make the same row id. Each test shifts the second root's random-id
/// sequence (extra captured tasks first), so a sequential or random id could never match.
/// </summary>
public sealed class DeterministicIdTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed record DataRoot(ChaosStorageHandle Storage, SequentialIdGenerator Ids) : IDisposable
    {
        public void Dispose() => Storage.Dispose();
    }

    private static async Task<DataRoot> OpenAsync()
    {
        var ids = new SequentialIdGenerator();
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, new FixedTimeProvider(Now), CancellationToken.None);
        return new DataRoot(storage, ids);
    }

    private static async Task CaptureNoiseAsync(DataRoot root, int count)
    {
        var tasks = new TaskCommands(root.Storage.Store, root.Ids, new FixedTimeProvider(Now));
        for (var i = 0; i < count; i++)
        {
            _ = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", $"Noise {i} that shifts the random ids", null))).Task;
        }
    }

    [Test]
    public async Task SameDayOnTwoFreshRoots_SameDayPlanId()
    {
        string profileA, planA;
        using (var first = await OpenAsync())
        {
            (profileA, planA) = await OpenDayAsync(first, "2026-11-10");
        }

        string profileB, planB;
        using (var second = await OpenAsync())
        {
            await CaptureNoiseAsync(second, 2);
            (profileB, planB) = await OpenDayAsync(second, "2026-11-10");
        }

        await Assert.That(profileA).IsEqualTo(profileB); // identical bootstraps derive the same profile
        await Assert.That(planA).IsEqualTo(planB);
        await Assert.That(planA).IsEqualTo(StorageFormats.Id(NameBasedId.Create(NameBasedId.Names.DayPlan(profileA, new(2026, 11, 10)))));
    }

    private static async Task<(string ProfileId, string PlanId)> OpenDayAsync(DataRoot root, string date)
    {
        var days = new MyDayRpcService(new MyDayCommands(root.Storage.Store, new FixedTimeProvider(Now)));
        await days.OpenAsync(new(date), CancellationToken.None);
        return await root.Storage.Store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new InvalidOperationException("No profile in a booted data root.");
            return (profile.Id, (await session.DayPlans.FindAsync(profile.Id, StorageFormats.ParseDate(date)!.Value, token))!.Id);
        }, CancellationToken.None);
    }

    [Test]
    public async Task SameDailyPageOnTwoFreshRoots_SamePageId()
    {
        string pageA;
        using (var first = await OpenAsync())
        {
            pageA = await CreateDailyPageAsync(first, "2026-11-12");
        }

        using (var second = await OpenAsync())
        {
            await CaptureNoiseAsync(second, 2);
            var pageB = await CreateDailyPageAsync(second, "2026-11-12");
            await Assert.That(pageA).IsEqualTo(pageB);
        }
    }

    private static async Task<string> CreateDailyPageAsync(DataRoot root, string date)
    {
        var pages = new PageRpcService(new PageCommands(root.Storage.Store, root.Ids, new FixedTimeProvider(Now)));
        var created = await pages.CreateDailyAsync(new(Guid.NewGuid().ToString(), date, "Wrote on the day."), CancellationToken.None);
        return created.Pages[0].Id;
    }

    [Test]
    public async Task SameOccurrenceCompletedOnTwoFreshRoots_SameSuccessorTaskAndOccurrenceIds()
    {
        var (successorA, occurrenceA) = await CompleteFirstOccurrenceAsync(shiftRandomIdsWithNoise: false);
        var (successorB, occurrenceB) = await CompleteFirstOccurrenceAsync(shiftRandomIdsWithNoise: true);

        await Assert.That(successorA).IsEqualTo(successorB);
        await Assert.That(occurrenceA).IsEqualTo(occurrenceB);
    }

    /// <summary>Captures a task, starts a two-slot daily recurrence and completes the first slot.</summary>
    private static async Task<(string SuccessorTaskId, string OccurrenceId)> CompleteFirstOccurrenceAsync(bool shiftRandomIdsWithNoise)
    {
        using var root = await OpenAsync();
        var clock = new FixedTimeProvider(Now);
        var tasks = new TaskCommands(root.Storage.Store, root.Ids, clock);
        var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Repeat", null))).Task!;

        var rpc = new RecurrenceRpcService(new RecurrenceCommands(root.Storage.Store, root.Ids, clock), tasks);
        var rule = new RecurrenceRuleDto("FixedSchedule", "Day", 1, "2026-11-10", null, "UTC", [], null, null, null, null, "Count", 2, null);
        var started = await rpc.StartAsync(new(Guid.NewGuid().ToString(), task.Id, task.Version.ToString(), rule), CancellationToken.None);
        await Assert.That(started.Ok).IsTrue();

        if (shiftRandomIdsWithNoise)
        {
            await CaptureNoiseAsync(root, 3);
        }

        await tasks.SetStatusAsync(new(Guid.NewGuid().ToString(), task.Id, long.Parse(started.Task!.Version), Chaos.Domain.Tasks.TaskItemStatus.Done, null));

        var view = await rpc.GetAsync(new(task.Id), CancellationToken.None);
        await Assert.That(view.Ok).IsTrue();
        var successorTaskId = view.Occurrences.Single(occurrence => occurrence.State == "Active").TaskId;
        var occurrenceRow = await root.Storage.Store.QueryAsync(
            (session, token) => session.Recurrence.FindByTaskAsync(successorTaskId, token), CancellationToken.None);
        return (successorTaskId, occurrenceRow!.Id);
    }
}
