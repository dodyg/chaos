using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tags;
using Chaos.Domain.GitHub;

namespace Chaos.Application.GitHub;

// Hashes identify only fields included in one PATCH. They contain no issue text.
public sealed record GitHubIssueWriteIntent(string? TitleHash, string? BodyHash, string? StateHash, long? LabelId = null, bool? LabelPresent = null, bool? SelfAssigned = null, string? CommentEntryId = null, long? CommentId = null, string? CommentBodyHash = null, long? TaskVersion = null, long? LinkVersion = null);
public sealed record GitHubOutboundResult(int Pushed, int Updated, int Conflicts);
internal sealed record GitHubSetWrite(string? Label, bool Present, bool IsAssignment);

/// <summary>Prepares issue writes against a fresh read and recovers interrupted PATCHes before inbound merging.</summary>
public sealed class GitHubOutboundCommands(IApplicationStore store, IGitHubApi api, GitHubInboundCommands inbound, TimeProvider clock)
{
    public async Task<GitHubOutboundResult> RecoverAsync(string repositoryId, CancellationToken ct, string? issueLinkId = null)
    {
        var (repo, connection) = await Context(repositoryId, ct);
        var pending = await store.QueryAsync(async (s, token) => issueLinkId is null ? await s.GitHub.ListIssueWritesAsync(repo.Id, token)
            : await s.GitHub.GetIssueWriteAsync(issueLinkId, token) is { } write ? new[] { write } : Array.Empty<GitHubIssueWriteRecord>(), ct);
        int updated = 0, conflicts = 0;
        foreach (var intent in pending)
        {
            if (StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(intent.PayloadJson)?.CommentEntryId is not null) continue;
            var link = await store.QueryAsync((s, token) => s.GitHub.GetIssueLinkAsync(intent.IssueLinkId, token), ct);
            if (link is not { State: 0, TaskId: not null } || link.RepositorySubscriptionId != repo.Id || link.NextAttemptUtc > clock.GetUtcNow()) continue;
            if (await store.QueryAsync((s, token) => s.Tasks.GetAsync(link.TaskId, token), ct) is null) continue;
            try
            {
                var issue = await api.GetIssueAsync(new(connection.AccountId, connection.AccountLogin), repo.Owner, repo.Name, link.IssueNumber, ct);
                CheckIdentity(repo, link, issue);
                await Acknowledge(repo, link, intent, issue, ct);
                var applied = await inbound.ApplyAsync(repo.Id, repo.Version, [issue], ct);
                updated += applied.Updated; conflicts += applied.Conflicts;
            }
            catch (GitHubApiException error) when (IsLinkError(error.Kind)) { await RecordLinkError(store, clock, repo.Id, link.Id, error.Kind, ct); }
            catch (AppException error) when (error.Kind == AppErrorKind.Validation) { await RecordLinkError(store, clock, repo.Id, link.Id, GitHubErrorKind.Validation, ct); }
        }
        return new(0, updated, conflicts);
    }

