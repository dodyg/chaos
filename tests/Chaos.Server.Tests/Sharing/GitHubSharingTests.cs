using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sharing;

/// <summary>
/// PLAN3 Q15 on the real service: the GitHub engine's runner follows the owners (it is cleared when its owner is
/// demoted or leaves), and a member's edit, assignment and diary post reach GitHub through the owner's device,
/// the post signed with the member's name.
/// </summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class GitHubSharingTests(PostgresServer postgres)
{
    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<string?> RunnerAsync(TestServer server, Guid space, string connection)
    {
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var row = await db.SyncEntities.AsNoTracking().SingleAsync(e => e.AccountId == space && e.Kind == "github_connection" && e.EntityKey == connection);
        return SyncJson.ReadGroups(row.Groups).GetValueOrDefault("runner")?.Values.Values.GetValueOrDefault("RunnerDeviceId")?.ToString();
    }

    [Test]
    public async Task Runner_IsClearedWhenItsOwnerIsDemotedOrLeaves_AndOnlyOwnersSetIt()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await using var world = new SharingHarness(server);
        var first = await world.SignInAsync("runner-first@example.com");
        var second = await world.SignInAsync("runner-second@example.com");
        var space = await world.CreateSpaceAsync(first);
        await world.JoinAsync(second, await world.InviteLinkAsync(first, space, SpaceMember.EditorRole));
        // Invites carry Editor or Viewer only (8.5); the owner promotes the second owner.
        await Assert.That((await world.CallAsync(first, System.Net.Http.HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{second.ProfileId:D}", new MemberRoleRequest(SpaceMember.OwnerRole))).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        var connection = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(first, new[]
        {
            SharingHarness.Change("github_connection", connection, "create",
                new { SpaceId = space.ToString("D"), ProfileId = first.ProfileId.ToString("D"), WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com", AccountId = 1L, AccountLogin = "first", CreatedAtUtc = Instant() }),
            SharingHarness.Change("github_connection", connection, "runner", new { RunnerDeviceId = first.DeviceId.ToString("D"), RunnerDeviceName = "First desk" }),
        })).Rejections()).IsEmpty();
        await Assert.That(await RunnerAsync(server, space, connection)).IsEqualTo(first.DeviceId.ToString("D"));

        // Demoted to editor: the service clears the runner, and the former owner can no longer set it.
        await Assert.That((await world.CallAsync(second, System.Net.Http.HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{first.ProfileId:D}", new MemberRoleRequest(SpaceMember.EditorRole))).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(await RunnerAsync(server, space, connection)).IsNull();
        await Assert.That((await world.PushAsync(first, SharingHarness.Change("github_connection", connection, "runner",
            new { RunnerDeviceId = first.DeviceId.ToString("D"), RunnerDeviceName = "First desk" }))).Rejections()).Contains(SyncRejection.Forbidden);

        // The remaining owner moves the runner to their own device; promoting the editor back does not change it.
        await Assert.That((await world.PushAsync(second, SharingHarness.Change("github_connection", connection, "runner",
            new { RunnerDeviceId = second.DeviceId.ToString("D"), RunnerDeviceName = "Second desk" }))).Rejections()).IsEmpty();
        await Assert.That(await RunnerAsync(server, space, connection)).IsEqualTo(second.DeviceId.ToString("D"));
        await Assert.That((await world.CallAsync(second, System.Net.Http.HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{first.ProfileId:D}", new MemberRoleRequest(SpaceMember.OwnerRole))).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(await RunnerAsync(server, space, connection)).IsEqualTo(second.DeviceId.ToString("D"));

        // The runner's owner leaves: the runner is cleared again, so no device of a former member keeps syncing.
        await Assert.That((await world.PushAsync(second, SharingHarness.Change("github_connection", connection, "runner",
            new { RunnerDeviceId = first.DeviceId.ToString("D"), RunnerDeviceName = "First desk" }))).Rejections()).IsEmpty();
        await Assert.That((await world.CallAsync(first, System.Net.Http.HttpMethod.Post, $"/v2/spaces/{space:D}/leave")).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(await RunnerAsync(server, space, connection)).IsNull();
        await Assert.That((await world.CallAsync(second, System.Net.Http.HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/me", new { gitHubLogin = "second" })).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That((await world.CallAsync(second, System.Net.Http.HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/me", new { gitHubLogin = "not a login" })).Status).IsEqualTo(System.Net.HttpStatusCode.BadRequest);
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var member = await db.SpaceMembers.AsNoTracking().SingleAsync(m => m.SpaceId == space && m.MemberId == second.ProfileId);
        await Assert.That(member.GitHubLogin).IsEqualTo("second");
        await Assert.That((await world.CallAsync(second, System.Net.Http.HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/me", new { gitHubLogin = "" })).Status).IsEqualTo(System.Net.HttpStatusCode.OK);
        await db.Entry(member).ReloadAsync();
        await Assert.That(member.GitHubLogin).IsNull();
    }

    [Test]
    public async Task MembersEditAssignmentAndPost_ReachGitHubThroughTheOwnersRunner_SignedWithTheMembersName()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var sara = await world.AddPersonAsync("gh-sara@example.com", "Sara");
        var omar = await world.AddPersonAsync("gh-omar@example.com", "Omar");
        var space = await sara.Spaces().CreateAsync("Launch team");
        var list = (await new Chaos.Application.Containers.ContainerCommands(sara.Desktop.Store, sara.Desktop.Ids, sara.Desktop.Clock)
            .CreateAsync(new(Op(), "Issues", Chaos.Domain.Tasks.ContainerKind.List, space.SpaceId))).Container.Id;
        await world.SettleAsync();
        var joined = await omar.Spaces().JoinWithLinkAsync((await sara.Spaces().InviteAsync(space.SpaceId, null, SpaceMember.EditorRole, null, 10)).Link!);
        await Assert.That(joined.Role).IsEqualTo(SpaceMember.EditorRole);
        await world.SettleAsync();

        // Only Sara, an owner, sets up GitHub. Omar needs no GitHub CLI and no account.
        var api = new FakeGitHub();
        api.KnownUsers["omar"] = new(5, "omar");
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        api.PutIssue("person/repo", new GitHubIssue(1, "I_1", 1, "https://github.com/person/repo/issues/1", "Original", "Body", "open", null, new(2, "octocat"), [], [], null, null, Start, Start, null, 0, null));
        var saraCommands = new GitHubConnectionCommands(sara.Desktop.Store, api, sara.Desktop.Ids, sara.Desktop.Clock);
        var connection = await saraCommands.ConnectAsync(new(Op(), space.SpaceId));
        var repo = await saraCommands.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 10, list, null, GitHubFilter.Empty, false));
        GitHubSyncCommands Runner() =>
            new(sara.Desktop.Store, api,
                new GitHubIssueFetcher(sara.Desktop.Store, api, new GitHubInboundCommands(sara.Desktop.Store, sara.Desktop.Ids, sara.Desktop.Clock), _ => Task.FromResult("unused.db"), sara.Desktop.Clock),
                new GitHubCommentFetcher(sara.Desktop.Store, api, new GitHubInboundComments(sara.Desktop.Store, sara.Desktop.Ids, sara.Desktop.Clock), sara.Desktop.Clock),
                sara.Desktop.Ids, sara.Desktop.Clock);
        await Runner().RunAsync(null, CancellationToken.None, connection.Id);
        var link = (await sara.Desktop.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await world.SettleAsync();

        // Omar sets his GitHub login in the Members tab, assigns himself, edits the title and posts an update to GitHub.
        omar.Desktop.Clock.Now = omar.Desktop.Clock.Now.AddMinutes(1);
        await omar.Spaces().SetMyGitHubLoginAsync(space.SpaceId, "omar");
        await world.SettleAsync();
        var task = (await omar.Desktop.Tasks.GetAsync(link.TaskId!))!;
        await omar.Desktop.Tasks.ChangeAssigneeAsync(new(Op(), task.Id, task.Version, omar.ProfileId.ToString("D"), true));
        task = (await omar.Desktop.Tasks.GetAsync(link.TaskId!))!;
        await omar.Desktop.Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, "Edited by Omar", task.Notes, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        var entry = (await new DiaryCommands(omar.Desktop.Store, omar.Desktop.Ids, omar.Desktop.Clock).AddAsync(new(Op(), task.Id, "Omar's update", null, true))).Entry;
        await world.SettleAsync();

        // Sara's device, the runner, received the edit, the assignment and the pending post as ordinary sync changes.
        var runnerLink = (await sara.Desktop.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!;
        await Assert.That((await sara.Desktop.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!.Title).IsEqualTo("Edited by Omar");
        await Assert.That(runnerLink.LocalDirty).IsTrue();
        await Assert.That((await sara.Desktop.Store.QueryAsync((s, ct) => s.Tasks.ListAssigneesAsync(link.TaskId!, ct))).Any(row => row.MemberId == omar.ProfileId.ToString("D") && row.DeletedAtUtc is null)).IsTrue();
        // (the attribution is the runner's to add when it posts, so the link arrives unsigned)
        await Assert.That((await sara.Desktop.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Any(row => row is { Origin: 1, State: 1 })).IsTrue();
        await Runner().RunTaskAsync(link.TaskId!, CancellationToken.None);
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await Assert.That(remote.Title).IsEqualTo("Edited by Omar");
        await Assert.That(remote.Assignees.Select(user => user.Login)).IsEquivalentTo(["omar"]);
        var posted = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Single();
        await Assert.That(posted.User.Login).IsEqualTo("person");
        await Assert.That(posted.Body).IsEqualTo(GitHubCommentMarker.Body("Omar's update", entry.Id, GitHubCommentMarker.Attribution("Omar")));
        await world.SettleAsync();

        // Back on Omar's device: his words without the signature, and he is the assignee on both devices.
        await Assert.That((await omar.Desktop.Store.QueryAsync((s, ct) => s.Diary.GetIncludingDeletedAsync(entry.Id, ct)))!.RawMarkdown).IsEqualTo("Omar's update");
        await Assert.That((await omar.Desktop.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single().Attribution).IsEqualTo("— Omar (via Chaos)");
        await Assert.That((await sara.Desktop.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsFalse();
        await Assert.That(api.RemoteWrites).IsEqualTo(3);
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static string Instant() => DateTimeOffset.UtcNow.ToString("O");
}
