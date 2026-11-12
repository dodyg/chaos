using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubG5AcceptanceTests
{
    private static readonly GitHubLabel Label = new(11, "area/bug", "aaaaaa");
    private static readonly GitHubMilestone Milestone = new(31, 1, "Release", "open", DataHost.Start.AddDays(10));
    private static GitHubRepository RemoteRepo(string owner = "person", string name = "repo", long id = 10) => new(id, "R" + id, name, owner + "/" + name, new(1, owner), $"https://github.com/{owner}/{name}", false, true);
    private static GitHubIssue Issue(long id = 100, long number = 1, string owner = "person", string repo = "repo") => new(id, "I" + id, number, $"https://github.com/{owner}/{repo}/issues/{number}", "Remote title", "- [ ] First\r\nText", "open", null,
        new(1, "person"), [Label], [], Milestone, null, DataHost.Start, DataHost.Start, null, 0, null);
    private static async Task<(GitHubConnectionCommands Commands, GitHubConnectionRecord Connection, GitHubRepositoryRecord Repo, GitHubIssueLinkRecord Link)> Setup(DataHost host, FakeGitHub api, bool projects = false)
    {
        api.PutRepository(RemoteRepo()); api.PutLabels("person/repo", [Label]); api.PutMilestones("person/repo", [Milestone]); api.PutIssue("person/repo", Issue());
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, projects));
        await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [Issue()]);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 100, ct)))!;
        return (commands, connection, repo, link);
    }
    private static GitHubInboundRefresh Refresh(DataHost host, FakeGitHub api) => new(host.Store, api, new(host.Store, host.Ids, host.Clock), host.Clock);
    private static Task<GitHubIssueLinkRecord?> Link(DataHost host, string id) => host.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(id, ct));
    private static async Task<string> Container(DataHost host, string space, string name) => await host.Store.ExecuteAsync(async (s, ct) =>
    {
        var id = host.Ids.NewId().ToString();
        await s.Containers.AddAsync(new ContainerRecord { Id = id, SpaceId = space, Name = name, Kind = ContainerKind.Project, Position = await s.Containers.GetNextPositionAsync(space, ct), Version = 1, CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start }, ct);
        return id;
    });

    private static async Task<GitHubRepositoryRecord> Subscription(DataHost host, FakeGitHub api, GitHubFilter filter)
    {
        api.PutRepository(RemoteRepo());
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var c = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(c.SpaceId, ct))!.Id);
        return await commands.AddRepositoryAsync(new(DataHost.Op(), c.Id, c.Version, "person", "repo", 10, inbox, null, filter, false));
    }

    [Test]
    public async Task FirstImportPrioritizesOpenIssues_AndDoesNotReplayHistoricalClosedIssues()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub();
        var repo = await Subscription(host, api, GitHubFilter.Empty);
        for (var n = 1; n <= 500; n++) api.PutIssue("person/repo", Issue(100 + n, n) with { State = "closed", UpdatedAt = DataHost.Start.AddDays(-100), ClosedAt = DataHost.Start.AddDays(-100) });
        api.PutIssue("person/repo", Issue(1000, 501) with { Title = "Open issue", UpdatedAt = DataHost.Start.AddDays(-2) });
        using var fetcher = new GitHubIssueFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), _ => Task.FromResult("before.db"), host.Clock);
        var first = await fetcher.PullAsync(repo.Id, 1);
        await Assert.That(first.Created).IsEqualTo(1);
        await Assert.That(first.MorePages).IsFalse();
        await Assert.That(api.IssueListStates.Single()).IsEqualTo("open");
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetRepositoryAsync(repo.Id, ct)))!.IssuesCursorUtc).IsEqualTo(DataHost.Start);
        await Assert.That((await fetcher.PullAsync(repo.Id, 1)).Created).IsEqualTo(0);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(1L);
    }

    [Test]
    public async Task RecentClosedImportResumesItsOwnPhaseAfterRestart()
    {
        var api = new FakeGitHub(); string root; GitHubRepositoryRecord repo;
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root; repo = await Subscription(host, api, new([], IncludeClosedDays: 7));
            api.PutIssue("person/repo", Issue(100, 1));
            api.PutIssue("person/repo", Issue(101, 2) with { State = "closed", UpdatedAt = DataHost.Start.AddDays(-1), ClosedAt = DataHost.Start.AddDays(-1) });
            api.PutIssue("person/repo", Issue(102, 3) with { State = "closed", UpdatedAt = DataHost.Start.AddDays(-100), ClosedAt = DataHost.Start.AddDays(-100) });
            using var fetcher = new GitHubIssueFetcher(host.Store, api, new(host.Store, host.Ids, host.Clock), _ => Task.FromResult("before.db"), host.Clock);
            var first = await fetcher.PullAsync(repo.Id, 1);
            await Assert.That(first.Created).IsEqualTo(1); await Assert.That(first.MorePages).IsTrue();
            await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.GetRepositoryAsync(repo.Id, ct)))!.InitialImportDoneUtc).IsNull();
        }
        using var restarted = await DataHost.StartAsync(root);
        using var next = new GitHubIssueFetcher(restarted.Store, api, new(restarted.Store, restarted.Ids, restarted.Clock), _ => Task.FromResult("unused.db"), restarted.Clock);
        var closed = await next.PullAsync(repo.Id, 1);
        await Assert.That(closed.Created).IsEqualTo(1); await Assert.That(closed.MorePages).IsFalse();
        await Assert.That(string.Join(',', api.IssueListStates)).IsEqualTo("open,closed");
        await Assert.That(await restarted.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(2L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransferOutsideDestinationFilter_KeepsIdentityThenResumesManagedPlacement(bool sourceOutOfScope)
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (commands, connection, repo, link) = await Setup(host, api);
        if (sourceOutOfScope)
        {
            repo = await commands.UpdateRepositoryAsync(new(DataHost.Op(), repo.Id, repo.Version, repo.TargetContainerId, null, new(["missing"]), false));
            await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [Issue()]);
        }
        var destination = await Container(host, connection.SpaceId, "Destination");
        api.PutRepository(RemoteRepo("other", "next", 20));
        var target = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "other", "next", 20, destination, null, new(["missing"]), false));
        var moved = Issue(number: 9, owner: "other", repo: "next") with { Milestone = new(99, 1, "Target release", "open", DataHost.Start.AddDays(20)) };
        api.RemoveIssue("person/repo", 1); api.PutIssue("other/next", moved); api.RedirectedIssues[("person/repo", 1)] = moved;
        var refresh = Refresh(host, api); await refresh.PullAsync(repo.Id);
        var outOfScope = (await Link(host, link.Id))!;
        await Assert.That(outOfScope.RepositorySubscriptionId).IsEqualTo(target.Id);
        await Assert.That(outOfScope.State).IsEqualTo(1);
        await Assert.That(outOfScope.TaskId).IsEqualTo(link.TaskId);
        target = await commands.UpdateRepositoryAsync(new(DataHost.Op(), target.Id, target.Version, destination, null, GitHubFilter.Empty, false));
        await refresh.PullAsync(target.Id);
        var resumed = (await Link(host, link.Id))!;
        await Assert.That(resumed.State).IsEqualTo(0);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!.ContainerId).IsEqualTo(destination);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(1L);
    }

    [Test]
    public async Task CappedCatalogAndIssueChecksResumeAfterRestart_AndUsePerPageEtags()
    {
        var api = new FakeGitHub(); string root; GitHubRepositoryRecord repo;
        using (var host = await DataHost.StartAsync())
        {
            root = host.Root; (_, _, repo, _) = await Setup(host, api);
            api.PutLabels("person/repo", [Label, new(12, "b", "aaaaaa"), new(13, "c", "aaaaaa"), new(14, "d", "aaaaaa"), new(15, "e", "aaaaaa")]);
            var extra = Enumerable.Range(2, 4).Select(n => Issue(100 + n, n)).ToArray();
            foreach (var issue in extra) api.PutIssue("person/repo", issue);
            await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, extra);
            await Assert.That((await Refresh(host, api).PullAsync(repo.Id, 1, 2)).MorePages).IsTrue();
        }
        using var restarted = await DataHost.StartAsync(root); var refresh = Refresh(restarted, api);
        var complete = false;
        for (var pass = 0; pass < 10; pass++) if (!(await refresh.PullAsync(repo.Id, 1, 2)).MorePages) { complete = true; break; }
        await Assert.That(complete).IsTrue();
        var before = api.NotModifiedCount;
        await refresh.PullAsync(repo.Id, 20, 20);
        await Assert.That(api.NotModifiedCount > before).IsTrue();
        await Assert.That(await restarted.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(5L);
    }

    [Test]
    public async Task CatalogChangesWithoutIssueTimestampChanges_RefreshMetadataAndConverge()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (_, connection, repo, link) = await Setup(host, api, true);
        api.PutLabels("person/repo", [Label with { Name = "area/fixed", Color = "bbbbbb" }]);
        api.PutMilestones("person/repo", [Milestone with { Title = "Ship", DueOn = DataHost.Start.AddDays(12) }]);
        var refresh = Refresh(host, api); await refresh.PullAsync(repo.Id);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var mapping = (await s.GitHub.ListLabelTagsAsync(repo.Id, ct)).Single();
            await Assert.That(mapping.LabelName).IsEqualTo("area/fixed");
            await Assert.That((await s.Tags.GetAsync(mapping.TagId, ct))!.DisplayName).IsEqualTo("bug");
            var project = (await s.GitHub.ListMilestoneProjectsAsync(repo.Id, ct)).Single();
            await Assert.That((await s.Containers.GetAsync(project.ContainerId, ct))!.Name).IsEqualTo("Ship");
            await Assert.That((await s.Tasks.GetAsync(link.TaskId!, ct))!.DueDate).IsEqualTo(DateOnly.FromDateTime(DataHost.Start.AddDays(12).UtcDateTime));
            return true;
        });
        var history = await host.CountAsync("SELECT count(*) FROM TaskHistory;");
        var result = await refresh.PullAsync(repo.Id);
        await Assert.That(result.Updated).IsEqualTo(0);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM TaskHistory;")).IsEqualTo(history);
        await Assert.That(api.NotModifiedCount >= 3).IsTrue();
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
    }

    [Test]
    public async Task MilestoneNameClashAndUserOverrides_ArePreservedAcrossRefreshes()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (_, connection, repo, link) = await Setup(host, api, true);
        await Container(host, connection.SpaceId, "Ship");
        api.PutMilestones("person/repo", [Milestone with { Title = "Ship" }]);
        var refresh = Refresh(host, api); await refresh.PullAsync(repo.Id); await refresh.PullAsync(repo.Id);
        var mapping = (await host.Store.QueryAsync((s, ct) => s.GitHub.ListMilestoneProjectsAsync(repo.Id, ct))).Single();
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var project = (await s.Containers.GetAsync(mapping.ContainerId, ct))!;
            await Assert.That(project.Name).IsEqualTo("Ship (repo)");
            await s.Containers.UpdateAsync(project with { Name = "My project", DeadlineDate = new(2026, 12, 25), Version = project.Version + 1 }, project.Version, ct);
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await s.Tasks.UpdateAsync(task with { DueDate = new(2026, 12, 26), Priority = TaskPriority.Urgent, Version = task.Version + 1 }, task.Version, ct);
            return true;
        });
        api.PutMilestones("person/repo", [Milestone with { Title = "New title", DueOn = DataHost.Start.AddDays(20) }]);
        await refresh.PullAsync(repo.Id);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var project = (await s.Containers.GetAsync(mapping.ContainerId, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await Assert.That(project.Name).IsEqualTo("My project");
            await Assert.That(project.DeadlineDate).IsEqualTo(new DateOnly(2026, 12, 25));
            await Assert.That(task.DueDate).IsEqualTo(new DateOnly(2026, 12, 26));
            await Assert.That(task.Priority).IsEqualTo(TaskPriority.Urgent);
            return true;
        });
    }

    [Test]
    [Arguments(GitHubErrorKind.NotFound, 2)]
    [Arguments(GitHubErrorKind.Gone, 1)]
    public async Task MissingIssueBecomesUnavailable_ThenRecoversSameTask(GitHubErrorKind kind, int reads)
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (_, _, repo, link) = await Setup(host, api);
        api.IssueFailures[("person/repo", 1)] = kind;
        var refresh = Refresh(host, api); await refresh.PullAsync(repo.Id);
        await Assert.That(api.IssueReads[("person/repo", 1)]).IsEqualTo(reads);
        await Assert.That((await Link(host, link.Id))!.State).IsEqualTo(2);
        api.IssueFailures.Clear(); await refresh.PullAsync(repo.Id);
        var recovered = (await Link(host, link.Id))!;
        await Assert.That(recovered.State).IsEqualTo(0);
        await Assert.That(recovered.TaskId).IsEqualTo(link.TaskId);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(1L);
    }

    [Test]
    public async Task Transient404RequiresConfirmation_AndDoesNotHideTheTask()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (_, _, repo, link) = await Setup(host, api);
        api.IssueFailureSequences[("person/repo", 1)] = new([GitHubErrorKind.NotFound]);
        await Refresh(host, api).PullAsync(repo.Id);
        await Assert.That(api.IssueReads[("person/repo", 1)]).IsEqualTo(2);
        await Assert.That((await Link(host, link.Id))!.State).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransferToSubscribedRepository_PreservesTaskAndMappings_AndRespectsUserPlacement(bool userMoved)
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (commands, connection, repo, link) = await Setup(host, api);
        var destination = await Container(host, connection.SpaceId, "Destination");
        var personal = await Container(host, connection.SpaceId, "Personal");
        if (userMoved) await host.Store.ExecuteAsync(async (s, ct) => { var t = (await s.Tasks.GetAsync(link.TaskId!, ct))!; await s.Tasks.UpdateAsync(t with { ContainerId = personal, Version = t.Version + 1 }, t.Version, ct); return true; });
        var subtask = (await host.Store.QueryAsync((s, ct) => s.Subtasks.ListByTaskAsync(link.TaskId!, ct))).Single().Id;
        await new GitHubInboundComments(host.Store, host.Ids, host.Clock).ApplyAsync(link.Id, (await Link(host, link.Id))!.Version, [new(7, "Keep diary", new(1, "person"), DataHost.Start, DataHost.Start, "https://github.com/person/repo/issues/1#7")], false);
        api.PutRepository(RemoteRepo("other", "next", 20));
        var target = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "other", "next", 20, destination, null, GitHubFilter.Empty, false));
        var moved = Issue(number: 9, owner: "other", repo: "next") with { Labels = [new(21, "new-label", "cccccc")], Milestone = null };
        api.RemoveIssue("person/repo", 1); api.PutIssue("other/next", moved); api.RedirectedIssues[("person/repo", 1)] = moved;
        await Refresh(host, api).PullAsync(repo.Id);
        await Refresh(host, api).PullAsync(target.Id);
        var result = (await Link(host, link.Id))!;
        await Assert.That(result.RepositorySubscriptionId).IsEqualTo(target.Id);
        await Assert.That(result.IssueNumber).IsEqualTo(9L);
        await Assert.That(result.TaskId).IsEqualTo(link.TaskId);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            await Assert.That((await s.Tasks.GetAsync(link.TaskId!, ct))!.ContainerId).IsEqualTo(userMoved ? personal : destination);
            await Assert.That((await s.Subtasks.ListByTaskAsync(link.TaskId!, ct)).Single().Id).IsEqualTo(subtask);
            await Assert.That((await s.GitHub.ListCommentLinksAsync(link.Id, ct)).Count).IsEqualTo(1);
            return true;
        });
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(1L);
        await Assert.That(api.RemoteWrites).IsEqualTo(0);
    }

    [Test]
    public async Task TransferToUnsubscribedRepository_BecomesUnavailableWithNewUrl_AndRecoversWhenAdded()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (commands, connection, repo, link) = await Setup(host, api);
        api.PutRepository(RemoteRepo("other", "next", 20));
        var moved = Issue(number: 9, owner: "other", repo: "next");
        api.RemoveIssue("person/repo", 1); api.PutIssue("other/next", moved); api.RedirectedIssues[("person/repo", 1)] = moved;
        var refresh = Refresh(host, api); await refresh.PullAsync(repo.Id);
        var unavailable = (await Link(host, link.Id))!;
        await Assert.That(unavailable.State).IsEqualTo(2);
        await Assert.That(unavailable.HtmlUrl).IsEqualTo(moved.HtmlUrl);
        await Assert.That(unavailable.IssueNumber).IsEqualTo(1L);
        var target = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "other", "next", 20, repo.TargetContainerId, null, GitHubFilter.Empty, false));
        await refresh.PullAsync(repo.Id);
        await Assert.That((await Link(host, link.Id))!.RepositorySubscriptionId).IsEqualTo(target.Id);
        await Assert.That((await Link(host, link.Id))!.TaskId).IsEqualTo(link.TaskId);
    }

    [Test]
    public async Task RepositoryRenameKeepsStableIdentityAndWatermarks_AndRefreshesSourceUrls()
    {
        using var host = await DataHost.StartAsync(); var api = new FakeGitHub(); var (_, _, repo, link) = await Setup(host, api);
        await host.Store.ExecuteAsync(async (s, ct) => { await s.GitHub.CompleteIssueScanAsync(repo.Id, repo.Version, DataHost.Start, "old", DataHost.Start, ct); await s.GitHub.SaveCheckpointAsync(new(repo.Id, "initial-import-backup", repo.Version, "{\"fileName\":\"original.db\"}", DataHost.Start), ct); return true; });
        var renamed = RemoteRepo("new-owner", "new-name"); api.RedirectedRepositories["person/repo"] = renamed; api.PutRepository(renamed);
        api.RemoveIssue("person/repo", 1); api.PutIssue("new-owner/new-name", Issue(owner: "new-owner", repo: "new-name"));
        var refresh = Refresh(host, api); var current = await refresh.RefreshIdentityAsync(repo.Id, CancellationToken.None); await refresh.PullAsync(repo.Id);
        await Assert.That(current.RepositoryId).IsEqualTo(10L);
        await Assert.That(current.Version).IsEqualTo(repo.Version);
        await Assert.That(current.FullName).IsEqualTo("new-owner/new-name");
        await Assert.That(current.IssuesCursorUtc).IsEqualTo(DataHost.Start);
        await Assert.That((await Link(host, link.Id))!.HtmlUrl).IsEqualTo("https://github.com/new-owner/new-name/issues/1");
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "initial-import-backup", ct))).IsNotNull();
    }
}
