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
