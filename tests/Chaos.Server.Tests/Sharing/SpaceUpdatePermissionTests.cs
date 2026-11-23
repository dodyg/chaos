using System.Net;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;

namespace Chaos.Server.Tests.Sharing;

/// <summary>
/// Standups (PLAN3 9.12) from the raw client, ignoring the UI: any member, a viewer included, may post one as
/// themself; the author alone edits it; the author or an owner deletes it; nobody posts one into a Personal space
/// or a space they do not belong to.
/// </summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class SpaceUpdatePermissionTests(PostgresServer postgres)
{
    private const int Schema = 11;

    [Test]
    public async Task Standup_ViewerPostsAsThemself_AuthorEdits_AuthorOrOwnerDeletes_OthersAreRefused()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var world = new SharingHarness(server);
        var owner = await world.SignInAsync("standup-owner@example.com", schema: Schema);
        var editor = await world.SignInAsync("standup-editor@example.com", schema: Schema);
        var viewer = await world.SignInAsync("standup-viewer@example.com", schema: Schema);
        var outsider = await world.SignInAsync("standup-outsider@example.com", schema: Schema);
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await world.JoinAsync(viewer, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));

        var standup = Guid.NewGuid().ToString("D");
        var created = SharingHarness.Change("space_update", standup, "create",
            new { SpaceId = space.ToString("D"), AuthorMemberId = viewer.ProfileId.ToString("D"), ForDate = "2026-11-23", CreatedAtUtc = Instant() });
        // A viewer is a commenter: posting under their own name is allowed.
        await Assert.That((await world.CallAsync(viewer, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { created } }), schema: Schema)).Rejections()).IsEmpty();
        // The same key again under another author is a forged post.
        await Assert.That((await world.CallAsync(editor, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { SharingHarness.Change("space_update", standup, "create",
                new { SpaceId = space.ToString("D"), AuthorMemberId = editor.ProfileId.ToString("D"), ForDate = "2026-11-23", CreatedAtUtc = Instant() }) } }),
            schema: Schema)).Rejections()).Contains(SyncRejection.Forbidden);
        // A post naming someone else as author is refused even under a new key.
        await Assert.That((await world.CallAsync(editor, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { SharingHarness.Change("space_update", Guid.NewGuid().ToString("D"), "create",
                new { SpaceId = space.ToString("D"), AuthorMemberId = owner.ProfileId.ToString("D"), ForDate = "2026-11-23", CreatedAtUtc = Instant() }) } }),
            schema: Schema)).Rejections()).Contains(SyncRejection.Forbidden);

        var text = SharingHarness.Change("space_update", standup, "entry", new { RawMarkdown = "## Done", EditedAtUtc = Instant() });
        await Assert.That((await world.CallAsync(editor, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { text } }), schema: Schema)).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { text } }), schema: Schema)).Rejections()).Contains(SyncRejection.NotMember);
        await Assert.That((await world.CallAsync(viewer, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { text } }), schema: Schema)).Rejections()).IsEmpty();

        var deletion = SharingHarness.Change("space_update", standup, "deleted", new { DeletedAtUtc = Instant() });
        await Assert.That((await world.CallAsync(editor, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { deletion } }), schema: Schema)).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.CallAsync(owner, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { deletion } }), schema: Schema)).Rejections()).IsEmpty();
        await Assert.That((await world.CallAsync(viewer, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { SharingHarness.Purge("space_update", standup) } }), schema: Schema)).Rejections())
            .Contains(SyncRejection.Forbidden);

        // A standup belongs to a shared space: the Personal space (the account stream) refuses it.
        await Assert.That((await world.CallAsync(owner, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { SharingHarness.Change("space_update", Guid.NewGuid().ToString("D"), "create",
                new { SpaceId = owner.PersonalSpaceId.ToString("D"), AuthorMemberId = owner.ProfileId.ToString("D"), ForDate = "2026-11-23", CreatedAtUtc = Instant() }) } }),
            schema: Schema)).Rejections()).Contains(SyncRejection.Forbidden);
        // A client too old to know the kind gets nothing through.
        await Assert.That((await world.CallAsync(owner, HttpMethod.Post, "/v2/sync/push",
            System.Text.Json.JsonSerializer.Serialize(new { changes = new[] { SharingHarness.Change("space_update", Guid.NewGuid().ToString("D"), "create",
                new { SpaceId = space.ToString("D"), AuthorMemberId = owner.ProfileId.ToString("D"), ForDate = "2026-11-23", CreatedAtUtc = Instant() }) } }),
            schema: 10)).Rejections()).Contains(SyncRejection.Invalid);
    }

    [Test]
    public async Task Standup_KindAndGroups_AreInThePermissionMatrix()
    {
        var kind = SyncKinds.Find("space_update")!;
        await Assert.That(kind.Route.Kind).IsEqualTo(SyncRouteKind.OwnSpace);
        await Assert.That(kind.Group("create")!.Write).IsEqualTo(SyncWrite.Commenter);
        await Assert.That(kind.Group("entry")!.Write).IsEqualTo(SyncWrite.Author);
        await Assert.That(kind.Group("deleted")!.Write).IsEqualTo(SyncWrite.Author);
        await Assert.That(kind.Groups.All(group => group.Since == 11)).IsTrue();
    }

    private static string Instant() => DateTimeOffset.UtcNow.ToString("o");
}
