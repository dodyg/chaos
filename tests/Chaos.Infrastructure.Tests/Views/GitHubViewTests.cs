using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tags;
using Chaos.Application.Views;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Tests.Views;

/// <summary>
/// The GitHub field group of custom views (PLAN2 D7) against real SQLite: repository, issue state,
/// label, milestone and assignee conditions read the stored issue baseline and mappings, and a task
/// that is not from GitHub never satisfies a positive GitHub condition.
/// </summary>
public sealed class GitHubViewTests
{
    private sealed record Seeded(string RepoA, string RepoB, string BugTask, string ClosedTask, string FeatureTask, string PlainTask);

    private static string Payload(bool open, long[] labels, long? milestone, string[] logins) =>
        "{\"v\":1,\"payload\":{\"title\":\"x\",\"body\":\"\",\"open\":" + (open ? "true" : "false") + ",\"reason\":null,\"labelIds\":[" + string.Join(",", labels)
        + "],\"assigneeIds\":[" + string.Join(",", logins.Select((_, index) => 100 + index)) + "],\"milestoneNumber\":" + (milestone?.ToString() ?? "null") + ",\"milestoneDue\":null,\"comments\":0,\"assigneeLogins\":["
        + string.Join(",", logins.Select(login => "\"" + login + "\"")) + "]}}";

