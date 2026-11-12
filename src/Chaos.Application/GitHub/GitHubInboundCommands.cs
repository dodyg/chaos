using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.GitHub;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Application.GitHub;

public sealed record GitHubInboundResult(int Created, int Updated, int Conflicts);

/// <summary>Applies fetched issue snapshots in short transactions. No network, undo entry or credential access.</summary>
public sealed class GitHubInboundCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public Task<GitHubInboundResult> ApplyAsync(string repositoryId, long expectedRepositoryVersion, IReadOnlyList<GitHubIssue> issues, CancellationToken ct = default)
    {
        if (issues.Count > 50) throw new ArgumentOutOfRangeException(nameof(issues), "Inbound batches contain at most 50 issues.");
        return store.ExecuteAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubConnectionCommands.Context(s, token);
            var repo = await s.GitHub.GetRepositoryAsync(repositoryId, token) ?? throw AppException.NotFound("Repository subscription was not found.");
            var connection = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token) ?? throw AppException.NotFound("GitHub connection was not found.");
            if (repo.DeletedAtUtc is not null || connection.DeletedAtUtc is not null || connection.SpaceId != space.Id || connection.ProfileId != profile.Id || repo.State != 0 || connection.State != 0)
                throw AppException.VersionConflict("The GitHub subscription is no longer active.");
            if (repo.Version != expectedRepositoryVersion) throw AppException.VersionConflict("Repository settings changed during the fetch. Run sync again.");
            var filter = GitHubIssueScope.Parse(repo.FilterJson);
            var now = clock.GetUtcNow();
            int created = 0, updated = 0, conflicts = 0;
            foreach (var issue in issues)
            {
                if (issue.PullRequest is not null) continue;
                if (issue.Id <= 0 || issue.Number <= 0 || string.IsNullOrEmpty(issue.NodeId) || string.IsNullOrWhiteSpace(issue.Title) || issue.Title.Length > 256 || (issue.Body?.Length ?? 0) > 65536)
                    throw AppException.Validation("issue", "GitHub returned an issue outside the supported size or identity limits.");
                if (!GitHubIssueAddress.TryParse(issue.HtmlUrl, out var address) || !address.IsIn(repo.Owner, repo.Name) || address.Number != issue.Number)
                    throw AppException.Validation("issue", "The issue moved during retrieval. Run sync again to refresh its destination.");
                var link = await s.GitHub.FindLinkByIssueAsync(connection.Id, issue.Id, token);
                var transferred = link is not null && link.RepositorySubscriptionId != repo.Id;
                if (transferred && link!.State == 4) continue;
                if (transferred)
                {
                    var origin = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link!.BaseJson);
                    link = link with { BaseJson = origin is null ? link.BaseJson : StoredJsonFormats.Snapshot(origin with { RepositorySubscriptionId = origin.RepositorySubscriptionId ?? link.RepositorySubscriptionId }), RepositorySubscriptionId = repo.Id, IssueEtag = null, IssueNumber = issue.Number, HtmlUrl = address.WebUrl };
                    await s.GitHub.ClearCommentCheckpointAsync(link.Id, token);
                }
                var remote = GitHubIssueSnapshot.From(issue) with { RepositorySubscriptionId = repo.Id };
                if (link is { State: 4 } or { State: 3, TaskId: null })
                {
                    if (transferred || issue.UpdatedAt > link.BaseRemoteUpdatedUtc)
                        await s.GitHub.UpdateIssueLinkAsync(link with { BaseRemoteUpdatedUtc = issue.UpdatedAt, UpdatedAtUtc = now }, link.Version, token);
                    continue;
                }
                var task = link?.TaskId is { } taskId ? await s.Tasks.GetIncludingDeletedAsync(taskId, token) : null;
                if (link is not null && (task is null || task.DeletedAtUtc is not null))
                {
                    await s.GitHub.UpdateIssueLinkAsync(link with { State = 3, LocalDirty = false, LocalDirtySinceUtc = null, UpdatedAtUtc = now }, link.Version, token);
                    continue;
                }
                var matches = GitHubIssueScope.Matches(filter, issue, connection.AccountId, now, link is not null);
                if (!matches)
                {
                    if (link is not null && (link.State != 1 || transferred)) await s.GitHub.UpdateIssueLinkAsync(link with { State = 1, LocalDirty = false, LocalDirtySinceUtc = null, UpdatedAtUtc = now }, link.Version, token);
                    continue;
                }
                var before = task is null ? null : await Snapshot(s, task, token);
                var baseline = link is null ? remote : StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson)
                    ?? throw AppException.Validation("issue", "The GitHub issue baseline could not be read.");
                if (link is not null && issue.UpdatedAt < link.BaseRemoteUpdatedUtc) continue;
                if (transferred) baseline = baseline with { LabelIds = remote.LabelIds };
                var local = task is null ? remote.Projection() : await Project(s, repo, link!, task, baseline, connection.AccountId, token);
                var merge = SyncMerge.Issue(baseline.Projection(), local, remote.Projection());
                var conflict = merge.Conflicts.Title is not null || merge.Conflicts.Body is not null || merge.Conflicts.State is not null;
                var dirty = !merge.Value.EquivalentTo(remote.Projection());
                var status = IssueStateMapping.ToTask(merge.Value.State, task?.Status ?? TaskItemStatus.NotStarted,
                    task is { IsTerminal: true } ? await TaskMutationSupport.PreviousNonterminalStatusAsync(s, task.Id, null, token) : null);
                var placement = await Placement(s, repo, issue.Milestone, space.Id, now, token);
                if (task is null || (link!.PlacementManaged && (transferred || (baseline.RepositorySubscriptionId is not null && baseline.RepositorySubscriptionId != repo.Id) || baseline.MilestoneNumber != remote.MilestoneNumber))) await ValidatePlacement(s, placement.ContainerId, placement.SectionId, token);
                if (task is null)
                {
                    task = new TaskRecord { Id = ids.NewId().ToString(), SpaceId = space.Id, ContainerId = placement.ContainerId, SectionId = placement.SectionId,
                        Title = merge.Value.Title, Notes = merge.Value.Body, Status = status, DueDate = remote.MilestoneDue,
                        CompletedAtUtc = status == TaskItemStatus.Done ? now : null, CancelledAtUtc = status == TaskItemStatus.Cancelled ? now : null,
                        Position = await s.Tasks.GetNextPositionAsync(placement.ContainerId, token), Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
                    await s.Tasks.AddAsync(task, token);
                    link = new GitHubIssueLinkRecord { Id = ids.NewId().ToString(), RepositorySubscriptionId = repo.Id, ConnectionId = connection.Id, TaskId = task.Id,
                        IssueId = issue.Id, IssueNodeId = issue.NodeId, IssueNumber = issue.Number, HtmlUrl = issue.HtmlUrl, BaseJson = StoredJsonFormats.Snapshot(remote),
                        BaseRemoteUpdatedUtc = issue.UpdatedAt, CreatedAtUtc = now, UpdatedAtUtc = now };
                    await s.GitHub.AddIssueLinkAsync(link, token);
                    created++;
                }
                else
                {
                    var move = link!.PlacementManaged && (transferred || (baseline.RepositorySubscriptionId is not null && baseline.RepositorySubscriptionId != repo.Id) || baseline.MilestoneNumber != remote.MilestoneNumber);
                    var due = task.DueDate == baseline.MilestoneDue && task.DueTime is null && task.DueZoneId is null ? remote.MilestoneDue : task.DueDate;
                    var next = task with { Title = merge.Value.Title, Notes = merge.Value.Body, Status = status, DueDate = due,
                        ContainerId = move ? placement.ContainerId : task.ContainerId, SectionId = move ? placement.SectionId : task.SectionId,
                        Position = move ? await s.Tasks.GetNextPositionAsync(placement.ContainerId, token) : task.Position,
                        CompletedAtUtc = status == TaskItemStatus.Done ? task.CompletedAtUtc ?? now : null,
                        CancelledAtUtc = status == TaskItemStatus.Cancelled ? task.CancelledAtUtc ?? now : null };
                    if (next != task)
                    {
                        task = await s.Tasks.UpdateAsync(next with { Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token) ?? throw AppException.VersionConflict("The task changed during GitHub import.");
                        updated++;
                    }
                }
                await Checklist(s, link!, task, now, token);
                await Labels(s, profile.Id, repo, task.Id, issue.Labels, merge.Value.LabelIds, now, token);
                var after = await Snapshot(s, task, token);
                if (before is not null && task.Version == before.Task.Version && StoredJsonFormats.Hash(before) != StoredJsonFormats.Hash(after))
                {
                    task = await s.Tasks.UpdateAsync(task with { Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token) ?? throw AppException.VersionConflict("The task changed during GitHub import.");
                    after = after with { Task = task };
                    updated++;
                }
                if (before is null || StoredJsonFormats.Hash(before) != StoredJsonFormats.Hash(after) || conflict || transferred)
                    await s.History.AddAsync(new TaskHistoryRecord { Id = ids.NewId().ToString(), TaskId = task.Id, OperationId = ids.NewId().ToString(),
                        EffectiveAtUtc = now, RecordedAtUtc = now, Actor = "github:" + connection.AccountLogin, EventKind = before is null ? "github.imported" : conflict ? "github.conflict" : transferred ? "github.transferred" : "github.updated",
                        ChangedFields = before is null ? ["Imported"] : TaskMutationSupport.ChangedTaskFields(before.Task, task).Concat(["GitHub"]).ToArray(),
                        SnapshotBefore = before is null ? null : StoredJsonFormats.Snapshot(before), SnapshotAfter = StoredJsonFormats.Snapshot(after), AggregateVersion = task.Version }, token);
                if (conflict) conflicts++;
                // The remote baseline advances even when local-only edits still need a later push.
                await s.GitHub.UpdateIssueLinkAsync(link! with { BaseJson = StoredJsonFormats.Snapshot(remote), BaseRemoteUpdatedUtc = issue.UpdatedAt,
                    State = 0, HtmlUrl = issue.HtmlUrl, IssueNumber = issue.Number, LocalDirty = dirty, LocalDirtySinceUtc = dirty ? link!.LocalDirtySinceUtc ?? now : null,
                    LastSyncedUtc = now, ConflictNoticeUtc = conflict ? now : link!.ConflictNoticeUtc, LastErrorKind = null, LastErrorMessage = null, ErrorAttempts = 0, NextAttemptUtc = null, UpdatedAtUtc = now }, link!.Version, token);
            }
            return new GitHubInboundResult(created, updated, conflicts);
        }, ct);
    }

    internal static async Task<IssueProjection> Project(IDataSession s, GitHubRepositoryRecord repo, GitHubIssueLinkRecord link, TaskRecord task, GitHubIssueSnapshot baseline, long accountId, CancellationToken ct)
    {
        var subtasks = await s.Subtasks.ListByTaskAsync(task.Id, ct);
        var mappings = await s.GitHub.ListChecklistItemsAsync(link.Id, ct);
        var edits = new List<ChecklistEdit>();
        foreach (var mapping in mappings)
        {
            var sub = subtasks.FirstOrDefault(t => t.Id == mapping.SubtaskId);
            if (sub is null) edits.Add(new(checked((int)mapping.BaseOrdinal), mapping.BaseText, mapping.BaseChecked, true));
            else if (sub.Title != mapping.BaseText || (sub.Status == TaskItemStatus.Done) != mapping.BaseChecked)
                edits.Add(new(checked((int)mapping.BaseOrdinal), sub.Title, sub.Status == TaskItemStatus.Done));
        }
        var additions = subtasks.Where(t => !mappings.Any(m => m.SubtaskId == t.Id)).Select(t => new NewChecklistItem(t.Title, t.Status == TaskItemStatus.Done)).ToArray();
        var body = IssueBody.Rewrite(task.Notes, edits, additions);
        var tags = (await s.Tags.ListTaskTagIdsAsync(task.Id, ct)).ToHashSet(StringComparer.Ordinal);
        var labelMappings = await s.GitHub.ListLabelTagsAsync(repo.Id, ct);
        var labels = baseline.LabelIds.Where(id => !labelMappings.Any(m => m.LabelId == id)).ToHashSet();
        foreach (var mapping in labelMappings) if (tags.Contains(mapping.TagId)) labels.Add(mapping.LabelId);
        var assignees = baseline.AssigneeIds.ToHashSet();
        if (link.LocalAssigneeIntent == 1) assignees.Add(accountId);
        else if (link.LocalAssigneeIntent == 0) assignees.Remove(accountId);
        return new(task.Title, body, IssueStateMapping.FromTask(task.Status, new(baseline.Open, baseline.Reason)), labels, assignees);
    }

    private async Task Checklist(IDataSession s, GitHubIssueLinkRecord link, TaskRecord task, DateTimeOffset now, CancellationToken ct)
    {
        var previous = await s.GitHub.ListChecklistItemsAsync(link.Id, ct);
        // Unsupported blank or overlong checklist titles stay byte-for-byte in notes.
        var current = IssueBody.ScanChecklist(task.Notes).Where(i => !string.IsNullOrWhiteSpace(i.Text) && i.Text.EnumerateRunes().Count() <= TaskTitle.MaxUnicodeScalarValues).ToArray();
        var reconciliation = ChecklistReconciler.Match(previous.Select(p => new ChecklistBase(Guid.Parse(p.SubtaskId), checked((int)p.BaseOrdinal), p.BaseText)).ToArray(), current);
        var rows = new List<GitHubChecklistItemRecord>();
        foreach (var match in reconciliation.Items)
        {
            var existing = match.SubtaskId is { } id ? await s.Subtasks.GetAsync(id.ToString(), ct) : null;
            var state = match.Item.Checked ? TaskItemStatus.Done : existing?.Status == TaskItemStatus.Done ? TaskItemStatus.NotStarted : existing?.Status ?? TaskItemStatus.NotStarted;
            var sub = existing is null ? new SubtaskRecord { Id = ids.NewId().ToString(), TaskId = task.Id, Title = match.Item.Text, Status = state,
                Position = await s.Subtasks.GetNextPositionAsync(task.Id, ct), Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now, CompletedAtUtc = match.Item.Checked ? now : null } :
                existing with { Title = match.Item.Text, Status = state, CompletedAtUtc = match.Item.Checked ? existing.CompletedAtUtc ?? now : null, CancelledAtUtc = state == TaskItemStatus.Cancelled ? existing.CancelledAtUtc : null };
            if (existing is null) await s.Subtasks.AddAsync(sub, ct);
            else if (sub != existing) await s.Subtasks.UpdateAsync(sub with { Version = existing.Version + 1, UpdatedAtUtc = now }, existing.Version, ct);
            rows.Add(new GitHubChecklistItemRecord { SubtaskId = sub.Id, IssueLinkId = link.Id, BaseOrdinal = match.Item.Ordinal, BaseText = match.Item.Text, BaseChecked = match.Item.Checked });
        }
        foreach (var removed in reconciliation.RemovedSubtaskIds)
        {
            var sub = await s.Subtasks.GetAsync(removed.ToString(), ct);
            if (sub is not null) await s.Subtasks.UpdateAsync(sub with { Version = sub.Version + 1, DeletedAtUtc = now, UpdatedAtUtc = now }, sub.Version, ct);
        }
        await s.GitHub.ReplaceChecklistItemsAsync(link.Id, rows, ct);
    }

    private async Task Labels(IDataSession s, string profile, GitHubRepositoryRecord repo, string task, IReadOnlyList<GitHubLabel> remote, IReadOnlySet<long> merged, DateTimeOffset now, CancellationToken ct)
    {
        var mappings = (await s.GitHub.ListLabelTagsAsync(repo.Id, ct)).ToList();
        foreach (var label in remote)
        {
            var mapping = mappings.FirstOrDefault(m => m.LabelId == label.Id);
            if (mapping is null)
            {
                IReadOnlyList<Chaos.Domain.Tags.TagSegment> path;
                try { path = LabelNames.ToTagPath(label.Name); }
                catch (DomainValidationException) { continue; } // Keep unsupported labels in the remote baseline; never remove them as a local edit.
                var (tag, _) = await TagCommands.EnsurePathAsync(s, ids, profile, null, path, now, ct);
                mapping = new GitHubLabelTagRecord { RepositorySubscriptionId = repo.Id, LabelId = label.Id, LabelName = label.Name, LabelColor = label.Color, TagId = tag.Id };
                mappings.Add(mapping);
            }
            await s.GitHub.UpsertLabelTagAsync(mapping with { LabelName = label.Name, LabelColor = label.Color }, ct);
        }
        foreach (var mapping in mappings)
            if (merged.Contains(mapping.LabelId) && await s.Tags.GetAsync(mapping.TagId, ct) is not null) await s.Tags.AddTaskLinkAsync(task, mapping.TagId, ct);
            else await s.Tags.RemoveTaskLinkAsync(task, mapping.TagId, ct);
    }

    private async Task<(string ContainerId, string? SectionId)> Placement(IDataSession s, GitHubRepositoryRecord repo, GitHubMilestone? milestone, string space, DateTimeOffset now, CancellationToken ct)
    {
        if (!repo.MilestonesAsProjects || milestone is null) return (repo.TargetContainerId, repo.TargetSectionId);
        var mapping = (await s.GitHub.ListMilestoneProjectsAsync(repo.Id, ct)).FirstOrDefault(m => m.MilestoneId == milestone.Id);
        var project = mapping is null ? null : await s.Containers.GetAsync(mapping.ContainerId, ct);
        var due = milestone.DueOn is { } date ? DateOnly.FromDateTime(date.UtcDateTime) : (DateOnly?)null;
        if (mapping is not null && (project is null || project.ArchivedAtUtc is not null)) return (repo.TargetContainerId, repo.TargetSectionId);
        if (project is null)
        {
            var used = (await s.Containers.ListBySpaceAsync(space, ct)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var title = milestone.Title;
            for (var suffix = 1; used.Contains(title); suffix++) title = milestone.Title + " (" + repo.Name + (suffix == 1 ? "" : " " + suffix) + ")";
            project = new ContainerRecord { Id = ids.NewId().ToString(), SpaceId = space, Kind = ContainerKind.Project, Name = title, DeadlineDate = due,
                Position = await s.Containers.GetNextPositionAsync(space, ct), Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
            await s.Containers.AddAsync(project, ct);
            mapping = new GitHubMilestoneProjectRecord { RepositorySubscriptionId = repo.Id, MilestoneId = milestone.Id, MilestoneNumber = milestone.Number, ContainerId = project.Id, BaseTitle = title, BaseDueOn = due?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
        }
        else
        {
            var managed = project.Name == mapping!.BaseTitle;
            var title = project.Name;
            if (managed)
            {
                var used = (await s.Containers.ListBySpaceAsync(space, ct)).Where(c => c.Id != project.Id).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                title = milestone.Title;
                for (var suffix = 1; used.Contains(title); suffix++) title = milestone.Title + " (" + repo.Name + (suffix == 1 ? "" : " " + suffix) + ")";
            }
            var deadline = project.DeadlineDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) == mapping.BaseDueOn ? due : project.DeadlineDate;
            if (title != project.Name || deadline != project.DeadlineDate)
                project = await s.Containers.UpdateAsync(project with { Version = project.Version + 1, Name = title, DeadlineDate = deadline, UpdatedAtUtc = now }, project.Version, ct) ?? throw AppException.VersionConflict("Milestone project changed.");
            mapping = mapping with { BaseTitle = managed ? title : milestone.Title, BaseDueOn = due?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
        }
        await s.GitHub.UpsertMilestoneProjectAsync(mapping!, ct);
        return (project.Id, null);
    }

    public Task<bool> ApplyCatalogAsync(string repositoryId, long expectedVersion, IReadOnlyList<GitHubLabel> labels, IReadOnlyList<GitHubMilestone> milestones, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        var (profile, space) = await GitHubConnectionCommands.Context(s, token);
        var repo = await s.GitHub.GetRepositoryAsync(repositoryId, token) ?? throw AppException.NotFound("Repository subscription was not found.");
        var connection = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token);
        if (repo.Version != expectedVersion || repo.State != 0 || repo.DeletedAtUtc is not null || connection is null || connection.SpaceId != space.Id || connection.ProfileId != profile.Id || connection.State != 0 || connection.DeletedAtUtc is not null) throw AppException.VersionConflict("Repository settings changed during catalog retrieval.");
        var mappings = await s.GitHub.ListLabelTagsAsync(repo.Id, token);
        foreach (var label in labels)
        {
            var mapping = mappings.FirstOrDefault(m => m.LabelId == label.Id);
            if (mapping is not null && (mapping.LabelName != label.Name || mapping.LabelColor != label.Color)) await s.GitHub.UpsertLabelTagAsync(mapping with { LabelName = label.Name, LabelColor = label.Color }, token);
        }
        var projects = await s.GitHub.ListMilestoneProjectsAsync(repo.Id, token);
        foreach (var milestone in milestones)
            if (projects.Any(m => m.MilestoneId == milestone.Id)) await Placement(s, repo, milestone, space.Id, clock.GetUtcNow(), token);
        return true;
    }, ct);

    private static async Task ValidatePlacement(IDataSession s, string containerId, string? sectionId, CancellationToken ct)
    {
        var container = await s.Containers.GetAsync(containerId, ct);
        if (container is null || container.ArchivedAtUtc is not null) throw AppException.Validation("targetContainerId", "Choose an active destination for this repository before syncing.");
        if (sectionId is not null && (await s.Sections.GetAsync(sectionId, ct))?.ContainerId != containerId) throw AppException.Validation("targetSectionId", "Choose an active destination section before syncing.");
    }

    private static async Task<TaskAggregateSnapshot> Snapshot(IDataSession s, TaskRecord task, CancellationToken ct) =>
        new(task, await s.Subtasks.ListByTaskAsync(task.Id, ct), await s.Tags.ListTaskTagIdsAsync(task.Id, ct));
}
