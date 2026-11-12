using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Application.Transfer;
using Chaos.Application.Tags;
using Chaos.Application.History;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubOutboundCommandsTests
{
    [Test]
    public async Task RunTask_TwoDirtyIssues_SyncsOnlySelectedIssueWithoutRepositoryFeeds()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var first = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var issue = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        var secondIssue = issue with { Id = 2, NodeId = "I2", Number = 2, HtmlUrl = "https://github.com/person/repo/issues/2" };
        api.PutIssue("person/repo", secondIssue);
        await inbound.ApplyAsync(repo.Id, repo.Version, [secondIssue]);
        var second = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 2, ct)))!;
        await SetTitle(host, first.Id, "First local edit");
        await SetTitle(host, second.Id, "Second local edit");
        await new DiaryCommands(host.Store, host.Ids, host.Clock).AddAsync(new(DataHost.Op(), first.TaskId!, "Post only to first", null, true));
        var issueReads = new List<long>();
        api.AfterIssueRead = remote => { issueReads.Add(remote.Number); return Task.CompletedTask; };
        using var issues = new GitHubIssueFetcher(host.Store, api, inbound, _ => throw new InvalidOperationException("A task action must not import a repository."), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        await new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock).RunTaskAsync(first.TaskId!, CancellationToken.None);
        await Assert.That(issueReads.All(number => number == 1)).IsTrue();
        await Assert.That(api.IssueListStates.Count).IsEqualTo(0);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "comments", ct))).IsNull();
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "comment-check", ct))).IsNull();
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Title).IsEqualTo("First local edit");
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 2, CancellationToken.None)).Title).IsEqualTo("Original");
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(second.Id, ct)))!.LocalDirty).IsTrue();
        await Assert.That((await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Count).IsEqualTo(1);
        await Assert.That((await api.ListCommentsAsync(api.Account, "person", "repo", null, 2, null, null, CancellationToken.None)).Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("ordinary")]
    [Arguments("paused")]
    [Arguments("ignored")]
    [Arguments("rate-limited")]
    public async Task RunTask_IneligibleTask_DoesNotContactGitHub(string scenario)
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await SetTitle(host, link.Id, "Local title");
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            if (scenario == "paused") await s.GitHub.UpdateRepositoryAsync(repo with { State = 1 }, repo.Version, ct);
            if (scenario == "ignored") await s.GitHub.UpdateIssueLinkAsync(link with { State = 3 }, link.Version, ct);
            if (scenario == "rate-limited") await s.GitHub.UpdateConnectionAsync(connection with { RateLimitResetUtc = host.Clock.GetUtcNow().AddHours(1) }, connection.Version, ct);
            return true;
        });
        var requests = api.Requests;
        using var issues = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("unused.db"), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        await new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock).RunTaskAsync(scenario == "ordinary" ? Guid.NewGuid().ToString() : link.TaskId!, CancellationToken.None);
        await Assert.That(api.Requests).IsEqualTo(requests);
    }

    [Test]
    public async Task CommentEdit_RemoteChangesAfterRead_DefersAndPreservesLocalRevision()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "Initial comment", null, true))).Entry;
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var remote = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Single();
        await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Local comment edit"));
        api.AfterCommentRead = () =>
        {
            api.AfterCommentRead = null;
            api.PutComments("person/repo", 1, [remote with { Body = GitHubCommentMarker.Body("Racing remote edit", entry.Id) }]);
            return Task.CompletedTask;
        };
        var writes = api.RemoteWrites;
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        await Assert.That(api.RemoteWrites).IsEqualTo(writes);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Diary.GetAsync(entry.Id, ct)))!.RawMarkdown).IsEqualTo("Racing remote edit");
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(link.Id, ct))).IsNull();
        var revisions = await host.Store.QueryAsync((s, ct) => s.Diary.ListRevisionsAsync(entry.Id, ct));
        await Assert.That(revisions.Any(r => r.RawMarkdown == "Local comment edit")).IsTrue();
    }

    [Test]
    public async Task CommentPost_TaskDeletedDuringMarkerRead_DoesNotPost()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await new DiaryCommands(host.Store, host.Ids, host.Clock).AddAsync(new(DataHost.Op(), link.TaskId!, "Unsent comment", null, true));
        api.AfterCommentRead = async () =>
        {
            api.AfterCommentRead = null;
            var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
            await host.Tasks.DeleteAsync(new(DataHost.Op(), task.Id, task.Version));
        };
        await new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock).RunAsync(repo.Id, true, CancellationToken.None);
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
    }

    [Test]
    public async Task Push_RemoteEditAfterInitialRead_DefersWriteAndKeepsConflictHistory()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await SetTitle(host, link.Id, "Local title");
        api.AfterIssueRead = issue =>
        {
            api.AfterIssueRead = null;
            // Even a change sharing the old timestamp must be noticed.
            api.PutIssue("person/repo", issue with { Title = "Racing remote title" });
            return Task.CompletedTask;
        };
        var result = await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(result.Conflicts).IsEqualTo(1);
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!.Title).IsEqualTo("Racing remote title");
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(link.Id, ct))).IsNull();
        var history = await host.Store.QueryAsync((s, ct) => s.History.ListByTaskAsync(link.TaskId!, ct));
        await Assert.That(history.Single(h => h.EventKind == "github.conflict").SnapshotBefore!.Contains("Local title", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Push_TaskDeletedDuringFinalRead_DiscardsUnsentIntent()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await SetTitle(host, link.Id, "Local title");
        var reads = 0;
        api.AfterIssueRead = async _ =>
        {
            if (++reads != 2) return;
            var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
            await host.Tasks.DeleteAsync(new(DataHost.Op(), task.Id, task.Version));
        };
        await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.State).IsEqualTo(3);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(link.Id, ct))).IsNull();
    }

    [Test]
    public async Task Lifecycle_DeleteRestoreAndPurge_NeverDeletesIssueOrReimportsPurgedTask()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        var deleted = await host.Tasks.DeleteAsync(new(DataHost.Op(), task.Id, task.Version));
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.State).IsEqualTo(3);
        var outbound = new GitHubOutboundCommands(host.Store, api, inbound, host.Clock);
        var requests = api.Requests;
        await outbound.PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(api.Requests).IsEqualTo(requests);
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        api.PutIssue("person/repo", remote with { Title = "Changed while trashed", UpdatedAt = remote.UpdatedAt.AddMinutes(1) });
        var restored = await host.Tasks.RestoreAsync(new(DataHost.Op(), task.Id, deleted.Task.Version));
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsTrue();
        await outbound.PushAsync(repo.Id, CancellationToken.None);
        task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(restored.Task.Id, ct)))!;
        await Assert.That(task.Title).IsEqualTo("Changed while trashed");
        await host.Tasks.DeleteAsync(new(DataHost.Op(), task.Id, task.Version));
        await host.Purge.PurgeAsync(task.Id, CancellationToken.None);
        var applied = await inbound.ApplyAsync(repo.Id, repo.Version, [await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)]);
        await Assert.That(applied.Created).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.TaskId).IsNull();
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
    }

    [Test]
    public async Task TaskActions_ConflictRetryDetach_UseVersionsReceiptsAndKeepLocalData()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.GitHub.UpdateIssueLinkAsync(link with { ConflictNoticeUtc = DataHost.Start, LastErrorKind = (int)GitHubErrorKind.Validation, NextAttemptUtc = DataHost.Start.AddHours(1) }, link.Version, ct);
            return true;
        });
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        await Assert.That(async () => await commands.DismissConflictAsync(new(DataHost.Op(), link.TaskId!, link.Version))).Throws<AppException>();
        link = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!;
        await commands.DismissConflictAsync(new(DataHost.Op(), link.TaskId!, link.Version));
        link = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!;
        await Assert.That(link.ConflictNoticeUtc).IsNull();
        await commands.RetryTaskAsync(new(DataHost.Op(), link.TaskId!, link.Version));
        link = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!;
        await Assert.That(link.NextAttemptUtc).IsNull();
        await Assert.That(link.LastErrorKind).IsNull();
        await host.Store.ExecuteAsync(async (s, ct) => { await s.GitHub.AddIssueWriteAsync(new(link.Id, StoredJsonFormats.Snapshot(new GitHubIssueWriteIntent("hash", null, null)), DataHost.Start), ct); return true; });
        var detach = new GitHubTaskActionCommand(DataHost.Op(), link.TaskId!, link.Version);
        var requests = api.Requests;
        await commands.KeepAsOrdinaryTaskAsync(detach);
        await commands.KeepAsOrdinaryTaskAsync(detach);
        await Assert.That(api.Requests).IsEqualTo(requests);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(link.Id, ct))).IsNull();
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Diary.ListByTaskAsync(link.TaskId!, false, ct))).Count).IsEqualTo(1);
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct))).IsNotNull();
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await inbound.ApplyAsync(repo.Id, repo.Version, [remote with { Title = "Remote later", UpdatedAt = remote.UpdatedAt.AddHours(1) }]);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!.Title).IsEqualTo("Original");
    }

    private static async Task<(GitHubRepositoryRecord Repo, GitHubConnectionRecord Connection, GitHubInboundCommands Inbound)> Setup(DataHost host, FakeGitHub api)
    {
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var issue = new GitHubIssue(1, "I1", 1, "https://github.com/person/repo/issues/1", "Original", "Body", "open", null, new(1, "person"), [], [], null, null, DataHost.Start, DataHost.Start, null, 0, null);
        api.PutIssue("person/repo", issue);
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        var inbound = new GitHubInboundCommands(host.Store, host.Ids, host.Clock);
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        return (repo, connection, inbound);
    }

    private static Task SetTitle(DataHost host, string linkId, string title) => host.Store.ExecuteAsync(async (s, ct) =>
    {
        var link = (await s.GitHub.GetIssueLinkAsync(linkId, ct))!;
        var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
        await s.Tasks.UpdateAsync(task with { Title = title, Version = task.Version + 1 }, task.Version, ct);
        return true;
    });

    [Test]
    public async Task LostPatchResponse_RestartAndNewLocalEdit_RecoversWithoutConflict()
    {
        var api = new FakeGitHub();
        string root, repoId, connectionId, linkId;
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root;
            var (repo, connection, inbound) = await Setup(host, api);
            repoId = repo.Id; connectionId = connection.Id;
            linkId = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!.Id;
            await SetTitle(host, linkId, "First local edit");
            api.AfterIssueWrite = () => throw new GitHubApiException(GitHubErrorKind.Network);
            await Assert.That(async () => await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
            await SetTitle(host, linkId, "Later local edit");
            var marker = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(linkId, ct)))!;
            await Assert.That(marker.PayloadJson.Contains("First local edit", StringComparison.Ordinal)).IsFalse();
            var export = await host.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(root, "out"), CancellationToken.None);
            await using var stream = File.OpenRead(export.Path);
            var archive = JsonArchive.Read(stream);
            var draft = ArchiveMerge.ToDraft(archive, "backup", "fingerprint");
            await Assert.That(draft.Tasks.Single(t => t.GitHubLink?.Id == linkId).GitHubIssueWrite).IsEqualTo(marker);
        }
        api.AfterIssueWrite = null;
        using var restarted = await DataHost.StartAsync(root);
        var outbound = new GitHubOutboundCommands(restarted.Store, api, new(restarted.Store, restarted.Ids, restarted.Clock), restarted.Clock);
        var recovery = await outbound.RecoverAsync(repoId, CancellationToken.None);
        await Assert.That(recovery.Conflicts).IsEqualTo(0);
        var link = (await restarted.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connectionId, 1, ct)))!;
        var task = (await restarted.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        await Assert.That(task.Title).IsEqualTo("Later local edit");
        await Assert.That(link.LocalDirty).IsTrue();
        await Assert.That(await restarted.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(link.Id, ct))).IsNull();
        await outbound.PushAsync(repoId, CancellationToken.None);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Title).IsEqualTo("Later local edit");
    }

    [Test]
    public async Task Push_ConcurrentRemoteEdit_RecordsConflictAndPreservesLocalHistory()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await SetTitle(host, link.Id, "Local title");
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        api.PutIssue("person/repo", remote with { Title = "Remote title", UpdatedAt = remote.UpdatedAt.AddMinutes(1) });
        var result = await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(result.Conflicts).IsEqualTo(1);
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
        var history = await host.Store.QueryAsync((s, ct) => s.History.ListByTaskAsync(link.TaskId!, ct));
        await Assert.That(history.Single(h => h.EventKind == "github.conflict").SnapshotBefore!.Contains("Local title", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Recovery_RemoteChangedAfterLostResponse_RecordsLaterLocalValue()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await SetTitle(host, link.Id, "Sent value");
        api.AfterIssueWrite = () => throw new GitHubApiException(GitHubErrorKind.Network);
        var outbound = new GitHubOutboundCommands(host.Store, api, inbound, host.Clock);
        await Assert.That(async () => await outbound.PushAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
        await SetTitle(host, link.Id, "Later local value");
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        api.PutIssue("person/repo", remote with { Title = "Later remote value", UpdatedAt = remote.UpdatedAt.AddMinutes(1) });
        var result = await outbound.RecoverAsync(repo.Id, CancellationToken.None);
        await Assert.That(result.Conflicts).IsEqualTo(1);
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        await Assert.That(task.Title).IsEqualTo("Later remote value");
        var history = await host.Store.QueryAsync((s, ct) => s.History.ListByTaskAsync(task.Id, ct));
        await Assert.That(history.Single(h => h.EventKind == "github.conflict").SnapshotBefore!.Contains("Later local value", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SyncNow_LostResponse_RecoversBeforeInboundAndSendsLaterEdit()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        using var issues = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("backup.db"), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        var sync = new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock);
        await SetTitle(host, link.Id, "First edit");
        api.AfterIssueWrite = () => throw new GitHubApiException(GitHubErrorKind.Network);
        await Assert.That(async () => await sync.RunAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
        await SetTitle(host, link.Id, "Later edit");
        api.AfterIssueWrite = null;
        var result = await sync.RunAsync(repo.Id, CancellationToken.None);
        await Assert.That(result.Conflicts).IsEqualTo(0);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Title).IsEqualTo("Later edit");
        var current = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!;
        await Assert.That(current.LocalDirty).IsFalse();
    }

    [Test]
    public async Task Push_BodyChecklistAndStatus_RoundTrip()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await s.Tasks.UpdateAsync(task with { Notes = "Updated body", Status = TaskItemStatus.Done, CompletedAtUtc = DataHost.Start, Version = task.Version + 1 }, task.Version, ct);
            return true;
        });
        var added = await new Chaos.Application.Tasks.SubtaskCommands(host.Store, host.Ids, host.Clock).AddAsync(new(DataHost.Op(), link.TaskId!, "Check this", null));
        var result = await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That(result.Pushed).IsEqualTo(1);
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await Assert.That(remote.Body).IsEqualTo("Updated body\n\n- [ ] Check this\n");
        await Assert.That(remote.State).IsEqualTo("closed");
        await Assert.That(remote.StateReason).IsEqualTo("completed");
        var subtasks = await host.Store.QueryAsync((s, ct) => s.Subtasks.ListByTaskAsync(link.TaskId!, ct));
        await Assert.That(subtasks.Count).IsEqualTo(1);
        await Assert.That(subtasks[0].Id).IsEqualTo(added.Subtask.Id);
        var current = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!;
        await Assert.That(current.LocalDirty).IsFalse();
    }

    [Test]
    public async Task Push_MappedLabelAndSelfAssignment_RoundTrip()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        var tag = (await tags.CreateAsync(new(DataHost.Op(), "bug", null, null))).Tag;
        api.PutLabels("person/repo", [new(11, "bug", "abcdef")]);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.Tags.AddTaskLinkAsync(link.TaskId!, tag.Id, ct);
            return true;
        });
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        await commands.SetSelfAssignmentAsync(new(DataHost.Op(), link.TaskId!, true));
        var outbound = new GitHubOutboundCommands(host.Store, api, inbound, host.Clock);
        await outbound.PushAsync(repo.Id, CancellationToken.None);
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await Assert.That(remote.Labels.Single().Id).IsEqualTo(11L);
        await Assert.That(remote.Assignees.Single().Id).IsEqualTo(connection.AccountId);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.Tags.RemoveTaskLinkAsync(link.TaskId!, tag.Id, ct);
            return true;
        });
        await commands.SetSelfAssignmentAsync(new(DataHost.Op(), link.TaskId!, false));
        await outbound.PushAsync(repo.Id, CancellationToken.None);
        remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await Assert.That(remote.Labels.Count).IsEqualTo(0);
        await Assert.That(remote.Assignees.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Push_EnabledLabelCreation_CreatesAndMapsNestedTag()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var tag = (await new TagCommands(host.Store, host.Ids, host.Clock).CreateAsync(new(DataHost.Op(), "area/ui", null, null))).Tag;
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.Tags.AddTaskLinkAsync(link.TaskId!, tag.Id, ct);
            await s.GitHub.UpdateConnectionAsync(connection with { CreateLabelsForTags = true }, connection.Version, ct);
            return true;
        });
        await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        var issue = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await Assert.That(issue.Labels.Single().Name).IsEqualTo("area/ui");
        var mappings = await host.Store.QueryAsync((s, ct) => s.GitHub.ListLabelTagsAsync(repo.Id, ct));
        await Assert.That(mappings.Single().TagId).IsEqualTo(tag.Id);
    }

    [Test]
    public async Task LostLabelResponse_LaterRemoval_IsNotOverwritten()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var tag = (await new TagCommands(host.Store, host.Ids, host.Clock).CreateAsync(new(DataHost.Op(), "bug", null, null))).Tag;
        api.PutLabels("person/repo", [new(11, "bug", "abcdef")]);
        await host.Store.ExecuteAsync(async (s, ct) => { await s.Tags.AddTaskLinkAsync(link.TaskId!, tag.Id, ct); return true; });
        api.AfterSetWrite = () => throw new GitHubApiException(GitHubErrorKind.Network);
        var outbound = new GitHubOutboundCommands(host.Store, api, inbound, host.Clock);
        await Assert.That(async () => await outbound.PushAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
        await host.Store.ExecuteAsync(async (s, ct) => { await s.Tags.RemoveTaskLinkAsync(link.TaskId!, tag.Id, ct); return true; });
        api.AfterSetWrite = null;
        await outbound.RecoverAsync(repo.Id, CancellationToken.None);
        await outbound.PushAsync(repo.Id, CancellationToken.None);
        var remote = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        await Assert.That(remote.Labels.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LostAssignmentResponse_LaterUnassign_IsNotOverwritten()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        await commands.SetSelfAssignmentAsync(new(DataHost.Op(), link.TaskId!, true));
        api.AfterSetWrite = () => throw new GitHubApiException(GitHubErrorKind.Network);
        var outbound = new GitHubOutboundCommands(host.Store, api, inbound, host.Clock);
        await Assert.That(async () => await outbound.PushAsync(repo.Id, CancellationToken.None)).Throws<GitHubApiException>();
        await commands.SetSelfAssignmentAsync(new(DataHost.Op(), link.TaskId!, false));
        api.AfterSetWrite = null;
        await outbound.RecoverAsync(repo.Id, CancellationToken.None);
        await outbound.PushAsync(repo.Id, CancellationToken.None);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Assignees.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OtherAuthorsComment_CannotBeEditedOrDeletedOnGitHub()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var comment = new GitHubComment(33, "Someone else's comment", new(2, "other"), DataHost.Start, DataHost.Start, "https://github.com/person/repo/issues/1#issuecomment-33");
        await new GitHubInboundComments(host.Store, host.Ids, host.Clock).ApplyAsync(link.Id, link.Version, [comment], false);
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.ListAsync(link.TaskId!, false)).Single();
        await Assert.That(async () => await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Changed"))).Throws<AppException>();
        await Assert.That(async () => await diary.DeleteAsync(new(DataHost.Op(), entry.Id, entry.Version, true))).Throws<AppException>();
        await diary.DeleteAsync(new(DataHost.Op(), entry.Id, entry.Version));
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single().State).IsEqualTo(5);
    }

    [Test]
    public async Task LostCommentPostResponse_RecoversWithoutPostingDuplicate()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "First note", null, true))).Entry;
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        api.AfterCommentPost = () => throw new GitHubApiException(GitHubErrorKind.Network);
        await Assert.That(async () => await outbound.RunAsync(repo.Id, true, CancellationToken.None)).Throws<GitHubApiException>();
        await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Later note"));
        api.AfterCommentPost = null;
        await outbound.RunAsync(repo.Id, false, CancellationToken.None);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var comments = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(comments.Items.Count).IsEqualTo(1);
        var mapping = (await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single();
        await Assert.That(mapping.CommentId).IsEqualTo((long?)comments.Items[0].Id);
        await Assert.That(mapping.State).IsEqualTo(0);
        await Assert.That(GitHubCommentMarker.Strip(comments.Items[0].Body, entry.Id)).IsEqualTo("Later note");
        await Assert.That((await diary.ListAsync(link.TaskId!, false)).Single().RawMarkdown).IsEqualTo("Later note");
    }

    [Test]
    public async Task LostCommentEditResponse_LaterEditIsSentWithoutConflict()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "First", null, true))).Entry;
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var edited = (await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Second"))).Entry;
        api.AfterCommentEdit = () => throw new GitHubApiException(GitHubErrorKind.Network);
        await Assert.That(async () => await outbound.RunAsync(repo.Id, true, CancellationToken.None)).Throws<GitHubApiException>();
        await diary.EditAsync(new(DataHost.Op(), entry.Id, edited.Version, "Third"));
        api.AfterCommentEdit = null;
        await outbound.RunAsync(repo.Id, false, CancellationToken.None);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var comments = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(comments.Items.Count).IsEqualTo(1);
        await Assert.That(GitHubCommentMarker.Strip(comments.Items[0].Body, entry.Id)).IsEqualTo("Third");
        var mapping = (await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single();
        await Assert.That(mapping.State).IsEqualTo(0);
    }

    [Test]
    public async Task CommentEdit_RemoteChanged_UsesRemoteAndKeepsLocalRevision()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "First", null, true))).Entry;
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Local edit"));
        var comment = (await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None)).Items.Single();
        await api.EditCommentAsync(api.Account, "person", "repo", comment.Id, GitHubCommentMarker.Body("Remote edit", entry.Id), CancellationToken.None);
        var writes = api.RemoteWrites;
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        await Assert.That(api.RemoteWrites).IsEqualTo(writes);
        await Assert.That((await diary.ListAsync(link.TaskId!, false)).Single().RawMarkdown).IsEqualTo("Remote edit");
        await Assert.That((await diary.RevisionsAsync(entry.Id)).Any(r => r.RawMarkdown == "Local edit")).IsTrue();
    }

    [Test]
    public async Task DiaryDelete_DefaultHidesLocally_ExplicitDeleteRecoversLostResponse()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var first = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "Keep on GitHub", null, true))).Entry;
        var second = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "Delete remotely", null, true))).Entry;
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        await diary.DeleteAsync(new(DataHost.Op(), first.Id, first.Version));
        await diary.DeleteAsync(new(DataHost.Op(), second.Id, second.Version, true));
        api.AfterCommentDelete = () => throw new GitHubApiException(GitHubErrorKind.Network);
        await Assert.That(async () => await outbound.RunAsync(repo.Id, true, CancellationToken.None)).Throws<GitHubApiException>();
        api.AfterCommentDelete = null;
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var comments = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(comments.Items.Count).IsEqualTo(1);
        await Assert.That(GitHubCommentMarker.Strip(comments.Items[0].Body, first.Id)).IsEqualTo("Keep on GitHub");
        var mappings = await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct));
        await Assert.That(mappings.Single(m => m.DiaryEntryId == first.Id).State).IsEqualTo(5);
        await Assert.That(mappings.Single(m => m.DiaryEntryId == second.Id).State).IsEqualTo(4);
    }

    [Test]
    public async Task RestoreBeforePendingDelete_DoesNotDeleteRemoteComment()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, _) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "Keep this", null, true))).Entry;
        var outbound = new GitHubOutboundComments(host.Store, api, host.Ids, host.Clock);
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var deleted = (await diary.DeleteAsync(new(DataHost.Op(), entry.Id, entry.Version, true))).Entry;
        await diary.RestoreAsync(new(DataHost.Op(), entry.Id, deleted.Version));
        await outbound.RunAsync(repo.Id, true, CancellationToken.None);
        var remote = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(remote.Items.Count).IsEqualTo(1);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.ListCommentLinksAsync(link.Id, ct))).Single().State).IsEqualTo(0);
    }

    [Test]
    public async Task SyncNow_DiaryPostEditAndDelete_RoundTrip()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), link.TaskId!, "First", null, true))).Entry;
        using var issues = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("backup.db"), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        var sync = new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock);
        await sync.RunAsync(repo.Id, CancellationToken.None);
        var remote = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(GitHubCommentMarker.Strip(remote.Items.Single().Body, entry.Id)).IsEqualTo("First");
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsFalse();
        var edited = (await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Second"))).Entry;
        await sync.RunAsync(repo.Id, CancellationToken.None);
        remote = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(GitHubCommentMarker.Strip(remote.Items.Single().Body, entry.Id)).IsEqualTo("Second");
        await diary.DeleteAsync(new(DataHost.Op(), entry.Id, edited.Version, true));
        await sync.RunAsync(repo.Id, CancellationToken.None);
        remote = await api.ListCommentsAsync(api.Account, "person", "repo", null, 1, null, null, CancellationToken.None);
        await Assert.That(remote.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Push_ForbiddenIssue_DoesNotPreventAnotherIssueFromSyncing()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var first = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var original = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        var secondIssue = original with { Id = 2, NodeId = "I2", Number = 2, HtmlUrl = "https://github.com/person/repo/issues/2" };
        api.PutIssue("person/repo", secondIssue);
        await inbound.ApplyAsync(repo.Id, repo.Version, [secondIssue]);
        var second = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 2, ct)))!;
        await SetTitle(host, first.Id, "Blocked");
        await SetTitle(host, second.Id, "Allowed");
        api.IssueUpdateFailures[1] = GitHubErrorKind.Forbidden;
        await new GitHubOutboundCommands(host.Store, api, inbound, host.Clock).PushAsync(repo.Id, CancellationToken.None);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 2, CancellationToken.None)).Title).IsEqualTo("Allowed");
        var blocked = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(first.Id, ct)))!;
        await Assert.That(blocked.LastErrorKind).IsEqualTo((int?)GitHubErrorKind.Forbidden);
        await Assert.That(blocked.NextAttemptUtc).IsNotNull();
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueWriteAsync(first.Id, ct))).IsNotNull();
    }

    [Test]
    public async Task SyncNow_ForbiddenIssueRead_ContinuesWithOtherIssue()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var first = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var original = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        var other = original with { Id = 2, NodeId = "I2", Number = 2, HtmlUrl = "https://github.com/person/repo/issues/2" };
        api.PutIssue("person/repo", other);
        await inbound.ApplyAsync(repo.Id, repo.Version, [other]);
        var second = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 2, ct)))!;
        await SetTitle(host, first.Id, "Blocked");
        await SetTitle(host, second.Id, "Allowed");
        api.IssueFailures[("person/repo", 1)] = GitHubErrorKind.Forbidden;
        using var issues = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("backup.db"), host.Clock);
        using var comments = new GitHubCommentFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
        await new GitHubSyncCommands(host.Store, api, issues, comments, host.Ids, host.Clock).RunAsync(repo.Id, CancellationToken.None);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 2, CancellationToken.None)).Title).IsEqualTo("Allowed");
        var blocked = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(first.Id, ct)))!;
        await Assert.That(blocked.LastErrorKind).IsEqualTo((int?)GitHubErrorKind.Forbidden);
    }

    [Test]
    public async Task IssueFeed_InvalidLinkedIssue_DoesNotRollBackOtherIssues()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var (repo, connection, inbound) = await Setup(host, api);
        var first = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var original = await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None);
        var other = original with { Id = 2, NodeId = "I2", Number = 2, HtmlUrl = "https://github.com/person/repo/issues/2" };
        api.PutIssue("person/repo", other);
        await inbound.ApplyAsync(repo.Id, repo.Version, [other]);
        api.PutIssue("person/repo", original with { Title = new string('x', 257), UpdatedAt = DataHost.Start.AddMinutes(1) });
        api.PutIssue("person/repo", other with { Title = "Valid remote change", UpdatedAt = DataHost.Start.AddMinutes(1) });
        using var fetcher = new GitHubIssueFetcher(host.Store, api, inbound, _ => Task.FromResult("backup.db"), host.Clock);
        await fetcher.PullAsync(repo.Id);
        var blocked = (await host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(first.Id, ct)))!;
        var second = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 2, ct)))!;
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(second.TaskId!, ct)))!;
        await Assert.That(blocked.LastErrorKind).IsEqualTo((int?)GitHubErrorKind.Validation);
        await Assert.That(task.Title).IsEqualTo("Valid remote change");
    }
}
