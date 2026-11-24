using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Server.Web;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sharing;

/// <summary>
/// PLAN3 Q21: the PWA reads the account stream plus the member space streams, its writes go through the v2 router and
/// permissions, and a removed member loses the space at once. Three real accounts against the in-process server.
/// </summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class WebSharingTests(PostgresServer postgres)
{
    private const string OwnerEmail = "web-owner@example.com";
    private const string EditorEmail = "web-editor@example.com";
    private const string ViewerEmail = "web-viewer@example.com";

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    [Test]
    public async Task Member_SeesAndCompletesASharedTaskOnTheWeb_AndTheChangeLandsInTheSpaceStream()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var shared = await SharedSpaceAsync(server);
        using var editor = shared.EditorBrowser;
        var session = await WebFlow.SessionAsync(editor);
        await using var db = ServerDatabase.Open(server.ConnectionString);

        var groups = (await editor.GetFromJsonAsync<WebLists>("/v1/web/lists"))!;
        var personal = groups.Spaces.Single(space => !space.IsShared);
        await Assert.That(personal.Name).IsEqualTo("Personal");
        await Assert.That(personal.Role).IsNull();
        await Assert.That(personal.Lists).IsEmpty(); // an Inbox is bound, but the editor pushed no Personal list
        var studio = groups.Spaces.Single(space => space.IsShared);
        await Assert.That(studio.Name).IsEqualTo("Studio");
        await Assert.That(studio.Role).IsEqualTo(SpaceMember.EditorRole);
        await Assert.That(studio.CanChange).IsTrue();
        await Assert.That(studio.Lists.Single().Id.ToString("D")).IsEqualTo(shared.List);
        var list = (await editor.GetFromJsonAsync<ListTasks>($"/v1/web/lists/{shared.List}/tasks"))!;
        await Assert.That(list.List.Name).IsEqualTo("Planning");
        await Assert.That(list.List.SpaceId).IsEqualTo(shared.Space);
        var rows = list.Tasks.OrderBy(task => task.Title, StringComparer.Ordinal).ToList();
        await Assert.That(rows.Select(task => task.Title)).IsEquivalentTo(["Book the studio", "Pay the deposit"]);
        await Assert.That(rows.All(task => task.SpaceId == shared.Space && task.CanChange)).IsTrue();
        var assigned = rows.Single(task => task.Title == "Pay the deposit");
        await Assert.That(assigned.Assignees.Single().Name).IsEqualTo("Sara");
        await Assert.That(assigned.Assignees.Single().IsMe).IsTrue();
        await Assert.That(rows.Single(task => task.Title == "Book the studio").Assignees).IsEmpty();
        await Assert.That(rows.All(task => task.Updates.Count == 0)).IsTrue();

        var detail = (await editor.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{shared.Open}"))!;
        var update = detail.Updates.Single();
        await Assert.That(update.Author).IsEqualTo("Omar");
        await Assert.That(update.IsMine).IsFalse();
        await Assert.That(update.Markdown).IsEqualTo("Booked for Friday.");
        await Assert.That(detail.Notes).IsEqualTo("The one by the canal.");
        await Assert.That(detail.CanChange).IsTrue();

        using var completed = await WebFlow.PostAsync(editor, session, $"/v1/web/tasks/{shared.Open}/complete");
        await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var done = (await completed.Content.ReadFromJsonAsync<WebTask>())!;
        await Assert.That(done.IsTerminal).IsTrue();
        await Assert.That(done.Status).IsEqualTo(WebReadModel.Done);
        var change = await db.SyncChanges.AsNoTracking().Where(c => c.AccountId == shared.Space && c.Kind == "task" && c.EntityKey == shared.Open)
            .OrderBy(c => c.Seq).LastAsync();
        await Assert.That(change.OriginDevice).IsEqualTo(session.DeviceId);
        await Assert.That(change.ActorId).IsEqualTo(shared.Editor.ProfileId);
        await Assert.That(SyncJson.ReadGroups(change.Groups).Keys.Single()).IsEqualTo("status");
        var stored = SyncJson.ReadGroups((await db.SyncEntities.AsNoTracking()
            .SingleAsync(e => e.AccountId == shared.Space && e.Kind == "task" && e.EntityKey == shared.Open)).Groups);
        await Assert.That(stored["status"].Values.Values["Status"]).IsEqualTo(4L);

        // Capture into the shared list creates the task in the space stream; the retry is a no-op.
        var id = Guid.NewGuid();
        var capture = new WebCaptureRequest("Send the invoice", shared.ListId, null, id);
        using var added = await WebFlow.PostAsync(editor, session, "/v1/web/tasks", capture);
        await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var task = (await added.Content.ReadFromJsonAsync<WebTask>())!;
        await Assert.That(task.SpaceId).IsEqualTo(shared.Space);
        await Assert.That(task.ListId).IsEqualTo(shared.ListId);
        await Assert.That(task.Updates).IsEmpty();
        using var replay = await WebFlow.PostAsync(editor, session, "/v1/web/tasks", capture);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var created = await db.SyncEntities.AsNoTracking().SingleAsync(e => e.Kind == "task" && e.EntityKey == id.ToString("D"));
        await Assert.That(created.AccountId).IsEqualTo(shared.Space);
        await Assert.That(SyncJson.ReadGroups(created.Groups)["create"].Values.Values["SpaceId"]).IsEqualTo(shared.Space.ToString("D"));

        // An owner's desktop reads the same state from the space stream.
        var snapshot = await shared.World.CallAsync(shared.Owner, HttpMethod.Get, $"/v2/sync/snapshot?stream={shared.Space:D}&kind=task");
        await Assert.That(snapshot.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(snapshot.Body).Contains(shared.Open).And.Contains(id.ToString("D"));
    }

    [Test]
    public async Task Member_CannotCompleteOnTheWebATaskWhoseAssigneesMustEachComplete()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var shared = await SharedSpaceAsync(server);
        using var editor = shared.EditorBrowser;
        var session = await WebFlow.SessionAsync(editor);

        // "Pay the deposit" is assigned to Sara with each-assignee completion; the app disables its check box on this flag.
        var list = (await editor.GetFromJsonAsync<ListTasks>($"/v1/web/lists/{shared.List}/tasks"))!;
        await Assert.That(list.Tasks.Single(task => task.Title == "Pay the deposit").EachMustComplete).IsTrue();
        await Assert.That(list.Tasks.Single(task => task.Title == "Book the studio").EachMustComplete).IsFalse();
        var detail = (await editor.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{shared.Assigned}"))!;
        await Assert.That(detail.EachMustComplete).IsTrue();
        await Assert.That(detail.CanChange).IsTrue();

        // The server refuses too: the rule that every live assignee completes first runs on the desktop, not here.
        using var refused = await WebFlow.PostAsync(editor, session, $"/v1/web/tasks/{shared.Assigned}/complete");
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Complete your part of this task on your desktop.");
        await using var db = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(await db.SyncChanges.AsNoTracking()
            .AnyAsync(c => c.AccountId == shared.Space && c.EntityKey == shared.Assigned && c.OriginDevice == session.DeviceId)).IsFalse();
        var stored = SyncJson.ReadGroups((await db.SyncEntities.AsNoTracking()
            .SingleAsync(e => e.AccountId == shared.Space && e.Kind == "task" && e.EntityKey == shared.Assigned)).Groups);
        await Assert.That(stored["status"].Values.Values["Status"]).IsEqualTo(0L);
    }

    [Test]
    public async Task Viewer_ReadsASharedList_ButCannotChangeItsTasks_AndCanStillPlanItPersonally()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var shared = await SharedSpaceAsync(server);
        using var viewer = shared.ViewerBrowser;
        var session = await WebFlow.SessionAsync(viewer);

        var groups = (await viewer.GetFromJsonAsync<WebLists>("/v1/web/lists"))!;
        var studio = groups.Spaces.Single(space => space.IsShared);
        await Assert.That(studio.Role).IsEqualTo(SpaceMember.ViewerRole);
        await Assert.That(studio.CanChange).IsFalse();
        var list = (await viewer.GetFromJsonAsync<ListTasks>($"/v1/web/lists/{shared.List}/tasks"))!;
        await Assert.That(list.Tasks.All(task => task.CanChange == false)).IsTrue();
        var seen = list.Tasks.Single(task => task.Title == "Pay the deposit").Assignees.Single();
        await Assert.That(seen.Name).IsEqualTo("Sara");
        await Assert.That(seen.IsMe).IsFalse();
        var detail = (await viewer.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{shared.Open}"))!;
        await Assert.That(detail.Updates.Single().Author).IsEqualTo("Omar");
        await Assert.That(detail.Updates.Single().IsMine).IsFalse();

        using var complete = await WebFlow.PostAsync(viewer, session, $"/v1/web/tasks/{shared.Open}/complete");
        await Assert.That(complete.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await complete.Content.ReadAsStringAsync()).Contains("viewer of this space");
        using var capture = await WebFlow.PostAsync(viewer, session, "/v1/web/tasks",
            new WebCaptureRequest("Not allowed", shared.ListId, null, Guid.NewGuid()));
        await Assert.That(capture.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await using var db = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(await db.SyncChanges.AsNoTracking().AnyAsync(c => c.AccountId == shared.Space && c.OriginDevice == session.DeviceId))
            .IsFalse();

        // My Day is the viewer's own overlay on the shared task, as on the desktop.
        using var added = await WebFlow.PostAsync(viewer, session, $"/v1/web/tasks/{shared.Open}/add-to-my-day");
        await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var today = (await viewer.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(today.Tasks.Single().Id.ToString("D")).IsEqualTo(shared.Open);
        await Assert.That(today.Tasks.Single().IsOnMyDay).IsTrue();
        await Assert.That(today.Tasks.Single().CanChange).IsFalse();
    }

    [Test]
    public async Task RemovedMember_LosesAccessAtOnce_AndSpaceEventsReachTheWebStream()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var shared = await SharedSpaceAsync(server);
        using var viewer = shared.ViewerBrowser;
        using var editor = shared.EditorBrowser;
        var viewerSession = await WebFlow.SessionAsync(viewer);
        var editorSession = await WebFlow.SessionAsync(editor);
        var before = (await viewer.GetFromJsonAsync<WebLists>("/v1/web/lists"))!;
        await Assert.That(before.Spaces.Any(space => space.Id == shared.Space)).IsTrue();

        // The editor's open app wakes when a desktop writes to the space stream.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var events = await editor.GetAsync($"/v1/web/events?device={editorSession.DeviceId:D}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(timeout.Token));
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("data: 0");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo(string.Empty);
        var renamed = await shared.World.PushAsync(shared.Owner, SharingHarness.Change("task", shared.Open, "title", new { Title = "Book the big studio" }));
        await Assert.That(renamed.Rejections()).IsEmpty();
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("data: 1");

        var removed = await shared.World.CallAsync(shared.Owner, HttpMethod.Delete, $"/v2/spaces/{shared.Space:D}/members/{shared.Viewer.ProfileId:D}");
        await Assert.That(removed.Status).IsEqualTo(HttpStatusCode.OK);

        using var goneList = await viewer.GetAsync($"/v1/web/lists/{shared.List}/tasks");
        await Assert.That(goneList.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var goneTask = await viewer.GetAsync($"/v1/web/tasks/{shared.Open}");
        await Assert.That(goneTask.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var goneWrite = await WebFlow.PostAsync(viewer, viewerSession, $"/v1/web/tasks/{shared.Open}/add-to-my-day");
        await Assert.That(goneWrite.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var goneCapture = await WebFlow.PostAsync(viewer, viewerSession, "/v1/web/tasks",
            new WebCaptureRequest("After removal", shared.ListId, null, Guid.NewGuid()));
        await Assert.That(goneCapture.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var after = (await viewer.GetFromJsonAsync<WebLists>("/v1/web/lists"))!;
        await Assert.That(after.Spaces.Any(space => space.Id == shared.Space)).IsFalse();
        var search = (await viewer.GetFromJsonAsync<WebSearchResult>("/v1/web/search?q=studio"))!;
        await Assert.That(search.Items).IsEmpty();
        var today = (await viewer.GetFromJsonAsync<WebToday>("/v1/web/today"))!;
        await Assert.That(today.Tasks).IsEmpty(); // the server tombstoned the viewer's My Day item for the shared task

        // The editor still sees the space and the renamed task.
        var kept = (await editor.GetFromJsonAsync<WebTask>($"/v1/web/tasks/{shared.Open}"))!;
        await Assert.That(kept.Title).IsEqualTo("Book the big studio");
    }

    /// <summary>
    /// Omar owns "Studio"; Sara is an editor and Mina a viewer. Omar's desktop pushed a list with two tasks into the space,
    /// the second assigned to Sara, and commented on the first. Sara and Mina each have a signed-in web browser.
    /// </summary>
    private static async Task<SharedWebSpace> SharedSpaceAsync(TestServer server)
    {
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync(OwnerEmail);
        var editor = await world.SignInAsync(EditorEmail);
        var viewer = await world.SignInAsync(ViewerEmail);
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            await db.Accounts.Where(a => a.PrimaryEmail == OwnerEmail).ExecuteUpdateAsync(s => s.SetProperty(a => a.DisplayName, "Omar"));
            await db.Accounts.Where(a => a.PrimaryEmail == EditorEmail).ExecuteUpdateAsync(s => s.SetProperty(a => a.DisplayName, "Sara"));
        }

        var space = await world.CreateSpaceAsync(owner, "Studio");
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await world.JoinAsync(viewer, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));
        var list = Guid.NewGuid().ToString("D");
        var open = Guid.NewGuid().ToString("D");
        var assigned = Guid.NewGuid().ToString("D");
        var comment = Guid.NewGuid().ToString("D");
        var spaceKey = space.ToString("D");
        // The assignment group and assignee rows arrived in client schema 5, so the pushes name the current schema.
        await Push(
            SharingHarness.Change("container", list, "create", new { SpaceId = spaceKey, Kind = 2L, IsInbox = 0L, CreatedAtUtc = Now() }),
            SharingHarness.Change("container", list, "name", new { Name = "Planning", NormalizedName = "planning" }),
            SharingHarness.Change("container", list, "position", new { Position = 0L }),
            SharingHarness.Change("task", open, "create", new { SpaceId = spaceKey, CreatedAtUtc = Now() }),
            SharingHarness.Change("task", open, "title", new { Title = "Book the studio" }),
            SharingHarness.Change("task", open, "notes", new { Notes = "The one by the canal." }),
            SharingHarness.Change("task", open, "status", Status()),
            SharingHarness.Change("task", open, "place", new { ContainerId = list, SectionId = (string?)null }),
            SharingHarness.Change("task", assigned, "create", new { SpaceId = spaceKey, CreatedAtUtc = Now() }),
            SharingHarness.Change("task", assigned, "title", new { Title = "Pay the deposit" }),
            SharingHarness.Change("task", assigned, "notes", new { Notes = string.Empty }),
            SharingHarness.Change("task", assigned, "status", Status()),
            SharingHarness.Change("task", assigned, "place", new { ContainerId = list, SectionId = (string?)null }),
            SharingHarness.Change("task", assigned, "assignment", new { AssignMode = 1L }),
            SharingHarness.Change("diary_entry", comment, "create",
                new { TaskId = open, AuthorProfileId = owner.ProfileId.ToString("D"), EntryAtUtc = Now(), CreatedAtUtc = Now() }));
        // A post's text is checked against its stored author, so it follows the create group in a later push.
        await Push(SharingHarness.Change("diary_entry", comment, "entry", new { RawMarkdown = "Booked for Friday.", EditedAtUtc = (string?)null }));
        await Push(SharingHarness.Change("task_assignee", $"{assigned}|{editor.ProfileId:D}", "create", new { AddedAtUtc = Now() }));

        var editorBrowser = server.NewBrowser();
        using var editorSignedIn = await WebFlow.SignInAsync(server, editorBrowser, EditorEmail);
        var viewerBrowser = server.NewBrowser();
        using var viewerSignedIn = await WebFlow.SignInAsync(server, viewerBrowser, ViewerEmail);
        return new SharedWebSpace(world, owner, editor, viewer, space, list, open, assigned, editorBrowser, viewerBrowser);

        async Task Push(params RawChange[] changes)
        {
            var pushed = await world.CallAsync(owner, HttpMethod.Post, "/v2/sync/push", JsonSerializer.Serialize(new { changes }), version: 2, schema: 13);
            await Assert.That(pushed.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(pushed.Rejections()).IsEmpty();
        }

        static object Status() => new { Status = 0L, CustomStateId = (string?)null, CompletedAtUtc = (string?)null, CancelledAtUtc = (string?)null };
    }

    /// <summary>The shape of <c>GET /v1/web/lists/{id}/tasks</c>.</summary>
    private sealed record ListTasks(WebList List, IReadOnlyList<WebTask> Tasks);

    private sealed record SharedWebSpace(SharingHarness World, SharingCaller Owner, SharingCaller Editor, SharingCaller Viewer,
        Guid Space, string List, string Open, string Assigned, HttpClient EditorBrowser, HttpClient ViewerBrowser) : IAsyncDisposable
    {
        public Guid ListId => Guid.Parse(List);

        public ValueTask DisposeAsync()
        {
            EditorBrowser.Dispose();
            ViewerBrowser.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
