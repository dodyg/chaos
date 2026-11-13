using Chaos.Application.Contracts;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Application.Transfer;
using Chaos.Infrastructure.Transfer;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Tests.Migrations;

public sealed class GitHubStoreTests
{
    [Test]
    public async Task ConnectionBackoff_SurvivesRestart_IncreasesAndClearsOnSuccess()
    {
        var host = await DataHost.StartAsync();
        var root = host.Root;
        var (connection, _) = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            await s.GitHub.RecordSyncOutcomeAsync(connection.Id, null, DataHost.Start, DataHost.Start, GitHubErrorKind.Network, null, ct);
            await Assert.That(await s.GitHub.GetConnectionRetryAsync(connection.Id, ct)).IsEqualTo(DataHost.Start.AddMinutes(1));
            return true;
        });
        host.Dispose();
        using var reopened = await DataHost.StartAsync(root);
        await reopened.Store.ExecuteAsync(async (s, ct) =>
        {
            await Assert.That(await s.GitHub.GetConnectionRetryAsync(connection.Id, ct)).IsEqualTo(DataHost.Start.AddMinutes(1));
            await s.GitHub.RecordSyncOutcomeAsync(connection.Id, null, DataHost.Start.AddMinutes(1), DataHost.Start.AddMinutes(1), GitHubErrorKind.Network, null, ct);
            await Assert.That(await s.GitHub.GetConnectionRetryAsync(connection.Id, ct)).IsEqualTo(DataHost.Start.AddMinutes(3));
            var now = DataHost.Start.AddMinutes(3);
            foreach (var minutes in new[] { 4, 8, 16, 30, 30 })
            {
                await s.GitHub.RecordSyncOutcomeAsync(connection.Id, null, now, now, GitHubErrorKind.Network, null, ct);
                await Assert.That(await s.GitHub.GetConnectionRetryAsync(connection.Id, ct)).IsEqualTo(now.AddMinutes(minutes));
                now = now.AddMinutes(minutes);
            }
            await s.GitHub.RecordSyncOutcomeAsync(connection.Id, null, now, now, null, null, ct);
            await Assert.That(await s.GitHub.GetConnectionRetryAsync(connection.Id, ct)).IsNull();
            return true;
        });
    }

    [Test]
    public async Task ScheduledRetries_ExcludePausedRepositoriesAndIgnoredLinks()
    {
        using var host = await DataHost.StartAsync();
        var seed = await host.SeedAsync();
        var (connection, repository) = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (s, ct) =>
        {
            var link = new GitHubIssueLinkRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id, RepositorySubscriptionId = repository.Id,
                TaskId = seed.PaintTaskId, IssueId = 987, IssueNodeId = "node", IssueNumber = 1, HtmlUrl = "https://github.com/person/repo/issues/1",
                BaseJson = "{}", BaseRemoteUpdatedUtc = DataHost.Start, CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start,
                LocalDirty = true, NextAttemptUtc = DataHost.Start.AddMinutes(1) };
            await s.GitHub.AddIssueLinkAsync(link, ct);
            await Assert.That((await s.GitHub.ListScheduledRetriesAsync(connection.Id, 10, ct)).Single().Id).IsEqualTo(link.Id);
            var paused = await s.GitHub.UpdateRepositoryAsync(repository with { State = 1 }, repository.Version, ct);
            await Assert.That((await s.GitHub.ListScheduledRetriesAsync(connection.Id, 10, ct)).Count).IsEqualTo(0);
            await s.GitHub.UpdateRepositoryAsync(paused! with { State = 0 }, paused!.Version, ct);
            await s.GitHub.UpdateIssueLinkAsync(link with { State = 3 }, link.Version, ct);
            await Assert.That((await s.GitHub.ListScheduledRetriesAsync(connection.Id, 10, ct)).Count).IsEqualTo(0);
            return true;
        });
    }
    private static async Task<(GitHubConnectionRecord Connection, GitHubRepositoryRecord Repository)> SeedAsync(DataHost host) =>
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            var profile = (await session.Profiles.GetPrimaryAsync(ct))!;
            var space = (await session.Spaces.GetPersonalByOwnerAsync(profile.Id, ct))!;
            var inbox = (await session.Containers.GetInboxAsync(space.Id, ct))!;
            var connection = new GitHubConnectionRecord { Id = Guid.NewGuid().ToString(), ProfileId = profile.Id, SpaceId = space.Id,
                AccountId = 123, AccountLogin = "person", WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com",
                CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start, RateLimitResetUtc = DataHost.Start.AddMinutes(1) };
            await session.GitHub.AddConnectionAsync(connection, ct);
            var repository = new GitHubRepositoryRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id,
                RepositoryId = 456, Owner = "person", Name = "repo", FullName = "person/repo", HtmlUrl = "https://github.com/person/repo",
                TargetContainerId = inbox.Id, FilterJson = "{}", IsPrivate = true, CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start };
            await session.GitHub.AddRepositoryAsync(repository, ct);
            return (connection, repository);
        });

    [Test]
    public async Task ConnectionAndRepository_RoundTrip_RejectsStaleUpdate()
    {
        using var host = await DataHost.StartAsync();
        var (connection, repository) = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            await Assert.That(await session.GitHub.GetConnectionForSpaceAsync(connection.SpaceId, ct)).IsEqualTo(connection);
            await Assert.That((await session.GitHub.ListSubscriptionsAsync(connection.Id, ct)).Single()).IsEqualTo(repository);
            var updated = await session.GitHub.UpdateConnectionAsync(connection with { State = 1 }, 1, ct);
            await Assert.That(updated!.Version).IsEqualTo(2L);
            await Assert.That(await session.GitHub.UpdateConnectionAsync(connection, 1, ct)).IsNull();
            await Assert.That((await session.GitHub.GetConnectionAsync(connection.Id, ct))!.State).IsEqualTo(1);
            return true;
        });
    }

    [Test]
    public async Task IssueLink_QueriesAndVersioning_RespectStateAndRetryBounds()
    {
        using var host = await DataHost.StartAsync();
        var (connection, repository) = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            var row = new GitHubIssueLinkRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id,
                RepositorySubscriptionId = repository.Id, IssueId = 987, IssueNodeId = "node", IssueNumber = 1,
                HtmlUrl = "https://github.com/person/repo/issues/1", BaseJson = "{}", BaseRemoteUpdatedUtc = DataHost.Start,
                CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start, LocalDirty = true, NextAttemptUtc = DataHost.Start };
            await session.GitHub.AddIssueLinkAsync(row, ct);
            await Assert.That(await session.GitHub.FindLinkByIssueAsync(connection.Id, row.IssueId, ct)).IsEqualTo(row);
            await Assert.That((await session.GitHub.ListDirtyLinksAsync(1, ct)).Single()).IsEqualTo(row);
            await Assert.That((await session.GitHub.ListLinksDueForRetryAsync(DataHost.Start, 1, ct)).Single()).IsEqualTo(row);
            await Assert.That((await session.GitHub.ListLinksDueForRetryAsync(DataHost.Start.AddTicks(-1), 1, ct)).Count).IsEqualTo(0);
            await session.GitHub.UpdateIssueLinkAsync(row with { State = 3 }, 1, ct);
            await Assert.That(await session.GitHub.UpdateIssueLinkAsync(row, 1, ct)).IsNull();
            await Assert.That((await session.GitHub.ListDirtyLinksAsync(1, ct)).Count).IsEqualTo(0);
            await Assert.That(async () => await session.GitHub.ListDirtyLinksAsync(201, ct)).Throws<ArgumentOutOfRangeException>();
            return true;
        });
    }

    [Test]
    [Arguments("keep")]
    [Arguments("missing")]
    [Arguments("collision")]
    public async Task Import_GitHubLink_KeepsOrDetachesWithOrigin(string scenario)
    {
        using var host = await DataHost.StartAsync();
        var (connection, repository) = await SeedAsync(host);
        var link = new GitHubIssueLinkRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id,
            RepositorySubscriptionId = repository.Id, IssueId = 987, IssueNodeId = "node", IssueNumber = 1,
            HtmlUrl = "https://github.com/person/repo/issues/1", BaseJson = "{}", BaseRemoteUpdatedUtc = DataHost.Start,
            CreatedAtUtc = DataHost.Start, UpdatedAtUtc = DataHost.Start };
        if (scenario == "collision") await host.Store.ExecuteAsync(async (session, ct) => { await session.GitHub.AddIssueLinkAsync(link with { Id = Guid.NewGuid().ToString() }, ct); return true; });
        if (scenario == "missing") link = link with { ConnectionId = Guid.NewGuid().ToString() };
        var taskId = Guid.NewGuid().ToString();
        var diaryId = Guid.NewGuid().ToString();
        var subtaskId = Guid.NewGuid().ToString();
        var item = new ImportTask { Line = 1, SourceKey = taskId, PreservedId = taskId, HasStableKey = true, Title = "Imported issue", GitHubLink = link,
            Subtasks = [new("Checklist", "", Domain.Tasks.TaskItemStatus.NotStarted, subtaskId, new GitHubChecklistItemRecord { IssueLinkId = link.Id, SubtaskId = subtaskId, BaseText = "Checklist" })],
            Diary = [new(DataHost.Start, "Comment", new GitHubCommentLinkRecord { IssueLinkId = link.Id, DiaryEntryId = diaryId, Origin = 1, State = 1 })] };
        var draft = new ImportDraft(ImportSourceKind.ChaosJson, "archive", new string('a', 64), [item], [], []);
        var preview = await host.Imports.PreviewAsync(draft);
        await Assert.That(preview.Warnings.Count).IsEqualTo(scenario == "keep" ? 0 : 1);
        await Assert.That(preview.DiaryEntries).IsEqualTo(scenario == "keep" ? 1 : 2);
        await host.Imports.CommitAsync(draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null));
        await host.Store.QueryAsync(async (session, ct) =>
        {
            var importedLink = await session.GitHub.FindLinkByTaskAsync(taskId, ct);
            var diary = await session.Diary.ListByTaskAsync(taskId, false, ct);
            if (scenario == "keep")
            {
                await Assert.That(importedLink!.IssueId).IsEqualTo(987L);
                await Assert.That((await session.GitHub.ListChecklistItemsAsync(link.Id, ct)).Single().SubtaskId).IsEqualTo(subtaskId);
                await Assert.That((await session.GitHub.ListCommentLinksAsync(link.Id, ct)).Single().DiaryEntryId).IsEqualTo(diaryId);
            }
            else
            {
                await Assert.That(importedLink).IsNull();
                await Assert.That(diary.Any(entry => entry.RawMarkdown == "Came from " + link.HtmlUrl)).IsTrue();
            }
            return true;
        });
        if (scenario == "keep")
        {
            var export = await host.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(host.Root, "out"), CancellationToken.None);
            using var input = File.OpenRead(export.Path);
            var parsed = ArchiveMerge.ToDraft(JsonArchive.Read(input), "export", "fingerprint");
            var parsedTask = parsed.Tasks.Single();
            await Assert.That(parsedTask.GitHubLink!.IssueId).IsEqualTo(link.IssueId);
            await Assert.That(parsedTask.Subtasks.Single().GitHubChecklist!.SubtaskId).IsEqualTo(subtaskId);
            await Assert.That(parsedTask.Diary.Single().GitHubComment!.DiaryEntryId).IsEqualTo(diaryId);
        }
    }

    [Test]
    public async Task MappingUpserts_LabelAndMilestone_RetainStableIdentityAcrossRename()
    {
        using var host = await DataHost.StartAsync();
        var seed = await host.SeedAsync();
        var (connection, repository) = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            var tag = (await session.Tags.ListByOwnerAsync(connection.ProfileId, ct)).First();
            var label = new GitHubLabelTagRecord { RepositorySubscriptionId = repository.Id, LabelId = 1, LabelName = "bug", LabelColor = "ffffff", TagId = tag.Id };
            await session.GitHub.UpsertLabelTagAsync(label, ct);
            await session.GitHub.UpsertLabelTagAsync(label with { LabelName = "renamed" }, ct);
            var storedLabel = (await session.GitHub.ListLabelTagsAsync(repository.Id, ct)).Single();
            await Assert.That(storedLabel.TagId).IsEqualTo(tag.Id);
            await Assert.That(storedLabel.LabelName).IsEqualTo("renamed");
            var milestone = new GitHubMilestoneProjectRecord { RepositorySubscriptionId = repository.Id, MilestoneId = 2, MilestoneNumber = 1, ContainerId = seed.ProjectId, BaseTitle = "Release", BaseDueOn = "2026-12-01" };
            await session.GitHub.UpsertMilestoneProjectAsync(milestone, ct);
            await session.GitHub.UpsertMilestoneProjectAsync(milestone with { BaseTitle = "Release 2" }, ct);
            await Assert.That((await session.GitHub.ListMilestoneProjectsAsync(repository.Id, ct)).Single()).IsEqualTo(milestone with { BaseTitle = "Release 2" });
            return true;
        });
    }

    [Test]
    public async Task SyncRuns_MoreThanTwoHundred_KeepsNewestPerConnection()
    {
        using var host = await DataHost.StartAsync();
        var (connection, _) = await SeedAsync(host);
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            for (var index = 0; index < 202; index++)
                await session.GitHub.AddSyncRunAsync(new GitHubSyncRunRecord { Id = Guid.NewGuid().ToString(), ConnectionId = connection.Id, StartedAtUtc = DataHost.Start.AddSeconds(index), RequestCount = index }, ct);
            var rows = await session.GitHub.ListSyncRunsAsync(connection.Id, ct);
            await Assert.That(rows.Count).IsEqualTo(200);
            await Assert.That(rows[0].RequestCount).IsEqualTo(201L);
            await Assert.That(rows[^1].RequestCount).IsEqualTo(2L);
            return true;
        });
    }
}
