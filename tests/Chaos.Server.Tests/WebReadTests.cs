using System.Net;
using System.Net.Http.Json;
using Chaos.Server.Data;
using Chaos.Server.Web;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chaos.Server.Tests;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class WebReadTests(PostgresServer postgres)
{
    [Test]
    public async Task Reads_UseAccountAndLiveGroups_AndTodayUsesDesktopPlanOrder()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var owner = server.NewBrowser();
        using var other = server.NewBrowser();
        using var anonymous = server.NewBrowser();
        using var signedIn = await WebFlow.SignInAsync(server, owner, "owner@example.com");
        using var otherSignedIn = await WebFlow.SignInAsync(server, other, "other@example.com");

        await using var scope = server.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChaosDbContext>();
        var account = await db.Accounts.SingleAsync(row => row.PrimaryEmail == "owner@example.com");
        var now = server.Clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var clock = Hlc.Create(now.ToUnixTimeMilliseconds(), 0, Guid.NewGuid());
        var space = Guid.NewGuid();
        var list = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var due = Guid.NewGuid();
        var planned = Guid.NewGuid();
        var overdue = Guid.NewGuid();
        var recurring = Guid.NewGuid();
        var series = Guid.NewGuid();
        var occurrence = Guid.NewGuid();
        var tombstone = Guid.NewGuid();
        var purged = Guid.NewGuid();
        account.ProfileId = profile;
        account.PersonalSpaceId = space;
        account.InboxId = list;

        db.SyncEntities.AddRange(
            Row("container", list, ("create", Fields(("SpaceId", space.ToString("D")))), ("name", Fields(("Name", "Inbox")))),
            Row("container", Guid.NewGuid(), ("create", Fields(("SpaceId", space.ToString("D"))))),
            Row("day_plan", plan, ("create", Fields(("ProfileId", profile.ToString("D")), ("LocalDate", today.ToString("yyyy-MM-dd"))))),
            Row("day_plan_item", Guid.NewGuid(),
                ("create", Fields(("DayPlanId", plan.ToString("D")), ("TaskId", planned.ToString("D")))),
                ("position", Fields(("Position", 0L)))),
            TaskRow(due, "Due today", ("due", Fields(("DueDate", today.ToString("yyyy-MM-dd")), ("DueTime", "15:30:00"), ("DueZoneId", "UTC"))),
                ("priority", Fields(("Priority", 4L)))),
            TaskRow(planned, "Planned today", ("planned", Fields(("PlannedDate", today.ToString("yyyy-MM-dd"))))),
            TaskRow(overdue, "Overdue", ("due", Fields(("DueDate", today.AddDays(-1).ToString("yyyy-MM-dd"))))),
            Row("recurrence_series", series, ("rule", Fields(("RuleJson", """{"payload":{"zoneId":"UTC","anchorTime":null}}""")))),
            Row("recurrence_occurrence", occurrence,
                ("create", Fields(("SeriesId", series.ToString("D")), ("EffectiveLocal", today.AddDays(-1).ToString("yyyy-MM-dd") + "T00:00:00"))),
                ("task", Fields(("TaskId", recurring.ToString("D")))), ("state", Fields(("State", 0L)))),
            Row("task", recurring,
                ("create", Fields(("SpaceId", space.ToString("D")), ("RecurrenceOccurrenceKey", occurrence.ToString("D")))),
                ("place", Fields(("ContainerId", list.ToString("D")))), ("title", Fields(("Title", "Missed recurrence"))),
                ("position", Fields(("Position", 1024L)))),
            TaskRow(Guid.NewGuid(), "Done", ("status", Fields(("Status", 4L))), ("due", Fields(("DueDate", today.ToString("yyyy-MM-dd"))))),
            TaskRow(Guid.NewGuid(), "Unscheduled"),
            TaskRow(tombstone, "Deleted", ("deleted", Fields(("DeletedAtUtc", now.ToString("o"))))));
        var purgedRow = TaskRow(purged, "Purged");
        purgedRow.PurgedAt = now;
        db.SyncEntities.Add(purgedRow);
        await db.SaveChangesAsync();

        using var noCookie = await anonymous.GetAsync("/v1/web/today");
        await Assert.That(noCookie.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var view = (await owner.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(view.Tasks.Select(task => task.Title).ToArray()).IsEquivalentTo(["Planned today", "Due today", "Overdue", "Missed recurrence"]);
        await Assert.That(view.Tasks[0].Id).IsEqualTo(planned);
        await Assert.That(view.Tasks[1].Id).IsEqualTo(due);
        await Assert.That(view.Tasks[1].DueTime).IsEqualTo(new TimeOnly(15, 30));
        await Assert.That(view.Tasks[3].IsRecurring).IsTrue();
        await Assert.That(view.Tasks[0].Notes).IsNull();
        var lists = (await owner.GetFromJsonAsync<WebList[]>("/v1/web/lists"))!;
        await Assert.That(lists.Single().Id).IsEqualTo(list);
        var detail = (await owner.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{planned}"))!;
        await Assert.That(detail.Notes).IsEqualTo("Synced notes");
        using var missing = await owner.GetAsync($"/v1/web/tasks/{tombstone}");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var gone = await owner.GetAsync($"/v1/web/tasks/{purged}");
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var foreignList = await other.GetAsync($"/v1/web/lists/{list}/tasks");
        await Assert.That(foreignList.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var foreignTask = await other.GetAsync($"/v1/web/tasks/{planned}");
        await Assert.That(foreignTask.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var foreignToday = (await other.GetFromJsonAsync<WebToday>($"/v1/web/today?accountId={account.Id}"))!;
        await Assert.That(foreignToday.Tasks.Count).IsEqualTo(0);
        using var foreignWrite = await WebFlow.PostAsync(other, await WebFlow.SessionAsync(other), $"/v1/web/tasks/{planned}/complete");
        await Assert.That(foreignWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var recurringWrite = await WebFlow.PostAsync(owner, await WebFlow.SessionAsync(owner), $"/v1/web/tasks/{recurring}/complete");
        await Assert.That(recurringWrite.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        SyncEntityRow TaskRow(Guid id, string title, params (string Name, SyncGroupValues Values)[] extras) =>
            Row("task", id,
                [("create", Fields(("SpaceId", space.ToString("D")))), ("place", Fields(("ContainerId", list.ToString("D")))),
                 ("title", Fields(("Title", title))), ("notes", Fields(("Notes", "Synced notes"))), .. extras]);

        SyncEntityRow Row(string kind, Guid id, params (string Name, SyncGroupValues Values)[] groups) => new()
        {
            AccountId = account.Id,
            Kind = kind,
            EntityKey = id.ToString("D"),
            Groups = SyncJson.WriteGroups(groups.ToDictionary(group => group.Name, group => new SyncGroupState(clock, group.Values))),
            UpdatedAt = now,
        };
    }

    private static SyncGroupValues Fields(params (string Column, object? Value)[] values) =>
        new(values.ToDictionary(field => field.Column, field => field.Value));
}
