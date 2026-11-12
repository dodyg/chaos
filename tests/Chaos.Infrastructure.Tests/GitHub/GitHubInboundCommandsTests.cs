using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Domain.Tasks;
using Chaos.Domain.Recurrence;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubInboundCommandsTests
{
    [Test]
    public async Task UpdateDetails_ConnectedIssue_EnforcesTitleAndMilestoneDueLimits()
    {
        using var host = await DataHost.StartAsync();
        var (repo, connection, inbound) = await Setup(host);
        var issue = Issue() with { Milestone = new(22, 1, "Release", "open", DataHost.Start.AddDays(7)) };
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        var commands = new TaskCommands(host.Store, host.Ids, host.Clock);
        UpdateTaskDetailsCommand Edit(string title, DateOnly? due) => new(DataHost.Op(), task.Id, task.Version, title, task.Notes,
            task.PlannedDate, task.PlannedTime, task.PlannedZoneId, due, task.DueTime, task.DueZoneId, task.Priority, task.EstimateMinutes, task.CapacityCategory);
        await Assert.That(async () => await commands.UpdateDetailsAsync(Edit(new string('x', 257), task.DueDate))).Throws<AppException>();
        await Assert.That(async () => await commands.UpdateDetailsAsync(Edit(task.Title, task.DueDate!.Value.AddDays(1)))).Throws<AppException>();
        var saved = await commands.UpdateDetailsAsync(Edit(new string('x', 256), task.DueDate));
        await Assert.That(saved.Task.Title.Length).IsEqualTo(256);
        await Assert.That(saved.Task.DueDate).IsEqualTo(task.DueDate);
    }

    [Test]
    public async Task ConnectedTask_RejectsRecurrenceAndMultilineChecklistTitles()
    {
        using var host = await DataHost.StartAsync();
        var (repo, connection, inbound) = await Setup(host);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        var task = (await host.Store.QueryAsync((s, ct) => s.Tasks.GetAsync(link.TaskId!, ct)))!;
        var subtasks = new SubtaskCommands(host.Store, host.Ids, host.Clock);
        var existing = (await host.Store.QueryAsync((s, ct) => s.Subtasks.ListByTaskAsync(task.Id, ct)))[0];
        await Assert.That(async () => await subtasks.AddAsync(new(DataHost.Op(), task.Id, "first\nsecond", null))).Throws<AppException>();
        await Assert.That(async () => await subtasks.UpdateAsync(new(DataHost.Op(), existing.Id, existing.Version, "first\rsecond", null))).Throws<AppException>();
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null, "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        await Assert.That(async () => await new RecurrenceCommands(host.Store, host.Ids, host.Clock).StartAsync(new(DataHost.Op(), task.Id, task.Version, rule))).Throws<AppException>();
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.Recurrence.FindByTaskAsync(task.Id, ct))).IsNull();
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.Subtasks.ListByTaskAsync(task.Id, ct))).Count).IsEqualTo(2);
    }

    [Test]
    public async Task DetachedTask_AllowsRecurrenceAndMultilineSubtasks()
    {
        using var host = await DataHost.StartAsync();
        var (repo, connection, inbound) = await Setup(host);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        var task = await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            await s.GitHub.UpdateIssueLinkAsync(link with { State = 4 }, link.Version, ct);
            return (await s.Tasks.GetAsync(link.TaskId!, ct))!;
        });
        var subtask = await new SubtaskCommands(host.Store, host.Ids, host.Clock).AddAsync(new(DataHost.Op(), task.Id, "first\nsecond", null));
        await Assert.That(subtask.Subtask.Title).IsEqualTo("first\nsecond");
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null, "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        await new RecurrenceCommands(host.Store, host.Ids, host.Clock).StartAsync(new(DataHost.Op(), task.Id, task.Version, rule));
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.Recurrence.FindByTaskAsync(task.Id, ct))).IsNotNull();
    }

    private static GitHubIssue Issue(long id = 1, string title = "Remote title", string body = "Text\r\n- [ ] First\r\n- [x] Second", string state = "open", DateTimeOffset? updated = null) =>
        new(id, "I" + id, id, "https://github.com/person/repo/issues/" + id, title, body, state, null, new(1, "person"),
            [new(11, "work/bug", "abcdef")], [], null, null, DataHost.Start, updated ?? DataHost.Start, state == "closed" ? DataHost.Start : null, 0, null);

    private static async Task<(GitHubRepositoryRecord Repository, GitHubConnectionRecord Connection, GitHubInboundCommands Inbound)> Setup(DataHost host, GitHubFilter? filter = null, bool projects = false)
    {
        var api = new FakeGitHub(); api.PutRepository(new(10, "R10", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(DataHost.Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, filter ?? GitHubFilter.Empty, projects));
        return (repo, connection, new(host.Store, host.Ids, host.Clock));
    }

    [Test]
    public async Task Import_CreatesTaskChecklistNestedTagsHistoryWithoutUndo_AndIsIdempotent()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        await Assert.That((await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()])).Created).IsEqualTo(1);
        await Assert.That((await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()])).Created).IsEqualTo(0);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await Assert.That(task.Notes).IsEqualTo(Issue().Body);
            var subtasks = await s.Subtasks.ListByTaskAsync(task.Id, ct);
            await Assert.That(subtasks.Count).IsEqualTo(2);
            await Assert.That(subtasks[1].Status).IsEqualTo(TaskItemStatus.Done);
            await Assert.That((await s.GitHub.ListChecklistItemsAsync(link.Id, ct)).Count).IsEqualTo(2);
            var tags = await s.Tags.ListByOwnerAsync(connection.ProfileId, ct);
            await Assert.That(tags.Count).IsEqualTo(2);
            await Assert.That((await s.Tags.ListTaskTagIdsAsync(task.Id, ct)).Single()).IsEqualTo(tags.Single(t => t.DisplayName == "bug").Id);
            var history = await s.History.ListByTaskAsync(task.Id, ct);
            await Assert.That(history.Count).IsEqualTo(1);
            await Assert.That(history[0].EventKind).IsEqualTo("github.imported");
            await Assert.That(history[0].Actor).IsEqualTo("github:person");
            await Assert.That(link.LocalDirty).IsFalse();
            await Assert.That((await s.Undo.ListOpenAsync(connection.ProfileId, 100, ct)).Count).IsEqualTo(0);
            return true;
        });
    }

    [Test]
    public async Task Merge_RemoteWinsConflict_AndKeepsPersonalFieldsAndLocalSnapshot()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await s.Tasks.UpdateAsync(task with { Version = task.Version + 1, Title = "Local title", Priority = TaskPriority.Urgent, EstimateMinutes = 35, PlannedDate = new(2026, 11, 20) }, task.Version, ct);
            return true;
        });
        var result = await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(title: "Changed remotely", updated: DataHost.Start.AddMinutes(1))]);
        await Assert.That(result.Conflicts).IsEqualTo(1);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await Assert.That(task.Title).IsEqualTo("Changed remotely");
            await Assert.That(task.Priority).IsEqualTo(TaskPriority.Urgent);
            await Assert.That(task.EstimateMinutes).IsEqualTo(35);
            await Assert.That(task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 20));
            var conflict = (await s.History.ListByTaskAsync(task.Id, ct)).Single(h => h.EventKind == "github.conflict");
            await Assert.That(conflict.SnapshotBefore!.Contains("Local title", StringComparison.Ordinal)).IsTrue();
            await Assert.That(link.ConflictNoticeUtc).IsNotNull();
            return true;
        });
    }

    [Test]
    public async Task Merge_LocalChecklistChangeSurvivesRemoteTitleChange_AndRemainsDirty()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var sub = (await s.Subtasks.ListByTaskAsync(link.TaskId!, ct))[0];
            await s.Subtasks.UpdateAsync(sub with { Version = sub.Version + 1, Title = "Changed locally", Status = TaskItemStatus.Done, CompletedAtUtc = DataHost.Start }, sub.Version, ct);
            return true;
        });
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(title: "New title", updated: DataHost.Start.AddMinutes(1))]);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await Assert.That(task.Notes).IsEqualTo("Text\r\n- [x] Changed locally\r\n- [x] Second");
            await Assert.That(link.LocalDirty).IsTrue();
            return true;
        });
    }

    [Test]
    public async Task Filter_LeavesAndReturnsToSameTask_ClosedLinkedIssuesStillComplete()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host, new(["work/bug"]));
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        var original = await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct));
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(updated: DataHost.Start.AddMinutes(1)) with { Labels = [] }]);
        await Assert.That((await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!.State).IsEqualTo(1);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(state: "closed", updated: DataHost.Start.AddMinutes(2))]);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            await Assert.That(link.TaskId).IsEqualTo(original!.TaskId);
            await Assert.That(link.State).IsEqualTo(0);
            await Assert.That((await s.Tasks.GetAsync(link.TaskId!, ct))!.Status).IsEqualTo(TaskItemStatus.Done);
            return true;
        });
    }

    [Test]
    public async Task DeletedTaskIsIgnoredAndRestoreReactivates_WithoutRemoteDeletion()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        await host.Store.ExecuteAsync(async (s, ct) => { var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!; var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!; await s.Tasks.UpdateAsync(task with { Version = task.Version + 1, DeletedAtUtc = DataHost.Start }, task.Version, ct); return true; });
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(title: "While deleted", updated: DataHost.Start.AddMinutes(1))]);
        await host.Store.ExecuteAsync(async (s, ct) => { var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!; await Assert.That(link.State).IsEqualTo(3); var task = (await s.Tasks.GetIncludingDeletedAsync(link.TaskId!, ct))!; await s.Tasks.UpdateAsync(task with { Version = task.Version + 1, DeletedAtUtc = null }, task.Version, ct); return true; });
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(title: "While deleted", updated: DataHost.Start.AddMinutes(1))]);
        await host.Store.QueryAsync(async (s, ct) => { var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!; await Assert.That(link.State).IsEqualTo(0); await Assert.That((await s.Tasks.GetAsync(link.TaskId!, ct))!.Title).IsEqualTo("While deleted"); return true; });
    }

    [Test]
    public async Task MilestoneProject_UsesDueDateAndPreservesUserEditedProjectName()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host, projects: true);
        var milestone = new GitHubMilestone(90, 1, "Release", "open", DataHost.Start.AddDays(10));
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue() with { Milestone = milestone }]);
        await host.Store.ExecuteAsync(async (s, ct) => { var map = (await s.GitHub.ListMilestoneProjectsAsync(repo.Id, ct)).Single(); var project = (await s.Containers.GetAsync(map.ContainerId, ct))!; await s.Containers.UpdateAsync(project with { Version = project.Version + 1, Name = "My project" }, project.Version, ct); return true; });
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(updated: DataHost.Start.AddMinutes(1)) with { Milestone = milestone with { Title = "New release", DueOn = DataHost.Start.AddDays(12) } }]);
        await host.Store.QueryAsync(async (s, ct) => { var map = (await s.GitHub.ListMilestoneProjectsAsync(repo.Id, ct)).Single(); var project = (await s.Containers.GetAsync(map.ContainerId, ct))!; await Assert.That(project.Name).IsEqualTo("My project"); var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!; await Assert.That((await s.Tasks.GetAsync(link.TaskId!, ct))!.DueDate).IsEqualTo(DateOnly.FromDateTime(DataHost.Start.AddDays(12).UtcDateTime)); return true; });
    }

    [Test]
    public async Task InvalidOrStaleBatch_CommitsNothing()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        await Assert.That(async () => await inbound.ApplyAsync(repo.Id, repo.Version + 1, [Issue()])).Throws<AppException>();
        await Assert.That(async () => await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(), Issue(2, title: new string('x', 257))])).Throws<AppException>();
        await Assert.That(await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))).IsNull();
    }

    [Test]
    public async Task Scope_PullRequestsAndOldClosedIssuesAreSkipped_AndUnknownVersionRejected()
    {
        using var host = await DataHost.StartAsync(); var (repo, _, inbound) = await Setup(host);
        var result = await inbound.ApplyAsync(repo.Id, repo.Version, [Issue() with { PullRequest = new("https://github.com/person/repo/pull/1") }, Issue(2, state: "closed")]);
        await Assert.That(result.Created).IsEqualTo(0);
        await Assert.That(() => GitHubIssueScope.Parse("{\"v\":2}")).Throws<AppException>();
        var filter = new GitHubFilter(["WORK/BUG"], "all", "me", 1, IncludeClosedDays: 5);
        var issue = Issue(state: "closed") with { Assignees = [new(1, "person")], Milestone = new(10, 1, "M", "open", null) };
        await Assert.That(GitHubIssueScope.Matches(GitHubIssueScope.Parse(filter.ToJson()), issue, 1, DataHost.Start.AddDays(4), false)).IsTrue();
        await Assert.That(GitHubIssueScope.Matches(filter, issue, 1, DataHost.Start.AddDays(6), false)).IsFalse();
    }

    [Test]
    public async Task UnsupportedChecklistAndLabelNames_StayInNotesAndBaselineWithoutBecomingRemovals()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        var issue = Issue(body: "- [ ] \r\n- [ ] " + new string('x', 501)) with { Labels = [new(12, "/", "ffffff")] };
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await Assert.That(task.Notes).IsEqualTo(issue.Body);
            await Assert.That((await s.Subtasks.ListByTaskAsync(task.Id, ct)).Count).IsEqualTo(0);
            await Assert.That(link.LocalDirty).IsFalse();
            return true;
        });
    }

    [Test]
    public async Task ClosedIssueWithoutReason_DoesNotBecomeDirtyOnAnUnchangedFetch()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host, new([], IncludeClosedDays: 7));
        var issue = Issue(state: "closed");
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        await inbound.ApplyAsync(repo.Id, repo.Version, [issue]);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            await Assert.That(link.LocalDirty).IsFalse();
            await Assert.That((await s.Tasks.GetAsync(link.TaskId!, ct))!.Status).IsEqualTo(TaskItemStatus.Done);
            return true;
        });
    }

    [Test]
    public async Task LabelOnlyRemoteChange_AdvancesAggregateVersionForStaleUndoProtection()
    {
        using var host = await DataHost.StartAsync(); var (repo, connection, inbound) = await Setup(host);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue()]);
        await inbound.ApplyAsync(repo.Id, repo.Version, [Issue(updated: DataHost.Start.AddMinutes(1)) with { Labels = [] }]);
        await host.Store.QueryAsync(async (s, ct) =>
        {
            var link = (await s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct))!;
            var task = (await s.Tasks.GetAsync(link.TaskId!, ct))!;
            await Assert.That(task.Version).IsEqualTo(2L);
            await Assert.That((await s.Tags.ListTaskTagIdsAsync(task.Id, ct)).Count).IsEqualTo(0);
            return true;
        });
    }
}
