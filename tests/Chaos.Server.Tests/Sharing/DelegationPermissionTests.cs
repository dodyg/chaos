using System.Net;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sharing;

/// <summary>
/// Delegation writes in a space stream (PLAN3 9.11): the delegator alone creates and briefs, the
/// transition table decides who may move the state, nudges come from the delegator of a live
/// delegation within the rate limit, and a member's departure ends their delegations.
/// </summary>
[NotInParallel]
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class DelegationPermissionTests(PostgresServer postgres)
{
    private const int Schema = 10;

    private static long _tick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A change with several groups on one rising clock, so later pushes win their merges.</summary>
    private static RawChange Change(string kind, string key, params (string Group, object Values)[] groups) =>
        new(Guid.NewGuid().ToString("D"), kind, key, "upsert", groups.ToDictionary(group => group.Group,
            group => new RawGroup(Hlc.Create(System.Threading.Interlocked.Increment(ref _tick), 0, Guid.Parse("11111111-1111-1111-1111-111111111111")).Encode(), group.Values),
            StringComparer.Ordinal));

    private static async Task<RawResponse> PushAsync(SharingHarness world, SharingCaller caller, params RawChange[] changes) =>
        await world.CallAsync(caller, HttpMethod.Post, "/v2/sync/push", System.Text.Json.JsonSerializer.Serialize(new { changes }), version: 2, schema: Schema);

    private static string Instant() => DateTimeOffset.UtcNow.ToString("o");

    private sealed record World(SharingHarness Harness, SharingCaller Owner, SharingCaller Editor, SharingCaller Viewer, Guid Space, string Task);

    private static async Task<World> WorldAsync(TestServer server, string prefix)
    {
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync($"{prefix}-owner@example.com", schema: Schema);
        var editor = await world.SignInAsync($"{prefix}-editor@example.com", schema: Schema);
        var viewer = await world.SignInAsync($"{prefix}-viewer@example.com", schema: Schema);
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await world.JoinAsync(viewer, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));
        var task = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("task", task, "create", new { SpaceId = space.ToString("D"), CreatedAtUtc = Instant() }))).Rejections()).IsEmpty();
        return new(world, owner, editor, viewer, space, task);
    }

    private static RawChange NewDelegation(World world, string key, Guid to, bool withBrief = true)
    {
        var groups = new List<(string, object)>
        {
            ("create", (object)new { TaskId = world.Task, FromMemberId = world.Owner.ProfileId.ToString("D"), ToMemberId = to.ToString("D"), CreatedAtUtc = Instant() }),
            ("state", new { State = 0L, StateChangedAtUtc = Instant() }),
        };
        if (withBrief) groups.Add(("brief", new { Message = "Please", DoneDefinition = "Sent", DueDate = (string?)null, ReviewRequired = 1L }));
        return Change("task_delegation", key, [.. groups]);
    }

    [Test]
    public async Task Delegation_OnlyTheDelegatorCreatesAndBriefs_AndTheTableGovernsTheState()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = await WorldAsync(server, "delegation");
        var key = Guid.NewGuid().ToString("D");

        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key,
            ("create", new { TaskId = world.Task, FromMemberId = world.Owner.ProfileId.ToString("D"), ToMemberId = world.Editor.ProfileId.ToString("D"), CreatedAtUtc = Instant() }),
            ("state", new { State = 0L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Owner, NewDelegation(world, key, world.Viewer.ProfileId))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key,
            ("create", new { TaskId = world.Task, FromMemberId = world.Owner.ProfileId.ToString("D"), ToMemberId = world.Editor.ProfileId.ToString("D"), CreatedAtUtc = Instant() }),
            ("state", new { State = 1L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Viewer, NewDelegation(world, key, world.Editor.ProfileId))).Rejections()).Contains(SyncRejection.Forbidden);

        await Assert.That((await PushAsync(world.Harness, world.Owner, NewDelegation(world, key, world.Editor.ProfileId))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("brief", new { Message = "Changed", DoneDefinition = "", DueDate = (string?)null, ReviewRequired = 0L })))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("brief", new { Message = "Forged", DoneDefinition = "", DueDate = (string?)null, ReviewRequired = 0L })))).Rejections()).Contains(SyncRejection.Forbidden);

        // Pending: the recipient may answer; the delegator may not accept on their behalf, and nobody jumps to Done.
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("state", new { State = 1L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("state", new { State = 4L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Viewer, Change("task_delegation", key, ("state", new { State = 1L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("state", new { State = 1L, StateChangedAtUtc = Instant() })))).Rejections()).IsEmpty();
        // Accepted: the recipient reports done (review) and the delegator closes or sends back; the delegator may withdraw.
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("state", new { State = 4L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("state", new { State = 5L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("state", new { State = 4L, StateChangedAtUtc = Instant() })))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("state", new { State = 5L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("state", new { State = 6L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("state", new { State = 1L, StateChangedAtUtc = Instant() })))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("state", new { State = 6L, StateChangedAtUtc = Instant() })))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("task_delegation", key, ("state", new { State = 1L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", key, ("state", new { State = 9L, StateChangedAtUtc = Instant() })))).Rejections()).Contains(SyncRejection.Invalid);
    }

    [Test]
    public async Task Nudges_ComeFromTheDelegatorOfALiveDelegation_AndAreRateLimited()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = await WorldAsync(server, "nudge");
        var delegation = Guid.NewGuid().ToString("D");
        await PushAsync(world.Harness, world.Owner, NewDelegation(world, delegation, world.Editor.ProfileId));
        RawChange Nudge(Guid from) => Change("nudge", Guid.NewGuid().ToString("D"),
            ("record", new { TaskId = world.Task, DelegationId = delegation, FromMemberId = from.ToString("D"), ToMemberId = world.Editor.ProfileId.ToString("D"), Text = "Any news?", SentAtUtc = Instant(), CreatedAtUtc = Instant() }));

        await Assert.That((await PushAsync(world.Harness, world.Editor, Nudge(world.Editor.ProfileId))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Editor, Nudge(world.Owner.ProfileId))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await PushAsync(world.Harness, world.Viewer, Nudge(world.Owner.ProfileId))).Rejections()).Contains(SyncRejection.Forbidden);
        for (var index = 0; index < 3; index++)
            await Assert.That((await PushAsync(world.Harness, world.Owner, Nudge(world.Owner.ProfileId))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Owner, Nudge(world.Owner.ProfileId))).Rejections()).Contains(SyncRejection.RateLimited);
        await Assert.That((await PushAsync(world.Harness, world.Owner, Change("nudge", Guid.NewGuid().ToString("D"),
            ("record", new { TaskId = world.Task, DelegationId = Guid.NewGuid().ToString("D"), FromMemberId = world.Owner.ProfileId.ToString("D"), ToMemberId = world.Editor.ProfileId.ToString("D"), Text = "x", SentAtUtc = Instant(), CreatedAtUtc = Instant() })))).Rejections())
            .Contains(SyncRejection.ParentUnknown);

        // A declined delegation has nothing to nudge about.
        await Assert.That((await PushAsync(world.Harness, world.Editor, Change("task_delegation", delegation, ("state", new { State = 3L, StateChangedAtUtc = Instant(), Response = "No" })))).Rejections()).IsEmpty();
        await Assert.That((await PushAsync(world.Harness, world.Owner, Nudge(world.Owner.ProfileId))).Rejections()).Contains(SyncRejection.Forbidden);
    }

    [Test]
    public async Task MemberLeaving_EndsTheirDelegations_AndADeclineOnLeavingHandsTheTaskBack()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = await WorldAsync(server, "leave");
        var delegation = Guid.NewGuid().ToString("D");
        await PushAsync(world.Harness, world.Owner, NewDelegation(world, delegation, world.Editor.ProfileId));
        await PushAsync(world.Harness, world.Owner, SharingHarness.Change("task_assignee", $"{world.Task}|{world.Editor.ProfileId:D}", "create", new { AddedAtUtc = Instant() }));
        await Assert.That((await world.Harness.CallAsync(world.Owner, HttpMethod.Delete, $"/v2/spaces/{world.Space:D}/members/{world.Editor.ProfileId:D}")).Status).IsEqualTo(HttpStatusCode.OK);

        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.SyncEntities,
                e => e.AccountId == world.Space && e.Kind == "task_delegation" && e.EntityKey == delegation);
            var groups = SyncJson.ReadGroups(row.Groups);
            await Assert.That(groups["state"].Values.Values["State"]).IsEqualTo(3L);
            await Assert.That(groups["state"].Values.Values["Response"]).IsEqualTo("Left the space");
            var assignee = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.SyncEntities,
                e => e.AccountId == world.Space && e.Kind == "task_assignee" && e.EntityKey == $"{world.Task}|{world.Owner.ProfileId:D}");
            await Assert.That(SyncJson.ReadGroups(assignee.Groups)["create"].Values.Values["AddedAtUtc"]).IsNotNull();
            var editorRow = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.SyncEntities,
                e => e.AccountId == world.Space && e.Kind == "task_assignee" && e.EntityKey == $"{world.Task}|{world.Editor.ProfileId:D}");
            await Assert.That(SyncJson.ReadGroups(editorRow.Groups)["deleted"].Values.Values["DeletedAtUtc"]).IsNotNull();
        }

        // A second owner delegates; when that delegator leaves, the delegation is withdrawn.
        var secondOwner = await world.Harness.SignInAsync("leave-second@example.com", schema: Schema);
        await world.Harness.JoinAsync(secondOwner, await world.Harness.InviteLinkAsync(world.Owner, world.Space, SpaceMember.EditorRole));
        await Assert.That((await world.Harness.CallAsync(world.Owner, HttpMethod.Patch, $"/v2/spaces/{world.Space:D}/members/{secondOwner.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.OwnerRole))).Status).IsEqualTo(HttpStatusCode.OK);
        var second = Guid.NewGuid().ToString("D");
        await Assert.That((await PushAsync(world.Harness, secondOwner, Change("task_delegation", second,
            ("create", new { TaskId = world.Task, FromMemberId = secondOwner.ProfileId.ToString("D"), ToMemberId = world.Owner.ProfileId.ToString("D"), CreatedAtUtc = Instant() }),
            ("state", new { State = 0L, StateChangedAtUtc = Instant() })))).Rejections()).IsEmpty();
        await Assert.That((await world.Harness.CallAsync(secondOwner, HttpMethod.Post, $"/v2/spaces/{world.Space:D}/leave")).Status).IsEqualTo(HttpStatusCode.OK);
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.SyncEntities,
                e => e.AccountId == world.Space && e.Kind == "task_delegation" && e.EntityKey == second);
            await Assert.That(SyncJson.ReadGroups(row.Groups)["state"].Values.Values["State"]).IsEqualTo(6L);
        }
    }
}
