using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class GitHubSyncRpcTests
{
    [Test]
    public async Task TaskAction_ReturnsBeforeRemoteWrite_AndBackgroundSyncsOnlyThatIssue()
    {
        var clock = new FixedTimeProvider(new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var host = new { Store = storage.Store, Ids = ids, Clock = clock, Tasks = new Chaos.Application.Tasks.TaskCommands(storage.Store, ids, clock) };
        var api = new FakeGitHub { Now = clock.GetUtcNow() };
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        var issue = new Chaos.Application.Abstractions.GitHubIssue(1, "I1", 1, "https://github.com/person/repo/issues/1", "Issue", "Body", "open", null, new(1, "person"), [], [], null, null, host.Clock.GetUtcNow(), host.Clock.GetUtcNow(), null, 0, null);
        api.PutIssue("person/repo", issue);
        var inbound = new GitHubInboundCommands(host.Store, host.Ids, host.Clock);
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        using var issues = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("unused.db"), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        using var engine = new GitHubSyncEngine(new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock), host.Clock);
        var rpc = new TaskRpcService(host.Tasks, engine);
        var github = new GitHubRpcService(commands, host.Store, engine);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.AfterIssueRead = async _ => { entered.TrySetResult(); await release.Task; };
        try
        {
            var result = await rpc.SetStatusAsync(new(Op(), task.Id, task.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), "Done", null), CancellationToken.None);
            await Assert.That(result.Ok).IsTrue();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(api.RemoteWrites).IsEqualTo(0);
            await Assert.That((await github.TaskSyncAsync(new(task.Id), CancellationToken.None)).Syncing).IsTrue();
            api.AfterIssueRead = null; release.TrySetResult();
            await engine.WaitForIdleAsync();
            await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).State).IsEqualTo("closed");
            await Assert.That(api.IssueListStates.Count).IsEqualTo(0);
            var info = await github.TaskSyncAsync(new(task.Id), CancellationToken.None);
            await Assert.That(info.Syncing).IsFalse();
            await Assert.That(info.Pending).IsFalse();
        }
        finally { api.AfterIssueRead = null; release.TrySetResult(); }
    }

    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task SyncNowImportsIssueAndCommentsWithRealBackup_AndRepeatIsIdempotent()
    {
        var clock = new FixedTimeProvider(new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)); var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var api = new FakeGitHub();
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        api.PutIssue("person/repo", new(1, "I1", 1, "https://github.com/person/repo/issues/1", "Imported issue", "- [ ] First", "open", null, new(1, "person"), [], [], null, null, clock.GetUtcNow(), clock.GetUtcNow(), null, 2, null));
        api.PutComments("person/repo", 1, [new(11, "First comment", new(1, "person"), clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddDays(-1), "https://github.com/person/repo/issues/1#11"), new(12, "Second comment", new(2, "other"), clock.GetUtcNow().AddHours(-1), clock.GetUtcNow().AddHours(-1), "https://github.com/person/repo/issues/1#12")]);
        var commands = new GitHubConnectionCommands(storage.Store, api, ids, clock);
        var connection = await commands.ConnectAsync(new(Op()));
        var inbox = await storage.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        var backups = new BackupManager(storage.Store.Source, storage.Options.BackupsDirectory, "0.1.0", clock);
        using var issues = new GitHubIssueFetcher(storage.Store, api, new(storage.Store, ids, clock), async ct => (await backups.CreateAsync(BackupReason.PreImport, ct)).FileName, clock);
        using var comments = new GitHubCommentFetcher(storage.Store, api, new(storage.Store, ids, clock), clock);
        using var engine = new GitHubSyncEngine(new GitHubSyncCommands(storage.Store, api, issues, comments, ids, clock), clock);
        var rpc = new GitHubRpcService(commands, storage.Store, engine);
        await Assert.That((await rpc.SyncNowAsync(new(repo.Id), CancellationToken.None)).Ok).IsTrue();
        await engine.WaitForIdleAsync();
        var status = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(status.Sync!.Error).IsNull();
        await Assert.That(status.Sync.Created).IsEqualTo(1);
        await Assert.That(status.Sync.CommentChanges).IsEqualTo(2);
        await Assert.That(backups.Latest()!.Reason).IsEqualTo(BackupReason.PreImport);
        await storage.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            await Assert.That((await s.Subtasks.ListByTaskAsync(link.TaskId!, ct)).Count).IsEqualTo(1);
            await Assert.That((await s.Diary.ListByTaskAsync(link.TaskId!, false, ct)).Count).IsEqualTo(2);
            await Assert.That((await s.GitHub.ListSyncRunsAsync(connection.Id, ct)).Count).IsEqualTo(1);
            return true;
        });
        await rpc.SyncNowAsync(new(null), CancellationToken.None); await engine.WaitForIdleAsync();
        status = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(status.Sync!.Created).IsEqualTo(0);
        await Assert.That(status.Sync.CommentChanges).IsEqualTo(0);
        var linked = (await storage.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var syncInfo = await rpc.TaskSyncAsync(new(linked.TaskId!), CancellationToken.None);
        await Assert.That(syncInfo.Connected).IsTrue();
        await Assert.That(syncInfo.Pending).IsFalse();
        await Assert.That(syncInfo.IssueUrl).IsEqualTo("https://github.com/person/repo/issues/1");
        await Assert.That(syncInfo.Repository).IsEqualTo("person/repo");
        var requestsBeforeActions = api.Requests;
        await Assert.That((await rpc.RetryTaskAsync(new(Op(), linked.TaskId!, "bad-version"), CancellationToken.None)).Error!.Kind).IsEqualTo("validation");
        await Assert.That((await rpc.DismissConflictAsync(new(Op(), linked.TaskId!, syncInfo.LinkVersion!), CancellationToken.None)).Ok).IsTrue();
        await Assert.That((await rpc.RetryTaskAsync(new(Op(), linked.TaskId!, syncInfo.LinkVersion!), CancellationToken.None)).Error!.Kind).IsEqualTo("versionConflict");
        var currentInfo = await rpc.TaskSyncAsync(new(linked.TaskId!), CancellationToken.None);
        await Assert.That((await rpc.RetryTaskAsync(new(Op(), linked.TaskId!, currentInfo.LinkVersion!), CancellationToken.None)).Ok).IsTrue();
        await engine.WaitForIdleAsync();
        await Assert.That(api.Requests > requestsBeforeActions).IsTrue();
        await storage.Store.ExecuteAsync(async (s, ct) => { var task = (await s.Tasks.GetAsync(linked.TaskId!, ct))!; await s.Tasks.UpdateAsync(task with { Title = "Local pending edit", Version = task.Version + 1 }, task.Version, ct); return true; });
        var callsBeforeStatus = api.Requests;
        await Assert.That((await rpc.TaskSyncAsync(new(linked.TaskId!), CancellationToken.None)).Pending).IsTrue();
        await Assert.That(api.Requests).IsEqualTo(callsBeforeStatus);
        api.Account = new(2, "other");
        await rpc.SyncNowAsync(new(null), CancellationToken.None); await engine.WaitForIdleAsync();
        status = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(status.Sync!.Error!.Kind).IsEqualTo("githubAccountMismatch");
        await Assert.That(status.Connection!.State).IsEqualTo("NeedsSignIn");
        await commands.DisconnectAsync(new(Op(), connection.Id, connection.Version));
        var replacement = await commands.ConnectAsync(new(Op()));
        var reads = api.Requests;
        engine.RequestSync(null, connection.Id);
        await engine.WaitForIdleAsync();
        await Assert.That(engine.Status.AppError!.Kind).IsEqualTo(Chaos.Application.Abstractions.AppErrorKind.VersionConflict);
        await Assert.That(api.Requests).IsEqualTo(reads);
        status = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(status.Connection!.Id).IsEqualTo(replacement.Id);
        await Assert.That(status.Sync!.Created).IsNull();
        await Assert.That(status.Sync.Error).IsNull();
    }
}
