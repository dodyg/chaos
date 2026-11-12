using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubSyncCommandsTests
{
    [Test]
    [Arguments(GitHubErrorKind.NotFound)]
    [Arguments(GitHubErrorKind.Gone)]
    [Arguments(GitHubErrorKind.RateLimited)]
    public async Task RemoteFailurePersistsSafeState_AndRateLimitBlocksFurtherNetwork(GitHubErrorKind kind)
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        using var issues = new GitHubIssueFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), _ => Task.FromResult("backup.db"), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        var sync = new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock);
        api.NextFailure = kind;
        await Assert.That(async () => await sync.RunAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var current = (await s.GitHub.GetRepositoryAsync(repo.Id, ct))!;
            await Assert.That(current.State).IsEqualTo(kind == GitHubErrorKind.RateLimited ? 0 : 2);
            await Assert.That(current.Version).IsEqualTo(repo.Version);
            await Assert.That(current.LastErrorMessage).IsEqualTo(kind.ToString());
            var run = (await s.GitHub.ListSyncRunsAsync(connection.Id, ct)).Single();
            await Assert.That(run.ErrorKind).IsEqualTo((int?)kind);
            await Assert.That(run.PushedCount).IsEqualTo(0L);
            return true;
        });
        if (kind == GitHubErrorKind.RateLimited)
        {
            var calls = api.Requests;
            await Assert.That(async () => await sync.RunAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
            await Assert.That(api.Requests).IsEqualTo(calls);
        }
    }
}