    private static async Task<Seeded> SeedAsync(DataHost host)
    {
        var containers = new ContainerCommands(host.Store, host.Ids, host.Clock);
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        var project = (await containers.CreateAsync(new(DataHost.Op(), "Release 1", ContainerKind.Project, null))).Container;
        var tagId = (await tags.CreateAsync(new(DataHost.Op(), "gh-bug", null, null))).Tag.Id;
        var bug = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Crash on save", null))).Task;
        var closed = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Old issue", null))).Task;
        var feature = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Dark mode", null))).Task;
        var plain = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Buy milk", null))).Task;
        var now = DataHost.Start;
        var connectionId = Guid.NewGuid().ToString();
        var repoA = Guid.NewGuid().ToString();
        var repoB = Guid.NewGuid().ToString();
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            var profile = (await session.Profiles.GetPrimaryAsync(ct))!;
            var space = (await session.Spaces.GetPersonalByOwnerAsync(profile.Id, ct))!;
            var inbox = (await session.Containers.ListBySpaceAsync(space.Id, ct)).First(container => container.Kind == ContainerKind.Inbox);
            await session.GitHub.AddConnectionAsync(new GitHubConnectionRecord
            {
                Id = connectionId, SpaceId = space.Id, ProfileId = profile.Id, WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com",
                AccountId = 7, AccountLogin = "person", CreatedAtUtc = now, UpdatedAtUtc = now,
            }, ct);
            foreach (var (id, number, name) in new[] { (repoA, 1L, "person/alpha"), (repoB, 2L, "person/beta") })
            {
                await session.GitHub.AddRepositoryAsync(new GitHubRepositoryRecord
                {
                    Id = id, ConnectionId = connectionId, RepositoryId = number, Owner = "person", Name = name[7..], FullName = name,
                    HtmlUrl = "https://github.com/" + name, TargetContainerId = inbox.Id, FilterJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now,
                }, ct);
            }

            await session.GitHub.UpsertLabelTagAsync(new GitHubLabelTagRecord { RepositorySubscriptionId = repoA, LabelId = 11, LabelName = "Bug", LabelColor = "d73a4a", TagId = tagId }, ct);
            await session.GitHub.UpsertLabelTagAsync(new GitHubLabelTagRecord { RepositorySubscriptionId = repoA, LabelId = 12, LabelName = "enhancement", LabelColor = "a2eeef", TagId = tagId }, ct);
            await session.GitHub.UpsertMilestoneProjectAsync(new GitHubMilestoneProjectRecord { RepositorySubscriptionId = repoA, MilestoneNumber = 3, MilestoneId = 30, ContainerId = project.Id, BaseTitle = "Release 1" }, ct);

            async Task Link(string repo, long issue, string taskId, string json) =>
                await session.GitHub.AddIssueLinkAsync(new GitHubIssueLinkRecord
                {
                    Id = Guid.NewGuid().ToString(), ConnectionId = connectionId, RepositorySubscriptionId = repo, TaskId = taskId, IssueId = issue, IssueNodeId = "I" + issue,
                    IssueNumber = issue, HtmlUrl = "https://github.com/x/y/issues/" + issue, BaseJson = json, BaseRemoteUpdatedUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
                }, ct);
            await Link(repoA, 1, bug.Id, Payload(open: true, [11], 3, ["octocat"]));
            await Link(repoA, 2, closed.Id, Payload(open: false, [11], null, []));
            await Link(repoB, 3, feature.Id, Payload(open: true, [], null, ["hubot"]));
            return true;
        });
        return new Seeded(repoA, repoB, bug.Id, closed.Id, feature.Id, plain.Id);
    }

    private static async Task<IReadOnlyList<string>> IdsAsync(DataHost host, string items)
    {
        var queries = new TaskQueries(host.Store, host.Clock);
        var spec = new TaskQuerySpec { View = TaskView.Custom, Definition = ViewDefinition.Parse("{\"v\":1,\"match\":\"all\",\"items\":[" + items + "]}") };
        var page = await queries.RunAsync(spec);
        if (page.Rows.Count != await queries.CountAsync(spec))
        {
            throw new InvalidOperationException("Rows and count disagree.");
        }

        return [.. page.Rows.Select(row => row.Task.Id).Order(StringComparer.Ordinal)];
    }

    private static string Sorted(params string[] ids) => string.Join(",", ids.Order(StringComparer.Ordinal));

    [Test]
    public async Task SharedView_OffersGitHubRepositoriesAndScopesGitHubConditions()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc)
                    SELECT 'team',Id,2,'Team','2026-11-10T09:00:00Z','2026-11-10T09:00:00Z' FROM Profiles LIMIT 1;
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc)
                    SELECT 'member','team',Id,1,'Me',1,'now' FROM Profiles LIMIT 1;
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc)
                    VALUES ('team-list','team',1,'Team','team',0,'2026-11-10T09:00:00Z','2026-11-10T09:00:00Z');
                UPDATE Tasks SET SpaceId='team',ContainerId='team-list' WHERE Id=$task;
                UPDATE GitHubConnections SET SpaceId='team' WHERE Id=(SELECT ConnectionId FROM GitHubRepositories WHERE Id=$repo);
                """;
            command.Parameters.AddWithValue("$task", seed.BugTask);
            command.Parameters.AddWithValue("$repo", seed.RepoA);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });
        var commands = new SavedViewCommands(host.Store, host.Ids, host.Clock);
        const string definition = "{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Open\"}]}";
        var personal = (await commands.SaveAsync(new(DataHost.Op(), null, 0, "Open issues", "github", null, definition, null, true, false))).View;
        var shared = (await commands.ShareAsync(DataHost.Op(), personal.Id, "team")).View;
        var queries = new SavedViewQueries(host.Store, new TaskQueries(host.Store, host.Clock), host.Clock);
        await Assert.That((await queries.GitHubRepositoriesAsync()).Select(repo => repo.Id)).Contains(seed.RepoA);
        var rows = await queries.RunAsync(shared.Id, null, 100, null);
        await Assert.That(rows.Page.Rows.Select(row => row.Task.Id)).IsEquivalentTo([seed.BugTask]);
    }

    [Test]
    public async Task Repository_IsAndIsNot_UseTheSubscription_AndIsNotKeepsNonGitHubTasks()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);

        var inA = await IdsAsync(host, "{\"field\":\"githubRepository\",\"op\":\"is\",\"value\":\"" + seed.RepoA + "\"}");
        var notA = await IdsAsync(host, "{\"field\":\"githubRepository\",\"op\":\"isNot\",\"value\":\"" + seed.RepoA + "\"}");
        var either = await IdsAsync(host, "{\"field\":\"githubRepository\",\"op\":\"in\",\"value\":[\"" + seed.RepoA + "\",\"" + seed.RepoB + "\"]}");

        await Assert.That(string.Join(",", inA)).IsEqualTo(Sorted(seed.BugTask, seed.ClosedTask));
        await Assert.That(string.Join(",", notA)).IsEqualTo(Sorted(seed.FeatureTask, seed.PlainTask));
        await Assert.That(string.Join(",", either)).IsEqualTo(Sorted(seed.BugTask, seed.ClosedTask, seed.FeatureTask));
    }

    [Test]
    public async Task State_OpenAndClosed_ReadTheStoredBaseline()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);

        var open = await IdsAsync(host, "{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Open\"}");
        var closed = await IdsAsync(host, "{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Closed\"}");

        await Assert.That(string.Join(",", open)).IsEqualTo(Sorted(seed.BugTask, seed.FeatureTask));
        await Assert.That(string.Join(",", closed)).IsEqualTo(Sorted(seed.ClosedTask));
    }

    [Test]
    public async Task Label_ContainsIsCaseInsensitive_AndIsEmptyNeedsAGitHubTask()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);

        var bugs = await IdsAsync(host, "{\"field\":\"githubLabel\",\"op\":\"contains\",\"value\":\"BUG\"}");
        var none = await IdsAsync(host, "{\"field\":\"githubLabel\",\"op\":\"isEmpty\"}");
        var miss = await IdsAsync(host, "{\"field\":\"githubLabel\",\"op\":\"contains\",\"value\":\"question\"}");

        await Assert.That(string.Join(",", bugs)).IsEqualTo(Sorted(seed.BugTask, seed.ClosedTask));
        await Assert.That(string.Join(",", none)).IsEqualTo(Sorted(seed.FeatureTask));
        await Assert.That(miss.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Milestone_TitleMatchesMappedMilestones_AndIsEmptyWorksForEveryIssue()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);

        var release = await IdsAsync(host, "{\"field\":\"githubMilestone\",\"op\":\"contains\",\"value\":\"release\"}");
        var none = await IdsAsync(host, "{\"field\":\"githubMilestone\",\"op\":\"isEmpty\"}");

        await Assert.That(string.Join(",", release)).IsEqualTo(Sorted(seed.BugTask));
        await Assert.That(string.Join(",", none)).IsEqualTo(Sorted(seed.ClosedTask, seed.FeatureTask));
    }

    [Test]
    public async Task Assignee_ContainsAndIsEmpty_ReadTheBaseline()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);

        var octo = await IdsAsync(host, "{\"field\":\"githubAssignee\",\"op\":\"contains\",\"value\":\"OCTO\"}");
        var unassigned = await IdsAsync(host, "{\"field\":\"githubAssignee\",\"op\":\"isEmpty\"}");

        await Assert.That(string.Join(",", octo)).IsEqualTo(Sorted(seed.BugTask));
        await Assert.That(string.Join(",", unassigned)).IsEqualTo(Sorted(seed.ClosedTask));
    }

    [Test]
    public async Task OpenBugsInOneRepository_IsAMyOpenGitHubBugsStyleView()
    {
        using var host = await DataHost.StartAsync();
        var seed = await SeedAsync(host);
        var views = new SavedViewCommands(host.Store, host.Ids, host.Clock);
        var definition = "{\"v\":1,\"match\":\"all\",\"items\":["
            + "{\"field\":\"githubRepository\",\"op\":\"is\",\"value\":\"" + seed.RepoA + "\"},"
            + "{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Open\"},"
            + "{\"field\":\"githubLabel\",\"op\":\"contains\",\"value\":\"bug\"}]}";

        var view = (await views.SaveAsync(new(DataHost.Op(), null, 0, "My open GitHub bugs", "bug", "red", definition, null, Pinned: true, AlertsEnabled: false))).View;
        var queries = new SavedViewQueries(host.Store, new TaskQueries(host.Store, host.Clock), host.Clock);
        var run = await queries.RunAsync(view.Id, null, 100, null);

        await Assert.That(run.Page.TotalCount).IsEqualTo(1L);
        await Assert.That(run.Page.Rows[0].Task.Id).IsEqualTo(seed.BugTask);
        await Assert.That((await queries.CountsAsync(null))[view.Id]).IsEqualTo(1L);
    }

    [Test]
    public async Task GitHubConditions_OnAnUnsyncedBaseline_DoNotMatchOrFail()
    {
        using var host = await DataHost.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Linked, baseline unreadable", null))).Task;
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            var profile = (await session.Profiles.GetPrimaryAsync(ct))!;
            var space = (await session.Spaces.GetPersonalByOwnerAsync(profile.Id, ct))!;
            var inbox = (await session.Containers.ListBySpaceAsync(space.Id, ct)).First(container => container.Kind == ContainerKind.Inbox);
            var now = DataHost.Start;
            await session.GitHub.AddConnectionAsync(new GitHubConnectionRecord { Id = "c", SpaceId = space.Id, ProfileId = profile.Id, WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com", AccountId = 7, AccountLogin = "p", CreatedAtUtc = now, UpdatedAtUtc = now }, ct);
            await session.GitHub.AddRepositoryAsync(new GitHubRepositoryRecord { Id = "r", ConnectionId = "c", RepositoryId = 9, Owner = "p", Name = "r", FullName = "p/r", HtmlUrl = "https://github.com/p/r", TargetContainerId = inbox.Id, FilterJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now }, ct);
            await session.GitHub.AddIssueLinkAsync(new GitHubIssueLinkRecord { Id = "l", ConnectionId = "c", RepositorySubscriptionId = "r", TaskId = task.Id, IssueId = 1, IssueNodeId = "I1", IssueNumber = 1, HtmlUrl = "https://github.com/p/r/issues/1", BaseJson = "{}", BaseRemoteUpdatedUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now }, ct);
            return true;
        });

        await Assert.That((await IdsAsync(host, "{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Open\"}")).Count).IsEqualTo(0);
        await Assert.That((await IdsAsync(host, "{\"field\":\"githubLabel\",\"op\":\"contains\",\"value\":\"bug\"}")).Count).IsEqualTo(0);
        await Assert.That((await IdsAsync(host, "{\"field\":\"githubAssignee\",\"op\":\"contains\",\"value\":\"x\"}")).Count).IsEqualTo(0);
        await Assert.That((await IdsAsync(host, "{\"field\":\"githubRepository\",\"op\":\"is\",\"value\":\"r\"}")).Count).IsEqualTo(1);
    }
}
