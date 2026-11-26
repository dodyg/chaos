using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Application.History;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.GitHub;

/// <summary>
/// GitHub in a shared space (PLAN3 §10) on real SQLite with the fake API: owners alone manage the connection and
/// run the engine, members' assignees match their GitHub logins both ways, a member's comment is posted signed
/// with their name and read back unsigned, and linked pull requests feed the view fields (G9).
/// </summary>
public sealed class GitHubSharingTests
{
    private sealed record Team(Guid Omar, Guid Lina, Guid Sara);

    /// <summary>The shared space's id, as real ones are: a GUID in D format.</summary>
    private const string Space = "7e5e1a2b-9d3c-4f6e-8a1b-0c2d3e4f5a6b";

    /// <summary>The local person owns "Team"; Omar and Sara edit with GitHub logins, Lina only views.</summary>
    private static async Task<Team> MakeTeamAsync(DataHost host)
    {
        var team = new Team(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await host.Store.ExecuteAsync((s, ct) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) SELECT $space,Id,2,'Team','2026-11-10T09:00:00Z','2026-11-10T09:00:00Z' FROM Profiles LIMIT 1;
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,GitHubLogin,Role,JoinedAtUtc) VALUES
                  ('m-me',$space,(SELECT Id FROM Profiles LIMIT 1),1,'Me',NULL,1,'2026-11-10T09:00:00Z'),
                  ('m-omar',$space,$omar,1,'Omar','omar',2,'2026-11-10T09:00:00Z'),
                  ('m-lina',$space,$lina,1,'Lina','lina',3,'2026-11-10T09:00:00Z'),
                  ('m-sara',$space,$sara,1,'Sara','sara',2,'2026-11-10T09:00:00Z');
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('team-inbox',$space,0,'Inbox','inbox',0,'2026-11-10T09:00:00Z','2026-11-10T09:00:00Z');
                """;
            command.Parameters.AddWithValue("$space", Space);
            command.Parameters.AddWithValue("$omar", team.Omar.ToString("D"));
            command.Parameters.AddWithValue("$lina", team.Lina.ToString("D"));
            command.Parameters.AddWithValue("$sara", team.Sara.ToString("D"));
            return command.ExecuteNonQueryAsync(ct);
        }, CancellationToken.None);
        return team;
    }

    private static FakeGitHub Fake(params GitHubUser[] assignees)
    {
        var api = new FakeGitHub();
        api.KnownUsers["omar"] = new(5, "omar");
        api.KnownUsers["lina"] = new(6, "lina");
        api.KnownUsers["sara"] = new(7, "sara");
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        api.PutIssue("person/repo", new GitHubIssue(1, "I_1", 1, "https://github.com/person/repo/issues/1", "Original", "Body", "open", null,
            new(2, "octocat"), [], [.. assignees], null, null, DataHost.Start, DataHost.Start, null, 0, null));
        return api;
    }

    private static async Task<(GitHubRepositoryRecord Repo, GitHubConnectionRecord Connection, GitHubIssueLinkRecord Link)> ConnectAsync(DataHost host, FakeGitHub api, bool import = true)
    {
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op(), Space));
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, "team-inbox", null, GitHubFilter.Empty, false));
        GitHubIssueLinkRecord? link = null;
        if (import)
        {
            await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)]);
            link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        }
        return (repo, connection, link!);
    }

    private static Task<IReadOnlyList<string>> AssignedAsync(DataHost host, string taskId) =>
        host.Store.QueryAsync<IReadOnlyList<string>>(async (s, ct) => (IReadOnlyList<string>)[.. (await s.Tasks.ListAssigneesAsync(taskId, ct)).Where(row => row.DeletedAtUtc is null).Select(row => row.MemberId).Order(StringComparer.Ordinal)], CancellationToken.None);

    private static Task<int> Sql(IDataSession s, string sql, CancellationToken ct)
    {
        using var command = s.Connection().CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQueryAsync(ct);
    }

    [Test]
    public async Task SharedConnection_IsManagedByOwnersOnly_AndTheEngineRunsOnAnOwnersDevice()
    {
        using var host = await DataHost.StartAsync();
        var team = await MakeTeamAsync(host);
        var api = Fake();
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op(), Space));
        await Assert.That(connection.SpaceId).IsEqualTo(Space);
        var status = await commands.StatusAsync(Space, CancellationToken.None);
        await Assert.That((status.IsOwner, status.IsRunner, status.SpaceName)).IsEqualTo((true, true, "Team"));
        await Assert.That((await commands.ScheduleAsync(CancellationToken.None)).Select(schedule => schedule.Status.Connection!.Id)).IsEquivalentTo([connection.Id]);
        var requests = api.Requests;
        await host.Store.ExecuteAsync((s, ct) => Sql(s, "UPDATE SpaceMembers SET Role=2 WHERE Id='m-me';", ct), CancellationToken.None);

        // Demoted: nothing reaches GitHub CLI, and every management action says who does it.
        status = await commands.StatusAsync(Space, CancellationToken.None);
        await Assert.That(status.IsOwner).IsFalse();
        await RefusedAsync(() => commands.RunOnThisDeviceAsync(new(DataHost.Op(), connection.Id, connection.Version, false), CancellationToken.None));
        await RefusedAsync(() => commands.UpdateSettingsAsync(new(DataHost.Op(), connection.Id, connection.Version, false, true), CancellationToken.None));
        await RefusedAsync(() => commands.SetConnectionPausedAsync(new(DataHost.Op(), connection.Id, connection.Version, true), CancellationToken.None));
        await RefusedAsync(() => new GitHubSyncCommands(host.Store, api, null!, null!, host.Ids, host.Clock).RunAsync(null, CancellationToken.None, connection.Id));
        await Assert.That(api.Requests).IsEqualTo(requests);
        await Assert.That(await commands.ScheduleAsync(CancellationToken.None)).IsEmpty();

        // A viewer is told the same, before any GitHub CLI call.
        await host.Store.ExecuteAsync((s, ct) => Sql(s, "UPDATE SpaceMembers SET Role=3 WHERE Id='m-me';", ct), CancellationToken.None);
        status = await commands.StatusAsync(Space, CancellationToken.None);
        await Assert.That(status.IsOwner).IsFalse();
        await RefusedAsync(() => commands.ConnectAsync(new(DataHost.Op(), Space), CancellationToken.None));
        await Assert.That(api.Requests).IsEqualTo(requests);
    }

    private static async Task RefusedAsync(Func<Task> action)
    {
        var error = await Assert.That(action).Throws<AppException>();
        await Assert.That(error!.Message).IsEqualTo("Only owners of Team manage its GitHub connection.");
    }

    [Test]
    public async Task MembersEdit_ReachesGitHubThroughTheOwnersRunner_InASharedSpace()
    {
        using var host = await DataHost.StartAsync();
        await MakeTeamAsync(host);
        var api = Fake();
        var (repo, connection, link) = await ConnectAsync(host, api);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await s.Tasks.UpdateAsync(task with { Title = "Edited by a member", Version = task.Version + 1 }, task.Version, ct);
            return true;
        }, CancellationToken.None);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsTrue();
        var inbound = new GitHubInboundCommands(host.Store, host.Ids, host.Clock);
        var result = await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(result.Pushed).IsEqualTo(1);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Title).IsEqualTo("Edited by a member");
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsFalse();
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetConnectionAsync(connection.Id, ct)))!.SpaceId).IsEqualTo(Space);
    }

    [Test]
    public async Task Assignees_MatchMembersGitHubLogins_BothWays_AndUnmatchedStayVisible()
    {
        using var host = await DataHost.StartAsync();
        var team = await MakeTeamAsync(host);
        var api = Fake(new GitHubUser(5, "omar"), new GitHubUser(6, "lina"), new GitHubUser(9, "stranger"));
        var (repo, _, link) = await ConnectAsync(host, api);
        // Inbound: Omar (editor) becomes an assignee; Lina is a viewer, so she cannot be one; the stranger stays a login on the issue.
        await Assert.That(await AssignedAsync(host, link.TaskId!)).IsEquivalentTo([team.Omar.ToString("D")]);
        var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.BaseJson)!;
        await Assert.That(baseline.DisplayAssignees()!).IsEquivalentTo(["omar", "lina", "stranger"]);

        // GitHub drops Omar: his assignment goes with the login.
        await api.SetAssignmentAsync(api.Account, "person", "repo", 1, "omar", false, CancellationToken.None);
        await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)]);
        await Assert.That(await AssignedAsync(host, link.TaskId!)).IsEmpty();

        // Assigning Sara here puts her login on the issue; assigning the owner, who has no login, stays in the app only.
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        await new TaskCommands(host.Store, host.Ids, host.Clock).ChangeAssigneeAsync(new(DataHost.Op(), task.Id, task.Version, team.Sara.ToString("D"), true), CancellationToken.None);
        var profile = (await host.Store.QueryAsync((s, ct) => s.Profiles.GetPrimaryAsync(ct), CancellationToken.None))!;
        await new TaskCommands(host.Store, host.Ids, host.Clock).ChangeAssigneeAsync(new(DataHost.Op(), task.Id, task.Version + 1, profile.Id, true), CancellationToken.None);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsTrue();
        api.AssignmentWrites.Clear();
        await new GitHubOutboundCommands(host.Store, api, new GitHubInboundCommands(host.Store, host.Ids, host.Clock), host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(api.AssignmentWrites).IsEquivalentTo(["+sara"]);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Assignees.Select(user => user.Login).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["lina", "sara", "stranger"]);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsFalse();

        // Unassigning Sara here removes her login from the issue.
        task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        await new TaskCommands(host.Store, host.Ids, host.Clock).ChangeAssigneeAsync(new(DataHost.Op(), task.Id, task.Version, team.Sara.ToString("D"), false), CancellationToken.None);
        api.AssignmentWrites.Clear();
        await new GitHubOutboundCommands(host.Store, api, new GitHubInboundCommands(host.Store, host.Ids, host.Clock), host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(api.AssignmentWrites).IsEquivalentTo(["-sara"]);
        await Assert.That(await AssignedAsync(host, link.TaskId!)).IsEquivalentTo([profile.Id]);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsFalse();
    }

    [Test]
    public async Task MemberWhoSetsALoginAfterTheIssueCarriedIt_IsMatchedInbound_AndGitHubKeepsTheAssignee()
    {
        using var host = await DataHost.StartAsync();
        var team = await MakeTeamAsync(host);
        var api = Fake(new GitHubUser(5, "omar"));
        var (repo, _, link) = await ConnectAsync(host, api);
        // Omar has no assignee row yet; the first pass creates it instead of reading his absence as a local removal.
        await Assert.That(await AssignedAsync(host, link.TaskId!)).IsEquivalentTo([team.Omar.ToString("D")]);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsFalse();
        api.AssignmentWrites.Clear();
        await new GitHubOutboundCommands(host.Store, api, new GitHubInboundCommands(host.Store, host.Ids, host.Clock), host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(api.AssignmentWrites).IsEmpty();
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Assignees.Select(user => user.Login)).IsEquivalentTo(["omar"]);
    }

    [Test]
    public async Task SelfAssignment_InASharedSpace_IsAnOrdinaryAssignment_AndFollowsTheLogin()
    {
        using var host = await DataHost.StartAsync();
        var team = await MakeTeamAsync(host);
        var api = Fake();
        var (repo, _, link) = await ConnectAsync(host, api);
        var profile = (await host.Store.QueryAsync((s, ct) => s.Profiles.GetPrimaryAsync(ct), CancellationToken.None))!;
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        await commands.SetSelfAssignmentAsync(new(DataHost.Op(), link.TaskId!, true), CancellationToken.None);
        await Assert.That(await AssignedAsync(host, link.TaskId!)).IsEquivalentTo([profile.Id]);
        // The owner sets a login in the Members tab; the next pass finds the local assignment and sends it.
        await host.Store.ExecuteAsync((s, ct) => Sql(s, "UPDATE SpaceMembers SET GitHubLogin='me' WHERE Id='m-me';", ct), CancellationToken.None);
        api.KnownUsers["me"] = new(8, "me");
        await new GitHubOutboundCommands(host.Store, api, new GitHubInboundCommands(host.Store, host.Ids, host.Clock), host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(api.AssignmentWrites).IsEquivalentTo(["+me"]);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalAssigneeIntent).IsNull();
        await Assert.That(team.Sara).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task AgentUpdates_AreSignedWithTheAgentsName_WhetherServerOrLocal_AndReadBackWithoutTheLine()
    {
        using var host = await DataHost.StartAsync();
        var team = await MakeTeamAsync(host);
        var bot = Guid.NewGuid();
        await host.Store.ExecuteAsync((s, ct) => Sql(s, $"INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,GitHubLogin,Role,JoinedAtUtc) VALUES ('m-bot','{Space}','{bot:D}',2,'Build bot',NULL,4,'2026-11-10T09:00:00Z');", ct), CancellationToken.None);
        var api = Fake();
        var (repo, _, link) = await ConnectAsync(host, api);

        // A shared-space agent's entry, authored by the agent's ID and queued by the service without a signature.
        var served = await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var now = host.Clock.GetUtcNow();
            var entry = new DiaryEntryRecord { Id = host.Ids.NewId().ToString(), TaskId = link.TaskId!, AuthorProfileId = bot.ToString("D"), EntryAtUtc = now, RawMarkdown = "Deployed to staging",
                Revision = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
            await s.Diary.AddAsync(entry, ct);
            await s.GitHub.UpsertCommentLinkAsync(new() { DiaryEntryId = entry.Id, IssueLinkId = link.Id, Origin = 1, State = 1 }, ct);
            return entry;
        }, CancellationToken.None);
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await Assert.That(await outbound.RunAsync(repo.Id, true, CancellationToken.None)).IsEqualTo(1);
        var remote = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Single();
        await Assert.That(remote.Body).IsEqualTo("Deployed to staging\n\n— Build bot (agent, via Chaos)\n\n<!-- chaos:" + served.Id + " -->");
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single().Attribution).IsEqualTo("— Build bot (agent, via Chaos)");
        await Assert.That(await new GitHubInboundComments(host.Store, host.Ids, host.Clock).ApplyAsync(link.Id,
            (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.Version, [remote], true, CancellationToken.None)).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Diary.GetAsync(served.Id, ct)))!.RawMarkdown).IsEqualTo("Deployed to staging");

        // A local agent's update, written under its scope, is queued with its own signature.
        var local = await new Chaos.Application.Agents.AgentCommands(host.Store, host.Ids, host.Clock).CreateAsync(new(DataHost.Op(), "Desk bot"));
        string mine;
        using (Chaos.Application.Activity.ActivityScope.Open(Chaos.Application.Activity.ActivitySources.Agent, Chaos.Application.Activity.ActivityActions.AgentAddUpdate,
            operationId: DataHost.Op(), actorId: $"agent:{local.Id}"))
        {
            mine = (await new DiaryCommands(host.Store, host.Ids, host.Clock).AddAsync(new(DataHost.Op(), link.TaskId!, "Checked the logs", null, true))).Entry.Id;
        }

        await Assert.That(team.Omar).IsNotEqualTo(Guid.Empty);
        await Assert.That(await outbound.RunAsync(repo.Id, true, CancellationToken.None)).IsEqualTo(1);
        var comments = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items;
        await Assert.That(comments.Single(comment => comment.Body.EndsWith("<!-- chaos:" + mine + " -->", StringComparison.Ordinal)).Body)
            .IsEqualTo("Checked the logs\n\n— Desk bot (agent, via Chaos)\n\n<!-- chaos:" + mine + " -->");
    }

    [Test]
    public async Task MemberComment_IsPostedSignedWithTheirName_AndReadBackWithoutTheLine()
    {
        using var host = await DataHost.StartAsync();
        var team = await MakeTeamAsync(host);
        var api = Fake();
        var (repo, _, link) = await ConnectAsync(host, api);
        var entry = await host.Store.ExecuteAsync(async (s, ct) =>
        {
            // Omar's device queued his update for GitHub, as the diary composer's "Also post to GitHub" does.
            var entry = new DiaryEntryRecord { Id = host.Ids.NewId().ToString(), TaskId = link.TaskId!, AuthorProfileId = team.Omar.ToString("D"),
                EntryAtUtc = host.Clock.GetUtcNow(), RawMarkdown = "Ship it", Revision = 1, Version = 1, CreatedAtUtc = host.Clock.GetUtcNow(), UpdatedAtUtc = host.Clock.GetUtcNow() };
            await s.Diary.AddAsync(entry, ct);
            await s.GitHub.UpsertCommentLinkAsync(new() { DiaryEntryId = entry.Id, IssueLinkId = link.Id, Origin = 1, State = 1 }, ct);
            return entry;
        }, CancellationToken.None);
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await Assert.That(await outbound.RunAsync(repo.Id, false, CancellationToken.None)).IsEqualTo(0);
        var stored = (await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single();
        await Assert.That(stored.Attribution).IsEqualTo("— Omar (via Chaos)");
        await Assert.That(api.AssignmentWrites).IsEmpty();

        await Assert.That(await outbound.RunAsync(repo.Id, true, CancellationToken.None)).IsEqualTo(1);
        var remote = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Single();
        await Assert.That(remote.Body).IsEqualTo("Ship it\n\n— Omar (via Chaos)\n\n<!-- chaos:" + entry.Id + " -->");
        stored = (await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single();
        await Assert.That((stored.State, stored.BaseBody, stored.Attribution)).IsEqualTo((0, "Ship it", "— Omar (via Chaos)"));

        // Reading the comment back does not copy the line into the diary, and a later edit carries it again.
        await Assert.That(await new GitHubInboundComments(host.Store, host.Ids, host.Clock).ApplyAsync(link.Id,
            (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.Version, [remote], true, CancellationToken.None)).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Diary.GetAsync(entry.Id, ct)))!.RawMarkdown).IsEqualTo("Ship it");
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var current = (await s.Diary.GetAsync(entry.Id, ct))!;
            await s.Diary.UpdateAsync(current with { RawMarkdown = "Ship it tomorrow", Revision = 2, Version = current.Version + 1, UpdatedAtUtc = host.Clock.GetUtcNow() }, current.Version, ct);
            return true;
        }, CancellationToken.None);
        await Assert.That(await outbound.RunAsync(repo.Id, true, CancellationToken.None)).IsEqualTo(1);
        remote = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Single();
        await Assert.That(remote.Body).IsEqualTo("Ship it tomorrow\n\n— Omar (via Chaos)\n\n<!-- chaos:" + entry.Id + " -->");
        stored = (await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single();
        await Assert.That((stored.State, stored.BaseBody)).IsEqualTo((0, "Ship it tomorrow"));

        // The owner's own update is posted unsigned.
        var mine = (await new DiaryCommands(host.Store, host.Ids, host.Clock).AddAsync(new(DataHost.Op(), link.TaskId!, "Owner's words", null, true))).Entry;
        await Assert.That(await outbound.RunAsync(repo.Id, true, CancellationToken.None)).IsEqualTo(1);
        var comments = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items;
        await Assert.That(comments.Single(comment => comment.Body.EndsWith("<!-- chaos:" + mine.Id + " -->", StringComparison.Ordinal)).Body).IsEqualTo("Owner's words\n\n<!-- chaos:" + mine.Id + " -->");
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single(mapping => mapping.DiaryEntryId == mine.Id).Attribution).IsNull();
    }

    [Test]
    public async Task PullRequests_AreKeptFromThePass_AndTheViewFieldsReadThem()
    {
        using var host = await DataHost.StartAsync();
        await MakeTeamAsync(host);
        var api = Fake();
        api.PullRequests["I_1"] =
        [
            new GitHubPullRequestRef(77, "PR_77", "person/repo", 7, "Fixes #1", "https://github.com/person/repo/pull/7", "MERGED", false, true, "APPROVED"),
            new GitHubPullRequestRef(78, "PR_78", "person/repo", 8, "Follow-up", "https://github.com/person/repo/pull/8", "OPEN", true, false, "CHANGES_REQUESTED"),
        ];
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op(), Space));
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, "team-inbox", null, GitHubFilter.Empty, false));
        var inbound = new GitHubInboundCommands(host.Store, host.Ids, host.Clock);
        using (var fetcher = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("unused.db"), host.Clock))
        {
            await fetcher.PullAsync(repo.Id, ct: CancellationToken.None);
        }
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var pulls = await host.Store.QueryAsync((s, ct) => s.GitHub.ListPullRequestLinksAsync(link.Id, ct));
        await Assert.That(pulls.Select(pr => (pr.Number, pr.State, pr.ReviewState)).Order()).IsEquivalentTo([(7L, 2, 1), (8L, 1, 2)]);
        await Assert.That(pulls.Single(pr => pr.Number == 8).PullRequestId).IsEqualTo("PR_78");
        var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.BaseJson)!;
        await Assert.That((baseline.AuthorLogin, baseline.IssueType)).IsEqualTo(("octocat", null));

        var ids = await IdsAsync(host, link.TaskId!);
        await Assert.That(await ids("{\"field\":\"githubLinkedPr\",\"op\":\"is\",\"value\":true}")).IsEquivalentTo([link.TaskId!]);
        await Assert.That(await ids("{\"field\":\"githubLinkedPr\",\"op\":\"is\",\"value\":false}")).IsEmpty();
        await Assert.That(await ids("{\"field\":\"githubPrReview\",\"op\":\"is\",\"value\":\"Approved\"}")).IsEquivalentTo([link.TaskId!]);
        await Assert.That(await ids("{\"field\":\"githubPrReview\",\"op\":\"is\",\"value\":\"Changes requested\"}")).IsEquivalentTo([link.TaskId!]);
        await Assert.That(await ids("{\"field\":\"githubPrReview\",\"op\":\"is\",\"value\":\"Review required\"}")).IsEmpty();
        await Assert.That(await ids("{\"field\":\"githubPrReview\",\"op\":\"is\",\"value\":\"None\"}")).IsEmpty();
        await Assert.That(await ids("{\"field\":\"githubAuthor\",\"op\":\"contains\",\"value\":\"OCTO\"}")).IsEquivalentTo([link.TaskId!]);
        await Assert.That(await ids("{\"field\":\"githubAuthor\",\"op\":\"isEmpty\"}")).IsEmpty();
        await Assert.That(await ids("{\"field\":\"githubIssueType\",\"op\":\"isEmpty\"}")).IsEquivalentTo([link.TaskId!]);
        await Assert.That(await ids("{\"field\":\"githubIssueType\",\"op\":\"contains\",\"value\":\"bug\"}")).IsEmpty();

        // A pull request that no longer references the issue is removed on the next pass; open ones are re-read once a day.
        api.PullRequests["I_1"] = [new GitHubPullRequestRef(78, "PR_78", "person/repo", 8, "Follow-up", "https://github.com/person/repo/pull/8", "OPEN", false, false, null)];
        await new GitHubPullRequestLinks(host.Store, api, inbound, host.Clock).RefreshChangedAsync(repo, connection, [await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)], CancellationToken.None);
        pulls = await host.Store.QueryAsync((s, ct) => s.GitHub.ListPullRequestLinksAsync(link.Id, ct));
        await Assert.That(pulls.Select(pr => pr.Number)).IsEquivalentTo([8L]);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.GitHub.ReplacePullRequestLinksAsync(link.Id, [pulls[0] with { RefreshedAtUtc = host.Clock.GetUtcNow().AddDays(-2) }], ct);
            return true;
        }, CancellationToken.None);
        var stale = await host.Store.QueryAsync((s, ct) => s.GitHub.ListLinksStaleForPullRequestsAsync(repo.Id, host.Clock.GetUtcNow().AddDays(-1), 50, ct), CancellationToken.None);
        await Assert.That(stale.Select(row => row.Id)).IsEquivalentTo([link.Id]);
        api.PullRequests["I_1"] = [];
        await new GitHubPullRequestLinks(host.Store, api, inbound, host.Clock).RefreshStaleAsync(repo, connection, stale, CancellationToken.None);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.ListPullRequestLinksAsync(link.Id, ct))).IsEmpty();
        await Assert.That(await ids("{\"field\":\"githubLinkedPr\",\"op\":\"is\",\"value\":true}")).IsEmpty();
    }

    private static async Task<Func<string, Task<IReadOnlyList<string>>>> IdsAsync(DataHost host, string taskId)
    {
        var queries = new TaskQueries(host.Store, host.Clock);
        async Task<IReadOnlyList<string>> Run(string items)
        {
            var spec = new TaskQuerySpec { View = TaskView.Custom, Definition = ViewDefinition.Parse("{\"v\":1,\"match\":\"all\",\"items\":[" + items + "]}") };
            var page = await queries.RunAsync(spec);
            await Assert.That(page.Rows.Count).IsEqualTo(await queries.CountAsync(spec));
            return [.. page.Rows.Select(row => row.Task.Id).Order(StringComparer.Ordinal)];
        }
        return Run;
    }
}
