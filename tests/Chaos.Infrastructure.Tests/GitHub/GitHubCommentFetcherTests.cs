using Chaos.Infrastructure.DataProtection;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubCommentFetcherTests
{
    private static GitHubComment Comment(long id, string body = "Original") => new(id, body, new(2, "other"), DataHost.Start.AddDays(-2).AddSeconds(id), DataHost.Start.AddDays(-2).AddSeconds(id), "https://github.com/person/repo/issues/1#issuecomment-" + id);
    private static async Task<(GitHubRepositoryRecord Repo, string Link)> Setup(DataHost host, FakeGitHub api, int count)
    {
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var c = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(c.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), c.Id, c.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        var issue = new GitHubIssue(1, "I1", 1, "https://github.com/person/repo/issues/1", "Issue", "", "open", null, new(1, "person"), [], [], null, null, DataHost.Start, DataHost.Start, null, count, null);
        await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [issue]);
        api.PutComments("person/repo", 1, Enumerable.Range(1, count).Select(i => Comment(i)));
        return (repo, (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(c.Id, 1, ct)))!.Id);
    }
    private static GitHubCommentFetcher Fetcher(DataHost host, FakeGitHub api) => new(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
    private static Task<IReadOnlyList<GitHubCommentLinkRecord>> Mappings(DataHost host, string link) => host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link, ct));

    [Test]
    public async Task CommentProgressIsExcludedFromArchivesAndClearedByRestore()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, link) = await Setup(host, api, 5);
        using var fetcher = Fetcher(host, api); await fetcher.PullAsync(repo.Id, 2);
        var tables = await host.Store.QueryAsync((s, ct) => Chaos.Infrastructure.Transfer.JsonArchive.DumpAsync(((Chaos.Infrastructure.Sqlite.SqliteDataSession)s).Connection, ct));
        await Assert.That(tables.Any(t => t.Name == "GitHubCommentCheckpoints")).IsFalse();
        var backup = await host.Backups.CreateAsync(BackupReason.Manual, CancellationToken.None);
        await host.Protection.RestoreBackupAsync(backup.FileName, backup.ChecksumSha256, CancellationToken.None);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCommentCheckpointAsync(link, ct))).IsNull();
    }

    [Test]
    public async Task InitialImportFetchesOnlyNewestHundred_AndFullChecksDoNotImportOlderComments()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub { PageSize = 100 }; var (repo, link) = await Setup(host, api, 250);
        using var fetcher = Fetcher(host, api);
        var result = await fetcher.PullAsync(repo.Id);
        await Assert.That(result.MorePages).IsFalse();
        var mappings = await Mappings(host, link);
        await Assert.That(mappings.Count).IsEqualTo(100);
        await Assert.That(mappings.Min(m => m.CommentId)).IsEqualTo(151L);
        await Assert.That(mappings.Max(m => m.CommentId)).IsEqualTo(250L);
        await fetcher.PullAsync(repo.Id);
        await Assert.That((await Mappings(host, link)).Count).IsEqualTo(100);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM DiaryRevisions;")).IsEqualTo(100L);
    }

    [Test]
    public async Task TailAndDeletionCheckResumeAcrossRestart_WithoutPersistingCommentText()
    {
        var api = new FakeGitHub(); string root; GitHubRepositoryRecord repo; string link;
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root; (repo, link) = await Setup(host, api, 5);
            using var fetcher = Fetcher(host, api);
            var result = await fetcher.PullAsync(repo.Id, 2);
            await Assert.That(result.MorePages).IsTrue();
            await Assert.That((await Mappings(host, link)).Count).IsEqualTo(0);
        }
        using var restarted = await DataHost.StartAsync(root); using var next = Fetcher(restarted, api);
        var finished = false;
        for (var pass = 0; pass < 12; pass++) { var result = await next.PullAsync(repo.Id, 2); await Assert.That(result.PagesRead <= 2).IsTrue(); if (!result.MorePages) { finished = true; break; } }
        await Assert.That(finished).IsTrue();
        await Assert.That((await Mappings(restarted, link)).Count).IsEqualTo(5);
        var checkpoint = await restarted.Store.QueryAsync((s, ct) => s.GitHub.GetCommentCheckpointAsync(link, ct));
        await Assert.That(checkpoint!.PayloadJson.Contains("Original", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task IncrementalFeedMirrorsEditsAndNewComments_WithConditionalNoChangeReads()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub { PageSize = 100 }; var (repo, link) = await Setup(host, api, 2);
        using var fetcher = Fetcher(host, api); await fetcher.PullAsync(repo.Id);
        api.PutComments("person/repo", 1, [Comment(1, "Remote edit") with { UpdatedAt = DataHost.Start.AddMinutes(1) }, Comment(2), Comment(3, "New comment") with { CreatedAt = DataHost.Start.AddMinutes(2), UpdatedAt = DataHost.Start.AddMinutes(2) }]);
        await fetcher.PullAsync(repo.Id);
        var mappings = await Mappings(host, link);
        await Assert.That(mappings.Count).IsEqualTo(3);
        var entry = await host.Store.QueryAsync((s, ct) => s.Diary.GetAsync(mappings.Single(m => m.CommentId == 1).DiaryEntryId, ct));
        await Assert.That(entry!.RawMarkdown).IsEqualTo("Remote edit");
        await Assert.That(entry.Revision).IsEqualTo(2L);
        await fetcher.PullAsync(repo.Id); await fetcher.PullAsync(repo.Id);
        await Assert.That(api.NotModifiedCount > 0).IsTrue();
        await Assert.That((await Mappings(host, link)).Count).IsEqualTo(3);
    }

    [Test]
    public async Task DailyDeletionCheckMarksMissingOnlyAfterAllPages_AndKeepsDiary()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, link) = await Setup(host, api, 5);
        using var fetcher = Fetcher(host, api); await fetcher.PullAsync(repo.Id);
        api.PutComments("person/repo", 1, Enumerable.Range(2, 4).Select(i => Comment(i)));
        host.Clock.Now = host.Clock.Now.AddDays(1);
        var partial = await fetcher.PullAsync(repo.Id, 2);
        await Assert.That(partial.MorePages).IsTrue();
        await Assert.That((await Mappings(host, link)).Single(m => m.CommentId == 1).State).IsEqualTo(0);
        for (var i = 0; i < 4; i++) if (!(await fetcher.PullAsync(repo.Id, 2)).MorePages) break;
        var deleted = (await Mappings(host, link)).Single(m => m.CommentId == 1);
        await Assert.That(deleted.State).IsEqualTo(4);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Diary.GetAsync(deleted.DiaryEntryId, ct)))!.RawMarkdown).IsEqualTo("Original");
    }

    [Test]
    public async Task FailedReadDoesNotAdvanceCheckpoint_AndAccountMismatchWritesNothing()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, link) = await Setup(host, api, 5);
        using var fetcher = Fetcher(host, api); await fetcher.PullAsync(repo.Id, 2);
        var checkpoint = await host.Store.QueryAsync((s, ct) => s.GitHub.GetCommentCheckpointAsync(link, ct));
        api.NextFailure = GitHubErrorKind.Network;
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<GitHubApiException>();
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCommentCheckpointAsync(link, ct))).IsEqualTo(checkpoint);
        api.Account = new(3, "other");
        await Assert.That(async () => await fetcher.PullAsync(repo.Id)).Throws<GitHubApiException>();
        await Assert.That((await Mappings(host, link)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task HiddenAndOutOfScopeCommentsStayUntouched()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (repo, link) = await Setup(host, api, 1);
        using var fetcher = Fetcher(host, api); await fetcher.PullAsync(repo.Id);
        await host.Store.ExecuteAsync(async (s, ct) => { var map = (await s.GitHub.ListCommentLinksAsync(link, ct)).Single(); await s.GitHub.UpsertCommentLinkAsync(map with { State = 5 }, ct); var row = (await s.GitHub.GetIssueLinkAsync(link, ct))!; await s.GitHub.UpdateIssueLinkAsync(row with { State = 1 }, row.Version, ct); return true; });
        api.PutComments("person/repo", 1, [Comment(1, "Changed remotely") with { UpdatedAt = DataHost.Start.AddHours(1) }]);
        await fetcher.PullAsync(repo.Id);
        var mapping = (await Mappings(host, link)).Single();
        await Assert.That(mapping.State).IsEqualTo(5);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Diary.GetAsync(mapping.DiaryEntryId, ct)))!.RawMarkdown).IsEqualTo("Original");
    }
}
