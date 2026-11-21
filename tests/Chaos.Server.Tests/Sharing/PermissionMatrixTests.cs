using System.Net;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;

namespace Chaos.Server.Tests.Sharing;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class PermissionMatrixTests(PostgresServer postgres)
{
    private static readonly Dictionary<string, string[]> ExpectedGroups = Parse("""
        profile=create,name,zone,locale,dates,planning,reminders,quiet,appearance,day_background,deleted
        space=create,name,look,settings,deleted
        space_member=create,profile,role,state
        container=create,name,position,deadline,archived,category,deleted
        task_template=create,name,payload,deleted
        section=create,title,position,deleted
        tag=create,name,parent,color,position,deleted
        google_account=create,deleted
        board_setting=create,grouping,limits,deleted
        saved_view=create,name,look,pinned,position,definition,layout,alerts,deleted
        calendar_source=create,name,look,busy,enabled,owner,position,deleted
        task=create,title,notes,status,assignment,place,position,priority,estimate,category,planned,due,deleted
        task_assignee=create,done,deleted
        calendar_event_link=create,deleted
        subtask=create,title,notes,status,position,deleted
        task_tag=deleted
        subtask_tag=deleted
        diary_entry=create,entry,deleted
        task_history=record
        pad=create,text,position,archived,deleted
        pad_drawing=strokes,preview
        scratch_task_link=anchor
        pad_tag=deleted
        recurrence_series=create,rule,ended,deleted
        recurrence_occurrence=create,task,state,edit,series,deleted
        recurrence_exception=exception
        reminder_rule=create,rule,deleted
        reminder_acknowledgement=decision
        waiting_detail=create,person,deleted
        follow_up=create,kind,schedule,resolved,deleted
        day_plan=create,seed,initialized
        day_plan_item=create,position,removed
        week_plan=create,state
        week_goal=create,title,position,project,outcome
        week_goal_task=deleted
        week_assignment=create,placement
        availability_rule=minutes
        week_availability_override=minutes
        summary=record
        import_batch=record
        imported_item=record
        github_connection=create,settings,runner,state,deleted
        github_repository=create,settings,state,deleted
        github_issue_link=create,base,state,assignee_intent
        github_checklist_item=base
        github_comment_link=create,state
        github_label_tag=mapping
        github_milestone_project=mapping
        page=create,title,text,tree,position,deleted
        page_task_link=anchor
        page_tag=deleted
        attachment=create,name,file,deleted
        """);

    [Test]
    public async Task PermissionMatrix_EveryKindGroupHasARow()
    {
        var actual = new SyncKindCatalog().All.ToDictionary(kind => kind.Kind, StringComparer.Ordinal);
        await Assert.That(actual.Keys).IsEquivalentTo(ExpectedGroups.Keys);
        foreach (var (kind, groups) in ExpectedGroups)
        {
            await Assert.That(actual[kind].Groups.Select(group => group.Name)).IsEquivalentTo(groups);
            await Assert.That(actual[kind].Groups.All(group => Enum.IsDefined(group.Write))).IsTrue();
        }
    }