    public async Task<GitHubOutboundResult> PushAsync(string repositoryId, CancellationToken ct, string? issueLinkId = null)
    {
        var (repo, connection) = await Context(repositoryId, ct);
        var account = new GitHubAccount(connection.AccountId, connection.AccountLogin);
        string? after = null;
        int pushed = 0, updated = 0, conflicts = 0;
        while (true)
        {
            var links = await store.QueryAsync(async (s, token) => issueLinkId is null ? await s.GitHub.ListRepositoryLinksAsync(repo.Id, after, 200, token)
                : await s.GitHub.GetIssueLinkAsync(issueLinkId, token) is { } link ? new[] { link } : Array.Empty<GitHubIssueLinkRecord>(), ct);
            foreach (var candidate in links.Where(l => l.RepositorySubscriptionId == repo.Id && l.State == 0 && l.LocalDirty && l.TaskId is not null && (l.NextAttemptUtc is null || l.NextAttemptUtc <= clock.GetUtcNow())))
            {
                try
                {
                    if (await store.QueryAsync((s, token) => s.GitHub.GetIssueWriteAsync(candidate.Id, token), ct) is not null) continue;
                    var issue = await api.GetIssueAsync(account, repo.Owner, repo.Name, candidate.IssueNumber, ct);
                    CheckIdentity(repo, candidate, issue);
                    await MapTags(repo, connection, candidate, ct);
                    var applied = await inbound.ApplyAsync(repo.Id, repo.Version, [issue], ct);
                    updated += applied.Updated; conflicts += applied.Conflicts;
                    var write = await Prepare(repo, connection, candidate.Id, issue, ct);
                    if (write is not null)
                    {
                        var deferred = await Revalidate(repo, connection, candidate.Id, issue, ct);
                        if (deferred is not null) { updated += deferred.Updated; conflicts += deferred.Conflicts; continue; }
                        await api.UpdateIssueAsync(account, repo.Owner, repo.Name, issue.Number, write, ct);
                        pushed++;
                        var recovered = await RecoverAsync(repo.Id, ct, issueLinkId);
                        updated += recovered.Updated; conflicts += recovered.Conflicts;
                    }
                    for (var count = 0; count < 100; count++)
                    {
                        issue = await api.GetIssueAsync(account, repo.Owner, repo.Name, candidate.IssueNumber, ct);
                        CheckIdentity(repo, candidate, issue);
                        applied = await inbound.ApplyAsync(repo.Id, repo.Version, [issue], ct);
                        updated += applied.Updated; conflicts += applied.Conflicts;
                        var setWrite = await PrepareSet(repo, connection, candidate.Id, issue, ct);
                        if (setWrite is null) break;
                        var deferred = await Revalidate(repo, connection, candidate.Id, issue, ct);
                        if (deferred is not null) { updated += deferred.Updated; conflicts += deferred.Conflicts; break; }
                        if (setWrite.IsAssignment) await api.SetSelfAssignmentAsync(account, repo.Owner, repo.Name, issue.Number, setWrite.Present, ct);
                        else if (setWrite.Present) await api.AddLabelAsync(account, repo.Owner, repo.Name, issue.Number, setWrite.Label!, ct);
                        else await api.RemoveLabelAsync(account, repo.Owner, repo.Name, issue.Number, setWrite.Label!, ct);
                        pushed++;
                        var recovered = await RecoverAsync(repo.Id, ct, issueLinkId);
                        updated += recovered.Updated; conflicts += recovered.Conflicts;
                    }
                }
                catch (GitHubApiException error) when (IsLinkError(error.Kind)) { await RecordLinkError(store, clock, repo.Id, candidate.Id, error.Kind, ct); }
                catch (AppException error) when (error.Kind == AppErrorKind.Validation) { await RecordLinkError(store, clock, repo.Id, candidate.Id, GitHubErrorKind.Validation, ct); }
            }
            if (links.Count < 200) break;
            after = links[^1].Id;
        }
        return new(pushed, updated, conflicts);
    }

