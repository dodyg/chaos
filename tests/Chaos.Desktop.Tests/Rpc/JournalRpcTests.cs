using Chaos.Application.Planning;
using Chaos.Application.Summaries;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class JournalRpcTests
{
    [Test]
    public async Task JournalThroughRpc_GenerateCarryOverAndRestart_KeepsTheStoredSummary()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        string summaryId, today, title;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None))
        {
            var ids = new SequentialIdGenerator();
            var tasks = new TaskCommands(storage.Store, ids, clock);
            var days = new MyDayRpcService(new MyDayCommands(storage.Store, ids, clock));
            var rpc = new JournalRpcService(new SummaryCommands(storage.Store, ids, clock));
            today = (await days.ClockAsync(new(), CancellationToken.None)).Clock!.Today;
            var task = (await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Renew passport", null))).Task;
            title = task.Title;
            await days.AddAsync(new(Guid.NewGuid().ToString(), today, task.Id), CancellationToken.None);

            var badKind = await rpc.GenerateAsync(new(Guid.NewGuid().ToString(), "Monthly", today), CancellationToken.None);
            await Assert.That(badKind.Ok).IsFalse();
            await Assert.That(badKind.Error!.Fields.Single().FieldPath).IsEqualTo("kind");

            var generated = await rpc.GenerateAsync(new(Guid.NewGuid().ToString(), "Daily", today), CancellationToken.None);
            await Assert.That(generated.Ok).IsTrue();
            var detail = generated.Summary!;
            summaryId = detail.Header.Id;
            await Assert.That(detail.Header.Kind).IsEqualTo("Daily");
            await Assert.That(detail.Header.Trigger).IsEqualTo("Manual");
            await Assert.That(detail.Facts.Unfinished.Single().Source).IsEqualTo("MyDay");

            var preview = await rpc.PreviewCarryOverAsync(new(summaryId), CancellationToken.None);
            await Assert.That(preview.Target).IsEqualTo("Day");
            await Assert.That(preview.Candidates.Single().StillOpen).IsTrue();
            var carried = await rpc.CarryOverAsync(new(Guid.NewGuid().ToString(), summaryId, [task.Id]), CancellationToken.None);
            await Assert.That(carried.Ok).IsTrue();
            await Assert.That(carried.Added).IsEqualTo(1);

            var settings = await rpc.SettingsAsync(new(), CancellationToken.None);
            await Assert.That(settings.EveningTime).IsEqualTo("18:00");
            var updated = await rpc.UpdateSettingsAsync(new(Guid.NewGuid().ToString(), settings.Version!, "07:30", "21:00", "Friday"), CancellationToken.None);
            await Assert.That(updated.Ok).IsTrue();
            await Assert.That(updated.MorningTime).IsEqualTo("07:30");
            await Assert.That(updated.WeeklyDay).IsEqualTo("Friday");
            var badTime = await rpc.UpdateSettingsAsync(new(Guid.NewGuid().ToString(), updated.Version!, "7 o'clock", null, null), CancellationToken.None);
            await Assert.That(badTime.Ok).IsFalse();
        }

        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var again = new JournalRpcService(new SummaryCommands(reopened.Store, new SequentialIdGenerator(), clock));
        var list = await again.ListAsync(new(null, null), CancellationToken.None);
        var found = await again.FindAsync(new("Daily", today), CancellationToken.None);

        await Assert.That(list.Entries.Any(entry => entry.Id == summaryId)).IsTrue();
        await Assert.That(found.Summary!.Header.Id).IsEqualTo(summaryId);
        await Assert.That(found.Summary.Facts.Unfinished.Single().Task.Title).IsEqualTo(title);
    }
}
