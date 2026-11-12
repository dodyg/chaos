using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubInboundCommentsTests
{
    private static async Task<(string LinkId, long Version)> Setup(DataHost host)
    {
        var api = new FakeGitHub(); api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [new(1, "I1", 1, "https://github.com/person/repo/issues/1", "Issue", "", "open", null, new(1, "person"), [], [], null, null, DataHost.Start, DataHost.Start, null, 0, null)]);
        var link = await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct));
        return (link!.Id, link.Version);
    }
    private static GitHubComment Comment(long id, string body = "Original") => new(id, body, new(2, "other"), DataHost.Start.AddSeconds(id), DataHost.Start.AddSeconds(id), "https://github.com/person/repo/issues/1#issuecomment-" + id);

    [Test]
    public async Task Import_OnlyNewestHundred_AndRepeatedFetchIsIdempotent()
    {
        using var host = await DataHost.StartAsync(); var (link, version) = await Setup(host);
        var inbound = new GitHubInboundComments(host.Store, host.Ids, host.Clock);
        var comments = Enumerable.Range(1, 105).Select(i => Comment(i)).ToArray();
        await Assert.That(await inbound.ApplyAsync(link, version, comments, true)).IsEqualTo(100);
        await Assert.That(await inbound.ApplyAsync(link, version, comments, true)).IsEqualTo(0);
        await host.Store.QueryAsync(async (s, ct) => { var mappings = await s.GitHub.ListCommentLinksAsync(link, ct); await Assert.That(mappings.Count).IsEqualTo(100); await Assert.That(mappings.Min(m => m.CommentId)).IsEqualTo(6L); var entry = (await s.Diary.GetAsync(mappings.First(m => m.CommentId == 6).DiaryEntryId, ct))!; await Assert.That(entry.EntryAtUtc).IsEqualTo(DataHost.Start.AddSeconds(6)); return true; });
    }

    [Test]
    public async Task RemoteEditPreservesLocalRevision_AndDeletionKeepsDiaryText()
    {
        using var host = await DataHost.StartAsync(); var (link, version) = await Setup(host);
        var inbound = new GitHubInboundComments(host.Store, host.Ids, host.Clock);
        await inbound.ApplyAsync(link, version, [Comment(1)], true);
        await host.Store.ExecuteAsync(async (s, ct) => { var mapping = (await s.GitHub.ListCommentLinksAsync(link, ct)).Single(); var entry = (await s.Diary.GetAsync(mapping.DiaryEntryId, ct))!; await s.Diary.UpdateAsync(entry with { Version = entry.Version + 1, RawMarkdown = "Local edit", Revision = entry.Revision + 1, EditedAtUtc = DataHost.Start }, entry.Version, ct); return true; });
        await inbound.ApplyAsync(link, version, [Comment(1, "Remote edit") with { UpdatedAt = DataHost.Start.AddMinutes(2) }], true);
        await inbound.ApplyAsync(link, version, [], false);
        await host.Store.QueryAsync(async (s, ct) => { var mapping = (await s.GitHub.ListCommentLinksAsync(link, ct)).Single(); await Assert.That(mapping.State).IsEqualTo(0); return true; });
        await inbound.ApplyAsync(link, version, [], true);
        await host.Store.QueryAsync(async (s, ct) => { var mapping = (await s.GitHub.ListCommentLinksAsync(link, ct)).Single(); await Assert.That(mapping.State).IsEqualTo(4); var entry = (await s.Diary.GetAsync(mapping.DiaryEntryId, ct))!; await Assert.That(entry.RawMarkdown).IsEqualTo("Remote edit"); var revisions = await s.Diary.ListRevisionsAsync(entry.Id, ct); await Assert.That(revisions.Any(r => r.RawMarkdown == "Local edit")).IsTrue(); return true; });
    }

    [Test]
    public async Task HiddenCommentDoesNotReappear_AndStaleLinkFailsClosed()
    {
        using var host = await DataHost.StartAsync(); var (link, version) = await Setup(host);
        var inbound = new GitHubInboundComments(host.Store, host.Ids, host.Clock);
        await inbound.ApplyAsync(link, version, [Comment(1)], true);
        await host.Store.ExecuteAsync(async (s, ct) => { var mapping = (await s.GitHub.ListCommentLinksAsync(link, ct)).Single(); await s.GitHub.UpsertCommentLinkAsync(mapping with { State = 5 }, ct); var entry = (await s.Diary.GetAsync(mapping.DiaryEntryId, ct))!; await s.Diary.UpdateAsync(entry with { Version = entry.Version + 1, DeletedAtUtc = DataHost.Start, Revision = entry.Revision + 1 }, entry.Version, ct); return true; });
        await inbound.ApplyAsync(link, version, [Comment(1, "New") with { UpdatedAt = DataHost.Start.AddMinutes(2) }], true);
        await host.Store.QueryAsync(async (s, ct) => { var mapping = (await s.GitHub.ListCommentLinksAsync(link, ct)).Single(); await Assert.That(mapping.State).IsEqualTo(5); await Assert.That(await s.Diary.GetAsync(mapping.DiaryEntryId, ct)).IsNull(); return true; });
        await Assert.That(async () => await inbound.ApplyAsync(link, version + 1, [], true)).Throws<Chaos.Application.Abstractions.AppException>();
    }
}
