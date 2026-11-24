using System.Text.Json;
using System.Net;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.GitHub;
using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class GitHubRpcTests
{
    private sealed class AttachmentRunner : IGitHubCliRunner
    {
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct) => Task.FromResult(arguments[0] == "--version" ? "gh version 2.103.3" : "fixture-token");
    }
    private sealed class AttachmentHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1,\"login\":\"person\"}") });
            if (request.RequestUri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://github-production-user-asset-6210df.s3.amazonaws.com/file");
                return Task.FromResult(redirect);
            }
            var content = new ByteArrayContent([1, 2, 3]); content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Test]
    public async Task AttachmentAsync_RequiresSavedUrlOnLinkedTask()
    {
        const string url = "https://github.com/user-attachments/assets/36da6f97-9797-4896-a07d-a5f92975d8e8";
        var clock = new FixedTimeProvider(new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var api = new FakeGitHub { Account = new(1, "person") };
        api.PutRepository(new(9, "R9", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", true, true));
        var commands = new GitHubConnectionCommands(storage.Store, api, ids, clock);
        var connection = await commands.ConnectAsync(new(Op()));
        var inbox = await storage.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 9, inbox, null, GitHubFilter.Empty, false));
        var tasks = new TaskCommands(storage.Store, ids, clock);
        var linked = (await tasks.CaptureAsync(new(Op(), inbox, "Linked", $"<img src=\"{url}\"/>"))).Task;
        var unrelated = (await tasks.CaptureAsync(new(Op(), inbox, "Other", "No attachment"))).Task;
        await storage.Store.ExecuteAsync(async (s, ct) => {
            await s.GitHub.AddIssueLinkAsync(new GitHubIssueLinkRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id, RepositorySubscriptionId = repo.Id,
                TaskId = linked.Id, IssueId = 12, IssueNodeId = "I12", IssueNumber = 12, HtmlUrl = "https://github.com/person/repo/issues/12", BaseJson = "{}",
                BaseRemoteUpdatedUtc = clock.GetUtcNow(), CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow() }, ct);
            return true;
        });
        var handler = new AttachmentHandler();
        using var client = new GitHubCliClient(new AttachmentRunner(), handler, clock);
        var rpc = new GitHubRpcService(commands, storage.Store, attachments: client);
        var denied = await rpc.AttachmentAsync(new(unrelated.Id, url), CancellationToken.None);
        await Assert.That(denied.Ok).IsFalse();
        await Assert.That(handler.Calls).IsEqualTo(0);
        var allowed = await rpc.AttachmentAsync(new(linked.Id, url), CancellationToken.None);
        await Assert.That(allowed.Ok).IsTrue();
        await Assert.That(allowed.ContentType).IsEqualTo("image/png");
        await Assert.That(allowed.Base64).IsEqualTo("AQID");
    }

    [Test]
    public async Task SharedSpace_StatusSaysWhoManages_AndTaskSyncShowsPullRequestsAndUnsentAssignees()
    {
        const string space = "7e5e1a2b-9d3c-4f6e-8a1b-0c2d3e4f5a6b";
        var clock = new FixedTimeProvider(new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        await storage.Store.ExecuteAsync((s, ct) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) SELECT $space,Id,2,'Team','2026-11-12T08:00:00Z','2026-11-12T08:00:00Z' FROM Profiles LIMIT 1;
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,GitHubLogin,Role,JoinedAtUtc) VALUES
                  ('m-me',$space,(SELECT Id FROM Profiles LIMIT 1),1,'Me',NULL,1,'2026-11-12T08:00:00Z'),
                  ('m-omar',$space,'11111111-1111-1111-1111-111111111111',1,'Omar','omar',2,'2026-11-12T08:00:00Z');
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('team-inbox',$space,0,'Inbox','inbox',0,'2026-11-12T08:00:00Z','2026-11-12T08:00:00Z');
                """;
            command.Parameters.AddWithValue("$space", space);
            return command.ExecuteNonQueryAsync(ct);
        }, CancellationToken.None);
        var api = new FakeGitHub { Account = new(1, "person") };
        api.PutRepository(new(9, "R9", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", true, true));
        api.PutIssue("person/repo", new GitHubIssue(1, "I_1", 1, "https://github.com/person/repo/issues/1", "Issue", "Body", "open", null, new(2, "octocat"), [], [], null, null, clock.GetUtcNow(), clock.GetUtcNow(), null, 0, null));
        var commands = new GitHubConnectionCommands(storage.Store, api, ids, clock);
        var connection = await commands.ConnectAsync(new(Op(), space));
        var repo = await commands.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 9, "team-inbox", null, GitHubFilter.Empty, false));
        var inbound = new GitHubInboundCommands(storage.Store, ids, clock);
        await inbound.ApplyAsync(repo.Id, repo.Version, [await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)]);
        var link = (await storage.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await inbound.ApplyPullRequestsAsync(repo.Id, repo.Version, link.Id,
            [new GitHubPullRequestRef(77, "PR_77", "person/repo", 7, "Fixes #1", "https://github.com/person/repo/pull/7", "OPEN", false, false, "REVIEW_REQUIRED")], clock.GetUtcNow(), CancellationToken.None);
        var profile = (await storage.Store.QueryAsync((s, ct) => s.Profiles.GetPrimaryAsync(ct), CancellationToken.None))!;
        await new TaskCommands(storage.Store, ids, clock).ChangeAssigneeAsync(new(Op(), link.TaskId!, 1, profile.Id, true), CancellationToken.None);
        var rpc = new GitHubRpcService(commands, storage.Store);

        var status = await rpc.StatusAsync(new(space), CancellationToken.None);
        await Assert.That((status.IsOwner, status.SpaceName, status.MemberCount, status.Repositories.Single().IsPrivate, status.Connection!.IsRunner)).IsEqualTo((true, "Team", 2, true, true));
        var detail = await rpc.TaskSyncAsync(new(link.TaskId!), CancellationToken.None);
        await Assert.That((detail.Connected, detail.CanManage, detail.CommentsSigned, detail.PostedAs)).IsEqualTo((true, true, true, "person"));
        await Assert.That(detail.PullRequests!.Single()).IsEqualTo(new GitHubPullRequestDto("7", "Fixes #1", "Open", "Review required", "https://github.com/person/repo/pull/7", "person/repo"));
        await Assert.That(detail.UnsentAssignees).IsEquivalentTo(["Me"]);
        var refused = await rpc.SyncNowAsync(new(null, space), CancellationToken.None);
        await Assert.That(refused.Error!.Message).IsNotEqualTo("Only owners of Team manage its GitHub connection.");

        await storage.Store.ExecuteAsync((s, ct) => Sql(s, "UPDATE SpaceMembers SET Role=2 WHERE Id='m-me';", ct), CancellationToken.None);
        status = await rpc.StatusAsync(new(space), CancellationToken.None);
        await Assert.That(status.IsOwner).IsFalse();
        detail = await rpc.TaskSyncAsync(new(link.TaskId!), CancellationToken.None);
        await Assert.That((detail.Connected, detail.CanManage)).IsEqualTo((true, false));
        refused = await rpc.SyncNowAsync(new(null, space), CancellationToken.None);
        await Assert.That(refused.Error!.Message).IsEqualTo("Only owners of Team manage its GitHub connection.");
        await Assert.That((await rpc.ConnectAsync(new(Op(), space), CancellationToken.None)).Error!.Message).IsEqualTo("Only owners of Team manage its GitHub connection.");
        await Assert.That(api.Requests).IsEqualTo(3); // setup account, repository lookup, the issue read; nothing for the refusals
    }

    private static Task<int> Sql(IDataSession s, string sql, CancellationToken ct)
    {
        using var command = s.Connection().CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQueryAsync(ct);
    }

    private static string Op() => Guid.NewGuid().ToString();
    [Test]
    public async Task ConnectionAndRepository_RpcRoundTrip_UsesStringIdsAndLocalStatus()
    {
        var clock = new FixedTimeProvider(new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var api = new FakeGitHub { Account = new(3000000000, "person") };
        api.PutRepository(new(4000000000, "R", "repo", "person/repo", new(3000000000, "person"), "https://github.com/person/repo", true, true));
        var rpc = new GitHubRpcService(new(storage.Store, api, new SequentialIdGenerator(), clock), storage.Store);
        var local = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(api.Requests).IsEqualTo(0);
        await Assert.That(local.Connection).IsNull();
        await Assert.That((await rpc.ConnectAsync(new(Op()), CancellationToken.None)).Ok).IsTrue();
        var status = await rpc.StatusAsync(new(), CancellationToken.None);
        var connection = status.Connection!;
        var list = await rpc.ListRepositoriesAsync(new(null), CancellationToken.None);
        var request = new GitHubAddRequest(Op(), connection.Id, connection.Version, "person", "repo", list.Repositories.Single().RepositoryId, status.Targets.First().Id, null, new([], "any", "any", null, false, 0), false, "Work");
        await Assert.That((await rpc.AddRepositoryAsync(request, CancellationToken.None)).Ok).IsTrue();
        status = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(status.Repositories.Single().TaskCategory).IsEqualTo("Work");
        var subscription = status.Repositories.Single();
        var invalidCategory = await rpc.UpdateRepositoryAsync(new(Op(), subscription.Id, subscription.Version, subscription.TargetContainerId, subscription.TargetSectionId, subscription.Filter, subscription.MilestonesAsProjects, "invalid"), CancellationToken.None);
        await Assert.That(invalidCategory.Error!.Kind).IsEqualTo("validation");
        var json = JsonSerializer.Serialize(status);
        await Assert.That(json.Contains("\"AccountId\":\"3000000000\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"RepositoryId\":\"4000000000\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That((await rpc.AddRepositoryAsync(request with { OperationId = Op(), ExpectedVersion = "bad" }, CancellationToken.None)).Error!.Kind).IsEqualTo("validation");
        await Assert.That((await rpc.DisconnectAsync(new(Op(), connection.Id, connection.Version, false), CancellationToken.None)).Ok).IsTrue();
        await Assert.That((await rpc.StatusAsync(new(), CancellationToken.None)).Connection).IsNull();
    }

    [Test]
    [Arguments(GitHubErrorKind.CliUnavailable)]
    [Arguments(GitHubErrorKind.Unauthorized)]
    [Arguments(GitHubErrorKind.AccountMismatch)]
    public async Task SetupFailure_ReturnsActionableErrorAsData(GitHubErrorKind kind)
    {
        var clock = new FixedTimeProvider(new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var api = new FakeGitHub { NextFailure = kind };
        var rpc = new GitHubRpcService(new(storage.Store, api, new SequentialIdGenerator(), clock), storage.Store);
        var result = await rpc.ConnectAsync(new(Op()), CancellationToken.None);
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Error!.Kind).IsEqualTo("github" + kind);
        await Assert.That(result.Error.Message.Length > 30).IsTrue();
        await Assert.That((await rpc.StatusAsync(new(), CancellationToken.None)).Connection).IsNull();
    }
}
