using Chaos.Application.Abstractions;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class ReminderRpcTests
{
    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    /// <summary>A presenter whose OS permission is denied: delivery is still recorded, shown as suppressed.</summary>
    private sealed class DeniedAdapter : INotificationAdapter
    {
        public int Shown { get; private set; }
        public NotificationCapabilities Capabilities { get; } = new("Denied", false, true, NotificationPermission.Denied, "Notifications are turned off for Chaos.");
        public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(new NotificationScheduleResult(false, null, "denied"));
        public Task CancelAsync(string osNotificationId, CancellationToken ct) => Task.CompletedTask;
        public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct)
        {
            Shown++;
            return Task.FromResult(NotificationShowOutcome.Suppressed);
        }
    }

    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task DeniedPermission_DeliveryIsKeptInTheCenter_AndActionsStillWork()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var ids = new GuidIds();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var adapter = new DeniedAdapter();
        var reminders = new ReminderCommands(storage.Store, ids, clock, tasks, adapter);
        using var engine = new ReminderEngine(reminders, adapter, clock);
        var rpc = new ReminderRpcService(reminders, engine);
        var task = (await tasks.CaptureAsync(new(Op(), "", "Renew licence", null))).Task;

        var added = await rpc.AddAsync(new(Op(), task.Id, new ReminderRuleDto("Absolute", null, "2026-11-10T11:00:00Z", null, true)), CancellationToken.None);
        var alerts = await rpc.AlertsAsync(new(null), CancellationToken.None);
        var center = await rpc.CenterAsync(new(10), CancellationToken.None);
        var alert = alerts.Items.Single();
        var badAction = await rpc.ActAsync(new(Op(), alert.JobId, alert.Generation, "2", null, null, null), CancellationToken.None);
        var snoozed = await rpc.ActAsync(new(Op(), alert.JobId, alert.Generation, "Snooze", "Tomorrow", null, null), CancellationToken.None);
        var settings = await rpc.SettingsAsync(new(), CancellationToken.None);

        await Assert.That(added.Ok).IsTrue();
        await Assert.That(adapter.Shown).IsEqualTo(1);
        await Assert.That(alert.TaskTitle).IsEqualTo("Renew licence");
        await Assert.That(alert.Generation).IsEqualTo("1");
        await Assert.That(center.Items.Single().Presentation).IsEqualTo("Suppressed");
        await Assert.That(badAction.Ok).IsFalse();
        await Assert.That(badAction.Error!.Fields.Single().FieldPath).IsEqualTo("action");
        await Assert.That(snoozed.Outcome).IsEqualTo("Snoozed");
        // "Tomorrow" is 09:00 on the next local date of the profile zone (seeded from this device).
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.ZoneId!);
        var expected = Chaos.Domain.Reminders.ReminderTiming.SnoozeUntil(Chaos.Domain.Reminders.SnoozeChoice.Tomorrow, clock.GetUtcNow(), zone, new TimeOnly(9, 0), null);
        await Assert.That(snoozed.SnoozedUntilUtc).IsEqualTo(StorageFormats.Instant(expected));
        await Assert.That(settings.Capabilities!.Permission).IsEqualTo("Denied");
        await Assert.That(settings.DefaultReminderTime).IsEqualTo("09:00");
    }

    [Test]
    public async Task WaitingThroughRpc_SetsPersonAndFollowUp_AndListsItForToday()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var ids = new GuidIds();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var task = (await new TaskCommands(storage.Store, ids, clock).CaptureAsync(new(Op(), "", "Signed lease", null))).Task;
        var rpc = new WaitingRpcService(new WaitingCommands(storage.Store, ids, clock));

        var set = await rpc.SetAsync(new(Op(), task.Id, task.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), "مريم",
            new FollowUpDto("Fixed", "2026-11-10T16:00:00Z", null, null)), CancellationToken.None);
        var due = await rpc.DueAsync(new(null), CancellationToken.None);
        var invalid = await rpc.SetAsync(new(Op(), task.Id, set.Task!.Version, "Someone", new FollowUpDto("AfterWaiting", null, 2, "Fortnights")), CancellationToken.None);

        await Assert.That(set.Ok).IsTrue();
        await Assert.That(set.Task.Status).IsEqualTo("WaitingOnSomeone");
        await Assert.That(set.Person).IsEqualTo("مريم");
        await Assert.That(set.FollowUpDueAtUtc).IsEqualTo("2026-11-10T16:00:00.0000000Z");
        await Assert.That(due.Items.Single().Person).IsEqualTo("مريم");
        await Assert.That(invalid.Ok).IsFalse();
        await Assert.That(invalid.Error!.Fields.Single().FieldPath).IsEqualTo("followUp.unit");
    }
}
