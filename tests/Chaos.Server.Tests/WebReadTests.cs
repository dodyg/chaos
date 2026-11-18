using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        var unscheduled = Guid.NewGuid();
        var done = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var archivedList = Guid.NewGuid();
        var week = Guid.NewGuid();
        var tombstone = Guid.NewGuid();
        var purged = Guid.NewGuid();
        var calendar = Guid.NewGuid();
        var note = Guid.NewGuid();
        var page = Guid.NewGuid();
        account.ProfileId = profile;
        account.PersonalSpaceId = space;
        account.InboxId = list;

        db.SyncEntities.AddRange(
            Row("profile", profile, ("appearance", Fields(("AppearanceTheme", "{\"name\":\"Midnight\"}"))),
                ("day_background", Fields(("DayBackground", "{\"kind\":\"scene\",\"value\":\"sunset\"}")))),
            Row("container", list, ("create", Fields(("SpaceId", space.ToString("D")))), ("name", Fields(("Name", "Inbox")))),
            Row("container", archivedList, ("create", Fields(("SpaceId", space.ToString("D")))),
                ("name", Fields(("Name", "Archived"))), ("archived", Fields(("ArchivedAtUtc", now.ToString("o"))))),
            Row("container", Guid.NewGuid(), ("create", Fields(("SpaceId", space.ToString("D"))))),
            Row("day_plan", plan, ("create", Fields(("ProfileId", profile.ToString("D")), ("LocalDate", today.ToString("yyyy-MM-dd"))))),
            Member(planned, 1024),
            Member(unscheduled, 0),
            Member(done, 2048),
            Member(removed, 3072, now.ToString("o")),
            Member(tombstone, 4096),
            Member(purged, 5120),
            Row("week_plan", week, ("create", Fields(("ProfileId", profile.ToString("D")), ("WeekStartDate", today.ToString("yyyy-MM-dd")))),
                ("state", Fields(("State", 1L), ("ConfirmedRevision", 1L)))),
            Row("week_assignment", Guid.NewGuid(), ("create", Fields(("WeekPlanId", week.ToString("D")), ("TaskId", removed.ToString("D")))),
                ("placement", Fields(("LocalDate", today.ToString("yyyy-MM-dd")), ("Position", 0L)))),
            TaskRow(due, "Due today", ("due", Fields(("DueDate", today.ToString("yyyy-MM-dd")), ("DueTime", "15:30:00"), ("DueZoneId", "UTC"))),
                ("priority", Fields(("Priority", 4L)))),
            TaskRow(planned, "Planned today", ("planned", Fields(("PlannedDate", today.ToString("yyyy-MM-dd"))))),
            TaskRow(Guid.NewGuid(), "Planned today outside My Day", ("planned", Fields(("PlannedDate", today.ToString("yyyy-MM-dd"))))),
            TaskRow(overdue, "Overdue", ("due", Fields(("DueDate", today.AddDays(-1).ToString("yyyy-MM-dd"))))),
            Row("task", recurring,
                ("create", Fields(("SpaceId", space.ToString("D")), ("RecurrenceOccurrenceKey", "occurrence"))),
                ("place", Fields(("ContainerId", list.ToString("D")))), ("title", Fields(("Title", "Recurring")))),
            TaskRow(done, "Done", ("status", Fields(("Status", 4L)))),
            TaskRow(removed, "Removed"),
            Row("task", unscheduled, ("create", Fields(("SpaceId", space.ToString("D")))),
                ("place", Fields(("ContainerId", archivedList.ToString("D")))), ("title", Fields(("Title", "Unscheduled")))),
            TaskRow(tombstone, "Deleted", ("deleted", Fields(("DeletedAtUtc", now.ToString("o"))))));
        db.SyncEntities.AddRange(
            Row("pad", note, ("create", Fields(("SpaceId", space.ToString("D")), ("Kind", 0L))),
                ("text", Fields(("RawMarkdown", "**Private note**")))),
            Row("pad", Guid.NewGuid(), ("create", Fields(("SpaceId", space.ToString("D")))),
                ("deleted", Fields(("DeletedAtUtc", now.ToString("o"))))),
            Row("page", page, ("create", Fields(("SpaceId", space.ToString("D")), ("DailyDate", "2026-11-18"))),
                ("title", Fields(("Title", "Journal"))), ("text", Fields(("RawMarkdown", "# Hello")))),
            Row("page", Guid.NewGuid(), ("create", Fields(("SpaceId", Guid.NewGuid().ToString("D")))),
                ("title", Fields(("Title", "Other space")))));
        db.SyncEntities.Add(Row("calendar_source", calendar, ("enabled", Fields(("Enabled", 1L))),
            ("busy", Fields(("CountsAsBusy", 1L))), ("look", Fields(("Color", "#123456")))));
        db.SyncEntities.Add(new SyncEntityRow { AccountId = account.Id, Kind = "calendar_cache", EntityKey = calendar.ToString("D"),
            Groups = JsonSerializer.Serialize(new CalendarCacheSnapshot(calendar.ToString("D"), now,
                [new CalendarCacheEvent("meeting", now, now.AddHours(1), false, null, null, "Standup", null, "https://example.com", null, true, now),
                 new CalendarCacheEvent("other-day", now.AddDays(2), now.AddDays(2).AddHours(1), false, null, null, "Later", null, null, null, true, now)])), UpdatedAt = now });
        var purgedRow = TaskRow(purged, "Purged");
        purgedRow.PurgedAt = now;
        db.SyncEntities.Add(purgedRow);
        await db.SaveChangesAsync();

        using var noCookie = await anonymous.GetAsync("/v1/web/today");
        await Assert.That(noCookie.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var notes = (await owner.GetFromJsonAsync<WebNote[]>("/v1/web/pad"))!;
        await Assert.That(notes.Length).IsEqualTo(1);
        await Assert.That(notes[0].Markdown).IsEqualTo("**Private note**");
        var pages = (await owner.GetFromJsonAsync<WebPage[]>("/v1/web/pages"))!;
        await Assert.That(pages.Length).IsEqualTo(1);
        await Assert.That(pages[0].Markdown).IsEqualTo("# Hello");
        await Assert.That(pages[0].DailyDate).IsEqualTo(new DateOnly(2026, 11, 18));
        await Assert.That((await other.GetFromJsonAsync<WebNote[]>("/v1/web/pad"))!.Length).IsEqualTo(0);
        await Assert.That((await other.GetFromJsonAsync<WebPage[]>("/v1/web/pages"))!.Length).IsEqualTo(0);
        using var anonymousPad = await anonymous.GetAsync("/v1/web/pad");
        await Assert.That(anonymousPad.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        var taskSearch = (await owner.GetFromJsonAsync<WebSearchResult>("/v1/web/search?q=sYnCeD%20NoTeS"))!;
        await Assert.That(taskSearch.Items.Any(item => item.Kind == "task" && item.Id == planned)).IsTrue();
        var noteSearch = (await owner.GetFromJsonAsync<WebSearchResult>("/v1/web/search?q=private"))!;
        await Assert.That(noteSearch.Items.Single().Id).IsEqualTo(note);
        var pageSearch = (await owner.GetFromJsonAsync<WebSearchResult>("/v1/web/search?q=hello"))!;
        await Assert.That(pageSearch.Items.Single().Id).IsEqualTo(page);
        var deletedSearch = (await owner.GetFromJsonAsync<WebSearchResult>("/v1/web/search?q=Deleted"))!;
        await Assert.That(deletedSearch.Items.Count).IsEqualTo(0);
        var foreignSearch = (await other.GetFromJsonAsync<WebSearchResult>($"/v1/web/search?q=private&accountId={account.Id}"))!;
        await Assert.That(foreignSearch.Items.Count).IsEqualTo(0);
        using var anonymousSearch = await anonymous.GetAsync("/v1/web/search?q=private");
        await Assert.That(anonymousSearch.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var longSearch = await owner.GetAsync($"/v1/web/search?q={new string('x', 201)}");
        await Assert.That(longSearch.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var appearance = (await owner.GetFromJsonAsync<WebAppearance>("/v1/web/appearance"))!;
        await Assert.That(appearance.Theme).Contains("Midnight");
        await Assert.That(appearance.Background).Contains("sunset");
        var otherAppearance = (await other.GetFromJsonAsync<WebAppearance>("/v1/web/appearance"))!;
        await Assert.That(otherAppearance.Theme).IsNull();
        var view = (await owner.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(string.Join(", ", view.Tasks.Select(task => task.Title))).IsEqualTo("Unscheduled, Planned today, Done");
        await Assert.That(view.Meetings.Count).IsEqualTo(1);
        await Assert.That(view.Meetings[0].Title).IsEqualTo("Standup");
        await Assert.That(view.Meetings[0].Color).IsEqualTo("#123456");
        await Assert.That(view.Tasks[0].PlannedDate).IsNull();
        await Assert.That(view.Tasks[2].IsTerminal).IsTrue();
        await Assert.That(view.Tasks[1].Notes).IsNull();
        var lists = (await owner.GetFromJsonAsync<WebList[]>("/v1/web/lists"))!;
        await Assert.That(lists.First().Id).IsEqualTo(list);
        var detail = (await owner.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{planned}"))!;
        await Assert.That(detail.Notes).IsEqualTo("Synced notes");
        await Assert.That(detail.IsOnMyDay).IsTrue();
        var dueDetail = (await owner.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{due}"))!;
        await Assert.That(dueDetail.DueTime).IsEqualTo(new TimeOnly(15, 30));
        await Assert.That(dueDetail.IsOnMyDay).IsFalse();
        using var missing = await owner.GetAsync($"/v1/web/tasks/{tombstone}");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var gone = await owner.GetAsync($"/v1/web/tasks/{purged}");
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var foreignList = await other.GetAsync($"/v1/web/lists/{list}/tasks");
        await Assert.That(foreignList.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var foreignTask = await other.GetAsync($"/v1/web/tasks/{planned}");
        await Assert.That(foreignTask.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var foreignToday = (await other.GetFromJsonAsync<WebToday>($"/v1/web/today?accountId={account.Id}"))!;
        await Assert.That(foreignToday.Meetings.Count).IsEqualTo(0);
        await Assert.That(foreignToday.Tasks.Count).IsEqualTo(0);
        using var foreignWrite = await WebFlow.PostAsync(other, await WebFlow.SessionAsync(other), $"/v1/web/tasks/{planned}/complete");
        await Assert.That(foreignWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var recurringWrite = await WebFlow.PostAsync(owner, await WebFlow.SessionAsync(owner), $"/v1/web/tasks/{recurring}/complete");
        await Assert.That(recurringWrite.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // Before a day has been initialized, My Day uses the confirmed week's open assignments.
        await db.SyncEntities.Where(row => row.AccountId == account.Id && row.Kind == "day_plan").ExecuteDeleteAsync();
        var seeded = (await owner.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(seeded.Tasks.Single().Id).IsEqualTo(removed);
        var session = await WebFlow.SessionAsync(owner);
        using var finishSeed = await WebFlow.PostAsync(owner, session, $"/v1/web/tasks/{removed}/complete");
        await Assert.That(finishSeed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var afterFinish = (await owner.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(afterFinish.Tasks.Single().IsTerminal).IsTrue();
        using var reopenSeed = await WebFlow.PostAsync(owner, session, $"/v1/web/tasks/{removed}/reopen");
        await Assert.That(reopenSeed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var removeSeed = await WebFlow.PostAsync(owner, session, $"/v1/web/tasks/{removed}/remove-from-my-day");
        await Assert.That(removeSeed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var afterRemoval = (await owner.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(afterRemoval.Tasks.Count).IsEqualTo(0);

        SyncEntityRow Member(Guid taskId, long position, string? removedAt = null) =>
            Row("day_plan_item", Guid.NewGuid(),
                ("create", Fields(("DayPlanId", plan.ToString("D")), ("TaskId", taskId.ToString("D")))),
                ("position", Fields(("Position", position))), ("removed", Fields(("RemovedAtUtc", removedAt))));

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
