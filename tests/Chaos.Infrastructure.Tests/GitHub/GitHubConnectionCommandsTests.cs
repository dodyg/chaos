using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubConnectionCommandsTests
{
    private static GitHubConnectionCommands Commands(DataHost host, FakeGitHub api) => new(host.Store, api, host.Ids, host.Clock);
    private static GitHubRepository Remote(long id = 9, bool issues = true) => new(id, "R9", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, issues);
    private static async Task<AddGitHubRepositoryCommand> AddCommand(DataHost host, GitHubConnectionRecord connection) => new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 9,
        await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id), null, GitHubFilter.Empty, false);

    [Test]
    public async Task Status_IsLocalOnly_AndConnectionPersistsAcrossRestart()
    {
        string root;
        string id;
        var api = new FakeGitHub();
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root;
            var commands = Commands(host, api);
            await Assert.That((await commands.StatusAsync()).Connection).IsNull();
            await Assert.That(api.Requests).IsEqualTo(0);
            id = (await commands.ConnectAsync(new(DataHost.Op()))).Id;
        }
        using var restarted = await DataHost.StartAsync(root);
        await Assert.That((await Commands(restarted, api).StatusAsync()).Connection!.Id).IsEqualTo(id);
        await Assert.That(api.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Connect_ReplaysWithoutNetwork_AndRejectsOtherAccount()
    {
        using var host = await DataHost.StartAsync();
        var api = new FakeGitHub(); var commands = Commands(host, api); var command = new ConnectGitHubCommand(DataHost.Op());
        var first = await commands.ConnectAsync(command);
        api.Account = new(2, "other");
        await Assert.That(await commands.ConnectAsync(command)).IsEqualTo(first);
        await Assert.That(api.Requests).IsEqualTo(1);
        await Assert.That(async () => await commands.ConnectAsync(new(DataHost.Op()))).Throws<AppException>();
        await Assert.That((await commands.StatusAsync()).Connection!.AccountId).IsEqualTo(1L);
    }

    [Test]
    public async Task Connect_MissingCli_DoesNotCreateConnection()
    {
        using var host = await DataHost.StartAsync();
        var api = new FakeGitHub { NextFailure = GitHubErrorKind.CliUnavailable }; var commands = Commands(host, api);
        await Assert.That(async () => await commands.ConnectAsync(new(DataHost.Op()))).Throws<GitHubApiException>();
        await Assert.That((await commands.StatusAsync()).Connection).IsNull();
    }

    [Test]
    public async Task Repository_AddDiscoverReplayAndReceiptConflict()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); api.PutRepository(Remote()); var commands = Commands(host, api);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var command = await AddCommand(host, connection);
        var added = await commands.AddRepositoryAsync(command); var requests = api.Requests;
        await Assert.That(await commands.AddRepositoryAsync(command)).IsEqualTo(added);
        await Assert.That(api.Requests).IsEqualTo(requests);
        await Assert.That(async () => await commands.AddRepositoryAsync(command with { MilestonesAsProjects = true })).Throws<AppException>();
        await Assert.That((await commands.ListReachableRepositoriesAsync(null)).Repositories.Single().AlreadyAdded).IsTrue();
        await Assert.That((await commands.LookupRepositoryAsync("person", "repo")).AlreadyAdded).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Repository_RejectsUnavailableIssuesOrAccountMismatch(bool mismatch)
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); api.PutRepository(Remote(issues: mismatch)); var commands = Commands(host, api);
        var connection = await commands.ConnectAsync(new(DataHost.Op())); var command = await AddCommand(host, connection);
        if (mismatch) api.Account = new(2, "other");
        if (mismatch) await Assert.That(async () => await commands.AddRepositoryAsync(command)).Throws<GitHubApiException>();
        else await Assert.That(async () => await commands.AddRepositoryAsync(command)).Throws<AppException>();
        await Assert.That((await commands.StatusAsync()).Repositories.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Repository_FilterChangeResetsCursor_AndStaleUpdateFails()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); api.PutRepository(Remote()); var commands = Commands(host, api);
        var connection = await commands.ConnectAsync(new(DataHost.Op())); var added = await commands.AddRepositoryAsync(await AddCommand(host, connection));
        added = (await host.Store.ExecuteAsync((s, ct) => s.GitHub.UpdateRepositoryAsync(added with { IssuesEtag = "etag", IssuesCursorUtc = DataHost.Start, InitialImportDoneUtc = DataHost.Start }, added.Version, ct)))!;
        var command = new UpdateGitHubRepositoryCommand(DataHost.Op(), added.Id, added.Version, added.TargetContainerId, null, new(["bug"], IncludeClosedDays: 7), true);
        var updated = await commands.UpdateRepositoryAsync(command);
        await Assert.That(updated.IssuesEtag).IsNull();
        await Assert.That(updated.IssuesCursorUtc).IsNull();
        await Assert.That(updated.InitialImportDoneUtc).IsNull();
        await Assert.That(async () => await commands.UpdateRepositoryAsync(command with { OperationId = DataHost.Op() })).Throws<AppException>();
        await Assert.That(async () => await commands.UpdateRepositoryAsync(command with { OperationId = DataHost.Op(), ExpectedVersion = updated.Version, TargetSectionId = Guid.NewGuid().ToString() })).Throws<AppException>();
    }

    [Test]
    public async Task PauseAndSettings_AreVersionedAndLocal()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var commands = Commands(host, api);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var paused = await commands.SetConnectionPausedAsync(new(DataHost.Op(), connection.Id, connection.Version, true));
        var settings = await commands.UpdateSettingsAsync(new(DataHost.Op(), connection.Id, paused.Version, false, true));
        await Assert.That(settings.State).IsEqualTo(1);
        await Assert.That(settings.PostDiaryByDefault).IsFalse();
        await Assert.That(settings.CreateLabelsForTags).IsTrue();
        await Assert.That(api.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Disconnect_DetachesLinksClearsMappingsAndPreservesTasksAndDiary()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); api.PutRepository(Remote()); var commands = Commands(host, api);
        var connection = await commands.ConnectAsync(new(DataHost.Op())); var repo = await commands.AddRepositoryAsync(await AddCommand(host, connection));
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), repo.TargetContainerId, "Keep this task", null))).Task;
        var linkId = Guid.NewGuid().ToString();
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.GitHub.AddIssueLinkAsync(new GitHubIssueLinkRecord { Id = linkId, ConnectionId = connection.Id, RepositorySubscriptionId = repo.Id,
                TaskId = task.Id, IssueId = 12, IssueNodeId = "I12", IssueNumber = 12, HtmlUrl = "https://github.com/person/repo/issues/12", BaseJson = "{}",
                BaseRemoteUpdatedUtc = DataHost.Start, CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start, LocalDirty = true }, ct);
            var diaryId = Guid.NewGuid().ToString();
            await s.Diary.AddAsync(new DiaryEntryRecord { Id = diaryId, TaskId = task.Id, AuthorProfileId = connection.ProfileId,
                RawMarkdown = "Original comment", EntryAtUtc = DataHost.Start, Revision = 1, Version = 1, CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start }, ct);
            await s.GitHub.UpsertCommentLinkAsync(new GitHubCommentLinkRecord { DiaryEntryId = diaryId, IssueLinkId = linkId, State = 1 }, ct);
            for (var i = 0; i < 201; i++)
                await s.GitHub.AddIssueLinkAsync(new GitHubIssueLinkRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id, RepositorySubscriptionId = repo.Id,
                    IssueId = 100 + i, IssueNodeId = "I" + i, IssueNumber = 100 + i, HtmlUrl = "https://github.com/person/repo/issues/" + (100 + i), BaseJson = "{}",
                    BaseRemoteUpdatedUtc = DataHost.Start, CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start, State = 3 }, ct);
            return true;
        });
        var request = new ChangeGitHubCommand(DataHost.Op(), connection.Id, connection.Version);
        await commands.DisconnectAsync(request);
        await commands.DisconnectAsync(request);
        await Assert.That((await commands.StatusAsync()).Connection).IsNull();
        await host.Store.QueryAsync(async (s, ct) =>
        {
            await Assert.That((await s.Tasks.GetAsync(task.Id, ct))!.Title).IsEqualTo("Keep this task");
            await Assert.That((await s.GitHub.GetIssueLinkAsync(linkId, ct))!.State).IsEqualTo(4);
            await Assert.That((await s.GitHub.GetRepositoryAsync(repo.Id, ct))!.DeletedAtUtc).IsNotNull();
            await Assert.That((await s.Diary.ListByTaskAsync(task.Id, false, ct)).Count).IsEqualTo(2);
            await Assert.That((await s.GitHub.ListCommentLinksAsync(linkId, ct)).Count).IsEqualTo(0);
            var firstPage = await s.GitHub.ListRepositoryLinksAsync(repo.Id, null, 200, ct);
            var lastPage = await s.GitHub.ListRepositoryLinksAsync(repo.Id, firstPage[^1].Id, 200, ct);
            await Assert.That(firstPage.Count + lastPage.Count).IsEqualTo(202);
            await Assert.That(firstPage.Concat(lastPage).All(l => !l.LocalDirty && l.State is 3 or 4)).IsTrue();
            return true;
        });
        var next = await commands.ConnectAsync(new(DataHost.Op()));
        await Assert.That(next.Id == connection.Id).IsFalse();
        await Assert.That(api.Requests).IsEqualTo(3);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(366)]
    public async Task Filter_RejectsInvalidDays(int days) => await Assert.That(() => new GitHubFilter([], IncludeClosedDays: days).ToJson()).Throws<AppException>();
}