    private Task<GitHubIssueUpdate?> Prepare(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, string linkId, GitHubIssue issue, CancellationToken ct) =>
        store.ExecuteAsync<GitHubIssueUpdate?>(async (s, token) =>
        {
            var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
            var currentConnection = await s.GitHub.GetConnectionAsync(connection.Id, token);
            var link = await s.GitHub.GetIssueLinkAsync(linkId, token);
            if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || currentRepo.DeletedAtUtc is not null || currentConnection is null || currentConnection.State != 0 || currentConnection.DeletedAtUtc is not null || link is null || link.RepositorySubscriptionId != repo.Id)
                throw AppException.VersionConflict("The GitHub subscription changed during sync. Run sync again.");
            if (link.State != 0 || link.TaskId is null) return null;
            if (await s.GitHub.GetIssueWriteAsync(link.Id, token) is not null) throw AppException.VersionConflict("A GitHub write needs recovery before another can start.");
            if (link.IssueId != issue.Id || link.IssueNumber != issue.Number || issue.UpdatedAt < link.BaseRemoteUpdatedUtc)
                throw AppException.VersionConflict("The GitHub issue changed during sync. Run sync again.");
            var task = await s.Tasks.GetAsync(link.TaskId, token);
            if (task is null) return null;
            var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson) ?? throw AppException.Validation("issue", "The GitHub issue baseline could not be read.");
            var local = await GitHubInboundCommands.Project(s, repo, link, task, baseline, connection.AccountId, token);
            var remote = GitHubIssueSnapshot.From(issue).Projection();
            var merge = SyncMerge.Issue(baseline.Projection(), local, remote);
            if (local.Title.Length > 256) throw AppException.Validation("title", "A GitHub issue title can hold at most 256 characters.");
            if (local.Body.Length > 65536) throw AppException.Validation("notes", "A GitHub issue body can hold at most 65,536 characters including checklist items.");
            var state = merge.Push.State;
            var write = new GitHubIssueUpdate(merge.Push.Title, merge.Push.Body, state is null ? null : state.Value.IsOpen ? "open" : "closed", state?.Reason);
            if (write.Title is null && write.Body is null && write.State is null) return null;
            var intent = new GitHubIssueWriteIntent(write.Title is null ? null : StoredJsonFormats.Hash(write.Title), write.Body is null ? null : StoredJsonFormats.Hash(write.Body), state is null ? null : StateHash(state.Value), TaskVersion: task.Version, LinkVersion: link.Version);
            await s.GitHub.AddIssueWriteAsync(new(link.Id, StoredJsonFormats.Snapshot(intent), clock.GetUtcNow()), token);
            return write;
        }, ct);

    private Task<GitHubSetWrite?> PrepareSet(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, string linkId, GitHubIssue issue, CancellationToken ct) => store.ExecuteAsync<GitHubSetWrite?>(async (s, token) =>
    {
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var currentConnection = await s.GitHub.GetConnectionAsync(connection.Id, token);
        var link = await s.GitHub.GetIssueLinkAsync(linkId, token);
        if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || currentRepo.DeletedAtUtc is not null || currentConnection is null || currentConnection.State != 0 || currentConnection.DeletedAtUtc is not null || link is null || link.RepositorySubscriptionId != repo.Id)
            throw AppException.VersionConflict("The GitHub subscription changed during sync. Run sync again.");
        if (link.State != 0 || link.TaskId is null) return null;
        if (await s.GitHub.GetIssueWriteAsync(link.Id, token) is not null) return null;
        if (link.IssueId != issue.Id || link.IssueNumber != issue.Number || issue.UpdatedAt < link.BaseRemoteUpdatedUtc) throw AppException.VersionConflict("The GitHub issue changed during sync. Run sync again.");
        var task = await s.Tasks.GetAsync(link.TaskId, token);
        if (task is null) return null;
        var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson) ?? throw AppException.Validation("issue", "The GitHub issue baseline could not be read.");
        var local = await GitHubInboundCommands.Project(s, repo, link, task, baseline, connection.AccountId, token);
        var merge = SyncMerge.Issue(baseline.Projection(), local, GitHubIssueSnapshot.From(issue).Projection());
        var mappings = await s.GitHub.ListLabelTagsAsync(repo.Id, token);
        var labelId = merge.Push.AddLabels.Order().Select(id => (long?)id).FirstOrDefault() ?? merge.Push.RemoveLabels.Order().Select(id => (long?)id).FirstOrDefault();
        GitHubIssueWriteIntent intent;
        GitHubSetWrite write;
        if (labelId is { } id)
        {
            var mapping = mappings.FirstOrDefault(m => m.LabelId == id) ?? throw AppException.Validation("tags", "The GitHub label is no longer available. Refresh and try again.");
            var present = merge.Push.AddLabels.Contains(id);
            write = new(mapping.LabelName, present, false);
            intent = new(null, null, null, id, present);
        }
        else if (merge.Push.AddAssignees.Contains(connection.AccountId) || merge.Push.RemoveAssignees.Contains(connection.AccountId))
        {
            var present = merge.Push.AddAssignees.Contains(connection.AccountId);
            write = new(null, present, true);
            intent = new(null, null, null, SelfAssigned: present);
        }
        else return null;
        await s.GitHub.AddIssueWriteAsync(new(link.Id, StoredJsonFormats.Snapshot(intent with { TaskVersion = task.Version, LinkVersion = link.Version }), clock.GetUtcNow()), token);
        return write;
    }, ct);

    // GitHub does not support conditional issue PATCHes. Re-read after preparation (which can
    // include catalog requests), and defer any changed snapshot instead of sending stale values.
    // This narrows the race; it cannot make the two HTTP requests atomic.
    private async Task<GitHubInboundResult?> Revalidate(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, string linkId, GitHubIssue expected, CancellationToken ct)
    {
        var latest = await api.GetIssueAsync(new(connection.AccountId, connection.AccountLogin), repo.Owner, repo.Name, expected.Number, ct);
        var sameRemote = latest.Id == expected.Id && latest.Number == expected.Number && latest.HtmlUrl == expected.HtmlUrl
            && latest.UpdatedAt == expected.UpdatedAt && StoredJsonFormats.Hash(GitHubIssueSnapshot.From(latest)) == StoredJsonFormats.Hash(GitHubIssueSnapshot.From(expected));
        var allowed = await store.ExecuteAsync(async (s, token) =>
        {
            var link = await s.GitHub.GetIssueLinkAsync(linkId, token);
            var intent = await s.GitHub.GetIssueWriteAsync(linkId, token);
            var task = link?.TaskId is { } id ? await s.Tasks.GetAsync(id, token) : null;
            var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
            var currentConnection = await s.GitHub.GetConnectionAsync(connection.Id, token);
            var prepared = intent is null ? null : StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(intent.PayloadJson);
            var valid = link is { State: 0 } && link.Version == prepared?.LinkVersion && link.RepositorySubscriptionId == repo.Id && task is not null && task.Version == prepared?.TaskVersion
                && currentRepo is { State: 0, DeletedAtUtc: null } && currentRepo.Version == repo.Version
                && currentConnection is { State: 0, DeletedAtUtc: null } && currentConnection.Version == connection.Version;
            if (valid && sameRemote) return true;
            // No mutation has been attempted, so this intent can be safely discarded.
            if (intent is not null) await s.GitHub.DeleteIssueWriteAsync(linkId, token);
            return false;
        }, ct);
        if (allowed) return null;
        if (sameRemote || latest.Id != expected.Id || latest.HtmlUrl != expected.HtmlUrl) return new(0, 0, 0);
        return await inbound.ApplyAsync(repo.Id, repo.Version, [latest], ct);
    }

    private async Task MapTags(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, GitHubIssueLinkRecord candidate, CancellationToken ct)
    {
        var unmapped = await store.QueryAsync(async (s, token) =>
        {
            var link = await s.GitHub.GetIssueLinkAsync(candidate.Id, token);
            if (link is not { State: 0, TaskId: not null }) return Array.Empty<TagWithPath>();
            var ids = (await s.Tags.ListTaskTagIdsAsync(link.TaskId, token)).ToHashSet(StringComparer.Ordinal);
            var mapped = (await s.GitHub.ListLabelTagsAsync(repo.Id, token)).Select(m => m.TagId).ToHashSet(StringComparer.Ordinal);
            return TagCommands.TreeOrder(await s.Tags.ListByOwnerAsync(connection.ProfileId, token)).Where(t => ids.Contains(t.Tag.Id) && !mapped.Contains(t.Tag.Id)).ToArray();
        }, ct);
        if (unmapped.Length == 0) return;
        var account = new GitHubAccount(connection.AccountId, connection.AccountLogin);
        var labels = new List<GitHubLabel>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await api.ListLabelsAsync(account, repo.Owner, repo.Name, cursor, null, ct);
            if (page.NotModified || (page.NextCursor is not null && !cursors.Add(page.NextCursor)) || cursors.Count > 100) throw new GitHubApiException(GitHubErrorKind.Server);
            labels.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        foreach (var tag in unmapped)
        {
            var label = labels.FirstOrDefault(l => l.Name.Equals(tag.Path, StringComparison.OrdinalIgnoreCase));
            if (label is null && connection.CreateLabelsForTags)
            {
                var allowed = await store.QueryAsync(async (s, token) =>
                {
                    var current = await s.GitHub.GetConnectionAsync(connection.Id, token);
                    var subscription = await s.GitHub.GetRepositoryAsync(repo.Id, token);
                    var link = await s.GitHub.GetIssueLinkAsync(candidate.Id, token);
                    return current is { State: 0, DeletedAtUtc: null, CreateLabelsForTags: true } && subscription is { State: 0, DeletedAtUtc: null } && subscription.Version == repo.Version
                        && link is { State: 0, TaskId: not null } && link.RepositorySubscriptionId == repo.Id && (await s.Tags.ListTaskTagIdsAsync(link.TaskId, token)).Contains(tag.Tag.Id);
                }, ct);
                if (!allowed) continue;
                label = await api.CreateLabelAsync(account, repo.Owner, repo.Name, tag.Path, ct);
                labels.Add(label);
            }
            if (label is null) continue;
            await store.ExecuteAsync(async (s, token) =>
            {
                var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
                var link = await s.GitHub.GetIssueLinkAsync(candidate.Id, token);
                if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || currentRepo.DeletedAtUtc is not null || link is null || link.State != 0 || link.RepositorySubscriptionId != repo.Id || link.TaskId is null) throw AppException.VersionConflict("The GitHub subscription changed during sync.");
                if (!(await s.Tags.ListTaskTagIdsAsync(link.TaskId, token)).Contains(tag.Tag.Id)) return false;
                var mappings = await s.GitHub.ListLabelTagsAsync(repo.Id, token);
                if (mappings.Any(m => m.LabelId == label.Id || m.TagId == tag.Tag.Id)) return false;
                await s.GitHub.UpsertLabelTagAsync(new() { RepositorySubscriptionId = repo.Id, LabelId = label.Id, LabelName = label.Name, LabelColor = label.Color, TagId = tag.Tag.Id }, token);
                return true;
            }, ct);
        }
    }

    private Task<bool> Acknowledge(GitHubRepositoryRecord repo, GitHubIssueLinkRecord fetched, GitHubIssueWriteRecord intentRow, GitHubIssue issue, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        var link = await s.GitHub.GetIssueLinkAsync(fetched.Id, token) ?? throw AppException.VersionConflict("The GitHub link changed during recovery.");
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var intent = await s.GitHub.GetIssueWriteAsync(link.Id, token);
        if (currentRepo is null || currentRepo.Version != repo.Version || link.Version != fetched.Version || link.RepositorySubscriptionId != repo.Id || intent != intentRow) throw AppException.VersionConflict("The GitHub link changed during recovery.");
        var attempted = StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(intentRow.PayloadJson) ?? throw AppException.Validation("issue", "The GitHub write record could not be read.");
        var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson) ?? throw AppException.Validation("issue", "The GitHub issue baseline could not be read.");
        var remote = GitHubIssueSnapshot.From(issue);
        var remoteState = remote.Projection().State;
        var labelIds = baseline.LabelIds.ToHashSet();
        if (attempted.LabelId is { } label && attempted.LabelPresent is { } labelPresent && remote.LabelIds.Contains(label) == labelPresent)
        {
            if (labelPresent) labelIds.Add(label); else labelIds.Remove(label);
        }
        var connection = await s.GitHub.GetConnectionAsync(link.ConnectionId, token) ?? throw AppException.VersionConflict("The GitHub connection changed during recovery.");
        var assignees = baseline.AssigneeIds.ToHashSet();
        var assignmentAcknowledged = attempted.SelfAssigned is { } assigned && remote.AssigneeIds.Contains(connection.AccountId) == assigned;
        if (assignmentAcknowledged)
        {
            if (attempted.SelfAssigned == true) assignees.Add(connection.AccountId); else assignees.Remove(connection.AccountId);
        }
        var next = baseline with {
            Title = attempted.TitleHash is not null && attempted.TitleHash == StoredJsonFormats.Hash(remote.Title) ? remote.Title : baseline.Title,
            Body = attempted.BodyHash is not null && attempted.BodyHash == StoredJsonFormats.Hash(remote.Body) ? remote.Body : baseline.Body,
            Open = attempted.StateHash is not null && attempted.StateHash == StateHash(remoteState) ? remote.Open : baseline.Open,
            Reason = attempted.StateHash is not null && attempted.StateHash == StateHash(remoteState) ? remote.Reason : baseline.Reason,
            LabelIds = labelIds.Order().ToArray(), AssigneeIds = assignees.Order().ToArray() };
        var clearIntent = assignmentAcknowledged && link.LocalAssigneeIntent == (attempted.SelfAssigned == true ? 1 : 0);
        if (await s.GitHub.UpdateIssueLinkAsync(link with { BaseJson = StoredJsonFormats.Snapshot(next), LocalAssigneeIntent = clearIntent ? null : link.LocalAssigneeIntent, IssueEtag = null, LocalDirty = true, LocalDirtySinceUtc = link.LocalDirtySinceUtc ?? clock.GetUtcNow() }, link.Version, token) is null)
            throw AppException.VersionConflict("The GitHub link changed during recovery.");
        await s.GitHub.DeleteIssueWriteAsync(link.Id, token);
        return true;
    }, ct);

    private Task<(GitHubRepositoryRecord, GitHubConnectionRecord)> Context(string repositoryId, CancellationToken ct) => store.QueryAsync(async (s, token) =>
    {
        var (profile, space) = await GitHubConnectionCommands.Context(s, token);
        var repo = await s.GitHub.GetRepositoryAsync(repositoryId, token) ?? throw AppException.NotFound("Repository subscription was not found.");
        var connection = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token);
        if (repo.State != 0 || repo.DeletedAtUtc is not null || connection is null || connection.State != 0 || connection.DeletedAtUtc is not null || connection.ProfileId != profile.Id || connection.SpaceId != space.Id)
            throw AppException.VersionConflict("The GitHub subscription is no longer active.");
        return (repo, connection);
    }, ct);

    private static void CheckIdentity(GitHubRepositoryRecord repo, GitHubIssueLinkRecord link, GitHubIssue issue)
    {
        if (issue.Id != link.IssueId || issue.Number != link.IssueNumber || !GitHubIssueAddress.TryParse(issue.HtmlUrl, out var address) || !address.IsIn(repo.Owner, repo.Name) || address.Number != issue.Number)
            throw new GitHubApiException(GitHubErrorKind.Validation);
    }

    private static string StateHash(GitHubIssueState state) => StoredJsonFormats.Hash(state.IsOpen ? new GitHubIssueState(true, null) : state);

    internal static bool IsLinkError(GitHubErrorKind kind) => kind is GitHubErrorKind.Forbidden or GitHubErrorKind.NotFound or GitHubErrorKind.Gone or GitHubErrorKind.Moved or GitHubErrorKind.Validation or GitHubErrorKind.Server;

    internal static Task<bool> RecordLinkError(IApplicationStore store, TimeProvider clock, string repoId, string linkId, GitHubErrorKind kind, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        var link = await s.GitHub.GetIssueLinkAsync(linkId, token);
        if (link is null || link.RepositorySubscriptionId != repoId || link.State != 0) return false;
        var attempts = Math.Min(link.ErrorAttempts + 1, 16);
        var now = clock.GetUtcNow();
        return await s.GitHub.UpdateIssueLinkAsync(link with { LocalDirty = true, LocalDirtySinceUtc = link.LocalDirtySinceUtc ?? now,
            LastErrorKind = (int)kind, LastErrorMessage = kind.ToString(), ErrorAttempts = attempts,
            NextAttemptUtc = now.AddMinutes(Math.Min(30, 1 << Math.Min(attempts - 1, 5))), UpdatedAtUtc = now }, link.Version, token) is not null;
    }, ct);
}
