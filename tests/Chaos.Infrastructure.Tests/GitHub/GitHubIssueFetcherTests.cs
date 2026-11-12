using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubIssueFetcherTests
{
    private static async Task<(GitHubRepositoryRecord Repo, GitHubConnectionCommands Commands)> Setup(DataHost host, FakeGitHub api, int count)
    {
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        for (var i = 1; i <= count; i++) api.PutIssue("person/repo", new(i, "I" + i, i, "https://github.com/person/repo/issues/" + i, "Issue " + i, "", "open", null,
            new(1, "person"), [], [], null, null, DataHost.Start, DataHost.Start.AddSeconds(i), null, 0, null));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var c = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(c.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), c.Id, c.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        return (repo, commands);
    }
    private static GitHubIssueFetcher Fetcher(DataHost host, FakeGitHub api, Func<CancellationToken, Task<string>> backup) => new(host.Store, api, new(host.Store, host.Ids, host.Clock), backup, host.Clock);

    [Test]
    public async Task CappedPagesResumeAcrossRestart_AndBackupPrecedesFirstTask()
    {
        var api = new FakeGitHub(); string root; GitHubRepositoryRecord repo; int backups = 0;
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root; (repo, _) = await Setup(host, api, 5);
            using var fetcher = Fetcher(host, api, async _ => { await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L); backups++; return "pre-import.db"; });
            var result = await fetcher.PullAsync(repo.Id, 1);
            await Assert.That(result.Created).IsEqualTo(2); await Assert.That(result.MorePages).IsTrue();
            var current = await host.Store.QueryAsync((s, ct) => s.GitHub.GetRepositoryAsync(repo.Id, ct));
            await Assert.That(current!.IssuesCursorUtc).IsNull();
            await Assert.That(current.InitialImportDoneUtc).IsNull();
        }
        using var restarted = await DataHost.StartAsync(root);
        using var next = Fetcher(restarted, api, _ => { backups++; return Task.FromResult("unexpected.db"); });
        await Assert.That((await next.PullAsync(repo.Id, 1)).Created).IsEqualTo(2);
        var done = await next.PullAsync(repo.Id, 1);
        await Assert.That(done.Created).IsEqualTo(1); await Assert.That(done.MorePages).IsFalse();
        await Assert.That(backups).IsEqualTo(1);
        await Assert.That(await restarted.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(5L);
        var completed = await restarted.Store.QueryAsync((s, ct) => s.GitHub.GetRepositoryAsync(repo.Id, ct));
        await Assert.That(completed!.Version).IsEqualTo(repo.Version);
        await Assert.That(completed.IssuesCursorUtc).IsEqualTo(DataHost.Start.AddSeconds(5));
        await Assert.That(completed.InitialImportDoneUtc).IsNotNull();
        await next.PullAsync(repo.Id); // Changed query: no reuse of the initial scan's ETag.
        await Assert.That((await next.PullAsync(repo.Id)).NotModified).IsTrue();
    }

    [Test]
    public async Task FetchFailureRetainsCursor_AndRetryDoesNotDuplicateTasksOrHistory()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, _) = await Setup(host, api, 5);
        using var fetcher = Fetcher(host, api, _ => Task.FromResult("backup.db"));
        await fetcher.PullAsync(repo.Id, 1);
        var before = await host.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct));
        api.NextFailure = GitHubErrorKind.Network;
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<GitHubApiException>();
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct))).IsEqualTo(before);
        await fetcher.PullAsync(repo.Id);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(5L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM TaskHistory WHERE EventKind='github.imported';")).IsEqualTo(5L);
    }

    [Test]
    public async Task BackupFailureAppliesNothing()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, _) = await Setup(host, api, 60);
        using var fetcher = Fetcher(host, api, _ => throw new IOException("Simulated backup failure."));
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<IOException>();
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct))).IsNull();
    }

    [Test]
    public async Task SettingsChangeDuringBackupStopsTheFetchedPage()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, commands) = await Setup(host, api, 2);
        using var fetcher = Fetcher(host, api, async _ => { await commands.UpdateRepositoryAsync(new(DataHost.Op(), repo.Id, repo.Version, repo.TargetContainerId, null, new(["bug"]), false)); return "backup.db"; });
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<AppException>();
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
    }

    [Test]
    public async Task AccountMismatchAppliesNothing_AndPausedRepositoryDoesNotReadNetwork()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, commands) = await Setup(host, api, 2);
        using var fetcher = Fetcher(host, api, _ => Task.FromResult("backup.db"));
        api.Account = new(2, "other");
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<GitHubApiException>();
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
        await commands.SetRepositoryPausedAsync(new(DataHost.Op(), repo.Id, repo.Version, true));
        var reads = api.Requests;
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<AppException>();
        await Assert.That(api.Requests).IsEqualTo(reads);
    }

    [Test]
    public async Task PageOverFiftyIssuesIsAppliedInBoundedBatches()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub { PageSize = 100 }; var (repo, _) = await Setup(host, api, 60);
        using var fetcher = Fetcher(host, api, _ => Task.FromResult("backup.db"));
        var result = await fetcher.PullAsync(repo.Id);
        await Assert.That(result.Created).IsEqualTo(60);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(60L);
    }
}
