using Chaos.Application.Contracts;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Application.Views;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.Views;

/// <summary>
/// View alerts against real SQLite (PLAN2 §11.2): switching alerts on is silent, a new match alerts
/// once and grouped, a task that leaves and returns alerts again only after 24 hours, quiet hours hold
/// delivery, and deleting a view or switching alerts off cancels what is pending.
/// </summary>
public sealed class ViewAlertTests
{
    // The test clock starts 2026-11-10 09:00 UTC; the profile uses the machine's time zone.
    private const string HighDefinition = "{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"},{\"field\":\"status\",\"op\":\"notIn\",\"value\":[\"Done\",\"Cancelled\"]}]}";

    private sealed class World(DataHost host)
    {
        public DataHost Host { get; } = host;
        public SavedViewCommands Views { get; } = new(host.Store, host.Ids, host.Clock);
        public ViewAlertService Alerts { get; } = new(host.Store, host.Ids, host.Clock);

        public async Task<TaskRecord> TaskAsync(string title, TaskPriority priority = TaskPriority.Normal)
        {
            var task = (await Host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, title, null))).Task;
            return priority == TaskPriority.Normal
                ? task
                : (await Host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, task.Version, title, null, null, null, null, null, null, null, priority, null, CapacityCategory.Personal))).Task;
        }

        public async Task<SavedViewRecord> AlertingViewAsync(string definition = HighDefinition) =>
            (await Views.SaveAsync(new(DataHost.Op(), null, 0, "Urgent things", "flag", null, definition, null, Pinned: true, AlertsEnabled: true))).View;

        public async Task MakeHighAsync(TaskRecord task) =>
            await Host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, (await Host.Tasks.GetAsync(task.Id))!.Version, task.Title, null, null, null, null, null, null, null, TaskPriority.High, null, CapacityCategory.Personal));

        public async Task MakeNormalAsync(TaskRecord task) =>
            await Host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, (await Host.Tasks.GetAsync(task.Id))!.Version, task.Title, null, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
    }

    [Test]
    public async Task EnablingAlerts_StoresTheCurrentMatchesSilently()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.TaskAsync("Already urgent", TaskPriority.High);

        var view = await w.AlertingViewAsync();
        var raised = await w.Alerts.EvaluateAsync();
        var delivered = await w.Alerts.DeliverDueAsync();

        await Assert.That(raised).IsEqualTo(0);
        await Assert.That(delivered.Count).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync(async (s, ct) => await s.SavedViews.ListMatchesAsync(view.Id, ct))).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ANewMatch_AlertsOnce_AndANewEvaluationDoesNotRepeatIt()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.AlertingViewAsync();
        var fresh = await w.TaskAsync("Server is down", TaskPriority.Urgent);

        await Assert.That(await w.Alerts.EvaluateAsync()).IsEqualTo(1);
        var first = await w.Alerts.DeliverDueAsync();
        await Assert.That(await w.Alerts.EvaluateAsync()).IsEqualTo(0);
        var second = await w.Alerts.DeliverDueAsync();

        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].Alert.TaskCount).IsEqualTo(1);
        await Assert.That(first[0].Alert.FirstTaskTitle).IsEqualTo("Server is down");
        await Assert.That(first[0].Alert.TaskIds[0]).IsEqualTo(fresh.Id);
        await Assert.That(first[0].ViewName).IsEqualTo("Urgent things");
        await Assert.That(second.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SeveralNewMatches_AreGroupedIntoOneAlertPerView_EvenAcrossEvaluations()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.AlertingViewAsync();
        await w.TaskAsync("One", TaskPriority.High);
        await w.TaskAsync("Two", TaskPriority.High);
        await w.Alerts.EvaluateAsync();
        // A third arrives before the alert is delivered: it joins the same alert.
        await w.TaskAsync("Three", TaskPriority.High);
        await w.Alerts.EvaluateAsync();

        var delivered = await w.Alerts.DeliverDueAsync();

        await Assert.That(delivered.Count).IsEqualTo(1);
        await Assert.That(delivered[0].Alert.TaskCount).IsEqualTo(3);
        await Assert.That(delivered[0].Alert.TaskIds.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ATaskThatLeavesAndReturnsWithin24Hours_DoesNotAlertAgain_ButAfter24HoursItDoes()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var task = await w.TaskAsync("Flapping", TaskPriority.High);
        await w.AlertingViewAsync();
        await w.Alerts.EvaluateAsync(); // baseline already holds it

        await w.MakeNormalAsync(task);
        await w.Alerts.EvaluateAsync(); // it left
        host.Clock.Now = host.Clock.Now.AddHours(23);
        await w.MakeHighAsync(task);
        await Assert.That(await w.Alerts.EvaluateAsync()).IsEqualTo(0); // back within 24 h: quiet

        await w.MakeNormalAsync(task);
        await w.Alerts.EvaluateAsync(); // left again
        host.Clock.Now = host.Clock.Now.AddHours(25);
        await w.MakeHighAsync(task);

        await Assert.That(await w.Alerts.EvaluateAsync()).IsEqualTo(1); // back after more than 24 h: new again
        await Assert.That((await w.Alerts.DeliverDueAsync()).Single().Alert.TaskCount).IsEqualTo(1);
    }

    [Test]
    public async Task QuietHours_HoldDeliveryUntilTheyEnd()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var reminders = new ReminderCommands(host.Store, host.Ids, host.Clock, host.Tasks, new NullAdapter());
        await QuietAroundNowAsync(host, reminders); // quiet from an hour ago until one hour from now
        await w.AlertingViewAsync();
        await w.TaskAsync("Arrives in quiet hours", TaskPriority.High);

        await w.Alerts.EvaluateAsync();
        var held = await host.Store.QueryAsync(async (session, ct) => await session.SavedViews.ListDueAlertsAsync(DateTimeOffset.MaxValue, 10, ct));
        await Assert.That(held.Single().DeliverAtUtc).IsEqualTo(host.Clock.Now.AddHours(1));
        await Assert.That((await w.Alerts.DeliverDueAsync()).Count).IsEqualTo(0);

        host.Clock.Now = host.Clock.Now.AddMinutes(61);
        var delivered = await w.Alerts.DeliverDueAsync();

        await Assert.That(delivered.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SwitchingAlertsOffOrDeletingTheView_CancelsAPendingAlert()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var reminders = new ReminderCommands(host.Store, host.Ids, host.Clock, host.Tasks, new NullAdapter());
        await QuietAroundNowAsync(host, reminders);
        var view = await w.AlertingViewAsync();
        await w.TaskAsync("Held back", TaskPriority.High);
        await w.Alerts.EvaluateAsync();

        await w.Views.SaveAsync(new(DataHost.Op(), view.Id, view.Version, view.Name, view.Icon, view.Color, view.DefinitionJson, view.LayoutJson, true, AlertsEnabled: false));
        host.Clock.Now = host.Clock.Now.AddHours(2);

        await Assert.That((await w.Alerts.DeliverDueAsync()).Count).IsEqualTo(0);
        await Assert.That((await w.Alerts.ListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Acknowledge_DismissesTheAlert_AndIsIdempotent()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.AlertingViewAsync();
        await w.TaskAsync("Something", TaskPriority.High);
        await w.Alerts.EvaluateAsync();
        var alert = (await w.Alerts.DeliverDueAsync()).Single().Alert;
        await Assert.That((await w.Alerts.ListAsync()).Count).IsEqualTo(1);

        await w.Alerts.AcknowledgeAsync(alert.Id);
        await w.Alerts.AcknowledgeAsync(alert.Id);
        await w.Alerts.AcknowledgeAsync("no-such-alert");

        await Assert.That((await w.Alerts.ListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ANewerAlertReplacesTheOlderOneOfTheSameViewOnScreen()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.AlertingViewAsync();
        await w.TaskAsync("First", TaskPriority.High);
        await w.Alerts.EvaluateAsync();
        await w.Alerts.DeliverDueAsync();
        await w.TaskAsync("Second", TaskPriority.High);
        await w.Alerts.EvaluateAsync();
        await w.Alerts.DeliverDueAsync();

        var shown = await w.Alerts.ListAsync();

        await Assert.That(shown.Count).IsEqualTo(1);
        await Assert.That(shown[0].Alert.FirstTaskTitle).IsEqualTo("Second");
    }

    [Test]
    public async Task AlertsSurviveARestart_AndTheBaselineStillKeepsOldMatchesQuiet()
    {
        var root = TestPaths.NewDataRoot();
        string alertId;
        using (var host = await DataHost.StartAsync(root))
        {
            var w = new World(host);
            await w.TaskAsync("Old match", TaskPriority.High);
            await w.AlertingViewAsync();
            await w.TaskAsync("New match", TaskPriority.High);
            await w.Alerts.EvaluateAsync();
            alertId = (await w.Alerts.DeliverDueAsync()).Single().Alert.Id;
        }

        using (var host = await DataHost.StartAsync(root))
        {
            var w = new World(host);
            await Assert.That(await w.Alerts.EvaluateAsync()).IsEqualTo(0);
            var shown = await w.Alerts.ListAsync();
            await Assert.That(shown.Single().Alert.Id).IsEqualTo(alertId);
            await Assert.That(shown.Single().Alert.TaskCount).IsEqualTo(1);
        }
    }

    /// <summary>Quiet hours from an hour before the test clock to an hour after it, in the profile's own zone (it is the machine's zone, not UTC).</summary>
    private static async Task QuietAroundNowAsync(DataHost host, ReminderCommands reminders)
    {
        var settings = await reminders.GetSettingsAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.ZoneId);
        var local = TimeZoneInfo.ConvertTime(host.Clock.Now, zone);
        var start = TimeOnly.FromDateTime(local.DateTime.AddHours(-1));
        var end = TimeOnly.FromDateTime(local.DateTime.AddHours(1));
        await reminders.UpdateSettingsAsync(new(DataHost.Op(), settings.Version, settings.DefaultReminderTime, true, new TimeOnly(start.Hour, start.Minute), new TimeOnly(end.Hour, end.Minute)));
    }

    private sealed class NullAdapter : Chaos.Application.Abstractions.INotificationAdapter
    {
        public Chaos.Application.Abstractions.NotificationCapabilities Capabilities { get; } = new("test", false, false, Chaos.Application.Abstractions.NotificationPermission.Granted, string.Empty);
        public Task<Chaos.Application.Abstractions.NotificationScheduleResult> ScheduleAsync(Chaos.Application.Abstractions.NotificationRequest request, CancellationToken ct) => Task.FromResult(new Chaos.Application.Abstractions.NotificationScheduleResult(false, null, null));
        public Task CancelAsync(string osNotificationId, CancellationToken ct) => Task.CompletedTask;
        public Task<Chaos.Application.Abstractions.NotificationShowOutcome> ShowAsync(Chaos.Application.Abstractions.NotificationRequest request, CancellationToken ct) => Task.FromResult(Chaos.Application.Abstractions.NotificationShowOutcome.InApp);
    }
}
