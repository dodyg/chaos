using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chaos.Domain.Tasks;
using Chaos.Server.Data;
using Chaos.Server.Tests.Sync;
using Chaos.Server.Web;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class WebWriteTests(PostgresServer postgres)
{
    [Test]
    public async Task MobileWrites_SyncToDesktop_AndCaptureCanBeRetried()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var desktop = await world.AddDeviceAsync();
        await desktop.ExecuteAsync("UPDATE SyncState SET BackfillPending = 1 WHERE Id = 1;");
        await world.SettleAsync();
        using var browser = world.Server.NewBrowser();
        using var signedIn = await WebFlow.SignInAsync(world.Server, browser, SyncWorld.Email);
        var session = await WebFlow.SessionAsync(browser);
        var today = (await browser.GetFromJsonAsync<WebToday>("/v1/web/today"))!.Date;
        var id = Guid.NewGuid();
        var capture = new WebCaptureRequest("  Mobile task  ", null, null, id);
        using var withoutToken = await browser.PostAsJsonAsync("/v1/web/tasks", capture);
        await Assert.That(withoutToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var added = await WebFlow.PostAsync(browser, session, "/v1/web/tasks", capture);
        await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var task = (await added.Content.ReadFromJsonAsync<WebTask>())!;
        await Assert.That(task.Id).IsEqualTo(id);
        await Assert.That(task.ListId).IsEqualTo(SharedSeedIds.InboxId);
        await Assert.That(task.Title).IsEqualTo("Mobile task");
        using var replay = await WebFlow.PostAsync(browser, session, "/v1/web/tasks", capture);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var planned = await WebFlow.PostAsync(browser, session, $"/v1/web/tasks/{id}/plan-today");
        await Assert.That(planned.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await planned.Content.ReadFromJsonAsync<WebTask>())!.PlannedDate).IsEqualTo(today);
        using var complete = await WebFlow.PostAsync(browser, session, $"/v1/web/tasks/{id}/complete");
        await Assert.That(complete.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await world.SettleAsync();
        var completed = (await desktop.Tasks.GetAsync(id.ToString("D")))!;
        await Assert.That(completed.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(completed.CompletedAtUtc).IsNotNull();
        await Assert.That(completed.PlannedDate).IsEqualTo(today);

        using var reopen = await WebFlow.PostAsync(browser, session, $"/v1/web/tasks/{id}/reopen");
        await Assert.That(reopen.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await world.SettleAsync();
        var reopened = (await desktop.Tasks.GetAsync(id.ToString("D")))!;
        await Assert.That(reopened.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await Assert.That(reopened.CompletedAtUtc).IsNull();

        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        var changes = await db.SyncChanges.AsNoTracking().Where(change => change.EntityKey == id.ToString("D")).OrderBy(change => change.Seq).ToListAsync();
        await Assert.That(changes.Count).IsEqualTo(4);
        await Assert.That(changes.All(change => change.OriginDevice == session.DeviceId)).IsTrue();
        await Assert.That(SyncJson.ReadGroups(changes[1].Groups).Keys.Single()).IsEqualTo("planned");
        await Assert.That(SyncJson.ReadGroups(changes[2].Groups).Keys.Single()).IsEqualTo("status");
    }

    [Test]
    public async Task Complete_ConcurrentDesktopEdit_PreservesTitleAndRecordsReplacedStatus()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var desktop = await world.AddDeviceAsync(start: world.Server.Clock.GetUtcNow().AddMinutes(-5));
        var reader = await world.AddDeviceAsync();
        await desktop.ExecuteAsync("UPDATE SyncState SET BackfillPending = 1 WHERE Id = 1;");
        var task = (await desktop.Tasks.CaptureAsync(new(Guid.NewGuid().ToString("D"), "", "Original", null))).Task;
        await world.SettleAsync();
        using var browser = world.Server.NewBrowser();
        using var signedIn = await WebFlow.SignInAsync(world.Server, browser, SyncWorld.Email);
        var session = await WebFlow.SessionAsync(browser);

        var edited = (await desktop.Tasks.SetStatusAsync(new(Guid.NewGuid().ToString("D"), task.Id, task.Version, TaskItemStatus.InProgress, null))).Task;
        await desktop.Tasks.UpdateDetailsAsync(new(Guid.NewGuid().ToString("D"), task.Id, edited.Version, "Desktop title",
            task.Notes, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        using var completed = await WebFlow.PostAsync(browser, session, $"/v1/web/tasks/{task.Id}/complete");
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await world.SettleAsync();
        foreach (var device in new[] { desktop, reader })
        {
            var merged = (await device.Tasks.GetAsync(task.Id))!;
            await Assert.That(merged.Title).IsEqualTo("Desktop title");
            await Assert.That(merged.Status).IsEqualTo(TaskItemStatus.Done);
        }

        var history = await desktop.TextAsync($"""
            SELECT SnapshotBefore FROM TaskHistory WHERE TaskId = '{task.Id}' AND EventKind = 'sync.merged'
              AND json_extract(SnapshotBefore, '$.groups.status.Status') = 1
            """);
        await Assert.That(history).IsNotNull();
        using var before = JsonDocument.Parse(history!);
        await Assert.That(before.RootElement.GetProperty("device").GetString()).IsEqualTo(session.DeviceId.ToString("N"));
        await world.AssertConvergedAsync();
    }
}