    [Test]
    public async Task Assignments_OnlyAcceptLiveEditorsAndAreTombstonedOnRemoval()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var world = new SharingHarness(server);
        var owner = await world.SignInAsync("assignment-owner@example.com");
        var editor = await world.SignInAsync("assignment-editor@example.com");
        var viewer = await world.SignInAsync("assignment-viewer@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await world.JoinAsync(viewer, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));
        var task = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("task", task, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Instant() }))).Rejections()).IsEmpty();
        async Task<RawResponse> Assign(SharingCaller actor, Guid member) => await world.CallAsync(actor, HttpMethod.Post,
            "/v2/sync/push", new { changes = new[] { SharingHarness.Change("task_assignee", $"{task}|{member:D}", "create", new { AddedAtUtc = Instant() }) } }, schema: 5);
        await Assert.That((await Assign(owner, editor.ProfileId)).Rejections()).IsEmpty();
        await Assert.That((await Assign(owner, viewer.ProfileId)).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await Assign(viewer, owner.ProfileId)).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.CallAsync(owner, HttpMethod.Delete, $"/v2/spaces/{space:D}/members/{editor.ProfileId:D}")).Status).IsEqualTo(HttpStatusCode.OK);
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.SyncEntities,
            e => e.AccountId == space && e.Kind == "task_assignee" && e.EntityKey == $"{task}|{editor.ProfileId:D}");
        await Assert.That(SyncJson.ReadGroups(row.Groups)["deleted"].Values.Values["DeletedAtUtc"]).IsNotNull();
        await Assert.That((await Assign(owner, editor.ProfileId)).Rejections()).Contains(SyncRejection.Forbidden);
    }

    [Test]
    public async Task PermissionMatrix_AllRepresentable8_4CellsAreEnforced()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("permission-owner@example.com");
        var editor = await world.SignInAsync("permission-editor@example.com");
        var viewer = await world.SignInAsync("permission-viewer@example.com");
        var outsider = await world.SignInAsync("permission-outsider@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await world.JoinAsync(viewer, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));

        foreach (var member in new[] { owner, editor, viewer })
        {
            await Assert.That((await world.CallAsync(member, HttpMethod.Get,
                $"/v2/sync/pull?stream={space:D}&after=0")).Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await world.CallAsync(member, HttpMethod.Get,
                $"/v2/sync/snapshot?stream={space:D}&kind=space_member")).Status).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Get,
            $"/v2/sync/pull?stream={space:D}&after=0")).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Get,
            $"/v2/sync/snapshot?stream={space:D}&kind=space_member")).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Get,
            $"/v2/sync/events?stream={space:D}")).Status).IsEqualTo(HttpStatusCode.Forbidden);

        var parentTask = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("task", parentTask, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Instant() }))).Rejections()).IsEmpty();
        foreach (var kind in new[]
        {
            ("task", "title", (object)new { Title = "task" }),
            ("container", "name", (object)new { Name = "list", NormalizedName = "list" }),
            ("subtask", "title", (object)new { Title = "subtask" }),
            ("page", "title", (object)new { Title = "page" }),
            ("pad", "text", (object)new { Text = "pad" }),
        })
        {
            var ownerKey = await CreateWorkAsync(world, owner, kind.Item1, space, parentTask);
            var editorKey = await CreateWorkAsync(world, editor, kind.Item1, space, parentTask);
            await AssertRejectedAsync(world, viewer, kind.Item1, kind.Item2, kind.Item3, space, parentTask,
                SyncRejection.Forbidden);
            await AssertRejectedAsync(world, outsider, kind.Item1, kind.Item2, kind.Item3, space, parentTask,
                SyncRejection.NotMember);
            await Assert.That((await world.PushAsync(editor, SharingHarness.Change(kind.Item1, ownerKey, kind.Item2, kind.Item3)))
                .Rejections()).IsEmpty();
            await Assert.That((await world.PushAsync(viewer, SharingHarness.Change(kind.Item1, ownerKey, kind.Item2, kind.Item3)))
                .Rejections()).Contains(SyncRejection.Forbidden);
            await Assert.That((await world.PushAsync(outsider, SharingHarness.Change(kind.Item1, ownerKey, kind.Item2, kind.Item3)))
                .Rejections()).Contains(SyncRejection.NotMember);
            await Assert.That((await world.PushAsync(editor, SharingHarness.Change(kind.Item1, editorKey, "deleted",
                new { DeletedAtUtc = Instant() }))).Rejections()).IsEmpty();
            await Assert.That((await world.PushAsync(viewer, SharingHarness.Change(kind.Item1, ownerKey, "deleted",
                new { DeletedAtUtc = Instant() }))).Rejections()).Contains(SyncRejection.Forbidden);
        }

        var commentId = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(viewer, SharingHarness.Change("diary_entry", commentId, "create",
            new { TaskId = parentTask, AuthorProfileId = viewer.ProfileId.ToString("D"), EntryAtUtc = Instant(), CreatedAtUtc = Instant() })))
            .Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor, SharingHarness.Change("diary_entry", commentId, "entry",
            new { RawMarkdown = "forged", EditedAtUtc = Instant() }))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(viewer, SharingHarness.Change("diary_entry", commentId, "deleted",
            new { DeletedAtUtc = Instant() }))).Rejections()).IsEmpty();
        var otherComment = Guid.NewGuid().ToString("D");
        await world.PushAsync(viewer, SharingHarness.Change("diary_entry", otherComment, "create",
            new { TaskId = parentTask, AuthorProfileId = viewer.ProfileId.ToString("D"), EntryAtUtc = Instant(), CreatedAtUtc = Instant() }));
        await Assert.That((await world.PushAsync(editor, SharingHarness.Change("diary_entry", otherComment, "deleted",
            new { DeletedAtUtc = Instant() }))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("diary_entry", otherComment, "deleted",
            new { DeletedAtUtc = Instant() }))).Rejections()).IsEmpty();

        foreach (var actor in new[] { owner, editor, viewer, outsider })
        {
            var expected = actor == owner ? (string?)null
                : actor == outsider ? SyncRejection.NotMember : SyncRejection.Forbidden;
            var result = await world.PushAsync(actor, SharingHarness.Change("space", space.ToString("D"), "name",
                new { Name = actor.Email }));
            await Assert.That(result.Rejections().Count(reason => reason == expected)).IsEqualTo(expected is null ? 0 : 1);
        }

        var unlockedEditorTag = await world.PushAsync(editor, NewSharedTag(space, editor));
        await Assert.That(unlockedEditorTag.Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("space", space.ToString("D"), "settings",
            new
            {
                TagsOwnerOnly = 1L, AgentsAllowed = 0L, ClaimMinutes = 120L,
                DefaultDeadlineReminderJson = (string?)null, AllowAgentsToClose = 0L,
            }))).Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor, NewSharedTag(space, editor))).Rejections())
            .Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(viewer, NewSharedTag(space, viewer))).Rejections())
            .Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(outsider, NewSharedTag(space, outsider))).Rejections())
            .Contains(SyncRejection.NotMember);

        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("github_connection",
            Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = space.ToString("D"), OwnerProfileId = owner.ProfileId.ToString("D"), CreatedAtUtc = Instant() })))
            .Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor, SharingHarness.Change("github_connection",
            Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = space.ToString("D"), OwnerProfileId = editor.ProfileId.ToString("D"), CreatedAtUtc = Instant() })))
            .Rejections()).Contains(SyncRejection.Forbidden);

        await Assert.That((await world.CallAsync(editor, HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{viewer.ProfileId:D}", new MemberRoleRequest(SpaceMember.EditorRole))).Status)
            .IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(viewer, HttpMethod.Delete,
            $"/v2/spaces/{space:D}/members/{editor.ProfileId:D}")).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{viewer.ProfileId:D}", new MemberRoleRequest(SpaceMember.EditorRole))).Status)
            .IsEqualTo(HttpStatusCode.Forbidden);
    }

    private static async Task<string> CreateWorkAsync(SharingHarness world, SharingCaller actor, string kind,
        Guid space, string parentTask)
    {
        var key = Guid.NewGuid().ToString("D");
        var change = kind == "subtask"
            ? SharingHarness.Change(kind, key, "create", new { TaskId = parentTask, CreatedAtUtc = Instant() })
            : SharingHarness.Change(kind, key, "create",
                new { SpaceId = space.ToString("D"), CreatedAtUtc = Instant() });
        var pushed = await world.PushAsync(actor, change);
        if (pushed.Rejections().Count > 0)
        {
            throw new InvalidOperationException($"Could not create {kind}: {string.Join(',', pushed.Rejections())}");
        }
        return key;
    }

    private static async Task AssertRejectedAsync(SharingHarness world, SharingCaller actor, string kind, string group,
        object values, Guid space, string parentTask, string reason)
    {
        var key = Guid.NewGuid().ToString("D");
        var create = kind == "subtask"
            ? SharingHarness.Change(kind, key, "create", new { TaskId = parentTask, CreatedAtUtc = Instant() })
            : SharingHarness.Change(kind, key, "create",
                new { SpaceId = space.ToString("D"), CreatedAtUtc = Instant() });
        await Assert.That((await world.PushAsync(actor, create)).Rejections()).Contains(reason);
    }

    private static RawChange NewSharedTag(Guid space, SharingCaller actor) =>
        SharingHarness.Change("tag", Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = space.ToString("D"), OwnerProfileId = actor.ProfileId.ToString("D"), CreatedAtUtc = Instant() });

    private static string Instant() => DateTimeOffset.UtcNow.ToString("o");

    private static Dictionary<string, string[]> Parse(string rows) =>
        rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
}
