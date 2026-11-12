using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubCheckpointTests
{
    private static async Task<GitHubRepositoryRecord> Setup(DataHost host)
    {
        var api = new FakeGitHub(); api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var c = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(c.SpaceId, ct))!.Id);
        return await commands.AddRepositoryAsync(new(DataHost.Op(), c.Id, c.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
    }

    [Test]
    public async Task Checkpoint_RoundTripsAfterRestart_AndStaleSettingsCannotSaveProgress()
    {
        string root; GitHubRepositoryRecord repo;
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root; repo = await Setup(host);
            await host.Store.ExecuteAsync(async (s, ct) =>
            {
                await Assert.That(await s.GitHub.SaveCheckpointAsync(new(repo.Id, "issues", repo.Version, "{\"nextCursor\":\"page2\"}", DataHost.Start), ct)).IsTrue();
                await Assert.That(await s.GitHub.SaveCheckpointAsync(new(repo.Id, "issues", repo.Version + 1, "{}", DataHost.Start), ct)).IsFalse();
                return true;
            });
        }
        using var restarted = await DataHost.StartAsync(root);
        var row = await restarted.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct));
        await Assert.That(row!.PayloadJson).IsEqualTo("{\"nextCursor\":\"page2\"}");
        var commands = new GitHubConnectionCommands(restarted.Store, new FakeGitHub(), restarted.Ids, restarted.Clock);
        var changed = await commands.UpdateRepositoryAsync(new(DataHost.Op(), repo.Id, repo.Version, repo.TargetContainerId, null, new(["bug"]), false));
        await Assert.That(await restarted.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct))).IsNull();
        await restarted.Store.ExecuteAsync(async (s, ct) => { await Assert.That(await s.GitHub.SaveCheckpointAsync(new(repo.Id, "issues", repo.Version, "{}", DataHost.Start), ct)).IsFalse(); return true; });
        await Assert.That(changed.Version).IsEqualTo(repo.Version + 1);
    }

    [Test]
    public async Task ArchiveOmitsCheckpoints_AndBackupRestoreClearsMachineScanState()
    {
        using var host = await DataHost.StartAsync(); var repo = await Setup(host);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.GitHub.SaveCheckpointAsync(new(repo.Id, "issues", repo.Version, "{\"nextCursor\":\"page2\"}", DataHost.Start), ct);
            var current = (await s.GitHub.GetRepositoryAsync(repo.Id, ct))!;
            await s.GitHub.UpdateRepositoryAsync(current with { IssuesEtag = "machine-etag", IssuesCursorUtc = DataHost.Start }, current.Version, ct);
            return true;
        });
        var tables = await host.Store.QueryAsync((s, ct) => JsonArchive.DumpAsync(((Chaos.Infrastructure.Sqlite.SqliteDataSession)s).Connection, ct));
        await Assert.That(tables.Any(t => t.Name == "GitHubSyncCheckpoints")).IsFalse();
        var backup = await host.Backups.CreateAsync(BackupReason.Manual, CancellationToken.None);
        await host.Protection.RestoreBackupAsync(backup.FileName, backup.ChecksumSha256, CancellationToken.None);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            await Assert.That(await s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct)).IsNull();
            var restored = (await s.GitHub.GetRepositoryAsync(repo.Id, ct))!;
            await Assert.That(restored.IssuesEtag).IsNull();
            await Assert.That(restored.IssuesCursorUtc).IsNull();
            return true;
        });
    }
}
