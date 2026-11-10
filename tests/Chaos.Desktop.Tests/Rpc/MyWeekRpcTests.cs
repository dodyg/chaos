using Chaos.Application.Planning;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class MyWeekRpcTests
{
    [Test]
    public async Task RitualThroughRpc_Restart_ConfirmedWeekSeedsMyDay()
    {
        var root = TestPaths.NewDataRoot();
        // Tuesday 10 November 2026, noon UTC.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        string today;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None))
        {
            var ids = new SequentialIdGenerator();
            var task = (await new TaskCommands(storage.Store, ids, clock).CaptureAsync(new(Guid.NewGuid().ToString(), "", "Prepare the talk", null))).Task;
            var rpc = new MyWeekRpcService(new MyWeekCommands(storage.Store, ids, clock));

            var opened = await rpc.OpenAsync(new(null), CancellationToken.None);
            await Assert.That(opened.Ok).IsTrue();
            var week = opened.Week!;
            today = week.Today;
            await Assert.That(week.Days()).IsEqualTo(7);
            await Assert.That(week.AvailabilityConfigured).IsFalse();

            var started = await rpc.StartDraftAsync(new(Guid.NewGuid().ToString(), week.WeekStart), CancellationToken.None);
            await Assert.That(started.Ok).IsTrue();
            var goal = new WeekDraftGoalDto(Guid.NewGuid().ToString(), "Give a good talk", null, [task.Id], "Pending");
            var draft = new WeekDraftDto([goal], [new(task.Id, today)], [], [new(today, 300, null)], 4);

            var badEnum = await rpc.SaveDraftAsync(new(Guid.NewGuid().ToString(), week.WeekStart, started.Week!.DraftRevision,
                draft with { Goals = [goal with { Outcome = "Finished" }] }), CancellationToken.None);
            await Assert.That(badEnum.Ok).IsFalse();
            await Assert.That(badEnum.Error!.Fields.Single().FieldPath).IsEqualTo("goals[0].outcome");

            var saved = await rpc.SaveDraftAsync(new(Guid.NewGuid().ToString(), week.WeekStart, started.Week.DraftRevision, draft), CancellationToken.None);
            await Assert.That(saved.Ok).IsTrue();
            await Assert.That(saved.Week!.DraftBoard!.Days.Single(day => day.Date == today).AvailableMinutes).IsEqualTo(300);

            var confirmed = await rpc.ConfirmAsync(new(Guid.NewGuid().ToString(), week.WeekStart, saved.Week.DraftRevision), CancellationToken.None);
            await Assert.That(confirmed.Ok).IsTrue();
            await Assert.That(confirmed.Week!.State).IsEqualTo("Confirmed");
            await Assert.That(confirmed.Week.Confirmed.Goals.Single().CountedCount).IsEqualTo(1);

            var availability = await rpc.SetAvailabilityAsync(new(Guid.NewGuid().ToString(), [new("Monday", 360, 240)]), CancellationToken.None);
            await Assert.That(availability.Ok).IsTrue();
            await Assert.That(availability.Week!.AvailabilityConfigured).IsTrue();
        }

        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var again = await new MyWeekRpcService(new MyWeekCommands(reopened.Store, new SequentialIdGenerator(), clock)).OpenAsync(new(today), CancellationToken.None);
        await Assert.That(again.Week!.Confirmed.Placements.Single().Day).IsEqualTo(today);
        var day = await new MyDayRpcService(new MyDayCommands(reopened.Store, new SequentialIdGenerator(), clock)).OpenAsync(new(null), CancellationToken.None);
        await Assert.That(day.Day!.OpenCount).IsEqualTo(1);
    }
}

internal static class WeekOverviewExtensions
{
    public static int Days(this WeekOverviewDto week) => week.Confirmed.Days.Count;
}
