using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

public static class GitHubCommentMarker
{
    public static string Suffix(string entryId) => "\n\n<!-- chaos:" + entryId + " -->";
    public static string Body(string text, string entryId) => text + Suffix(entryId);
    public static string Strip(string text, string entryId) => text.EndsWith(Suffix(entryId), StringComparison.Ordinal) ? text[..^Suffix(entryId).Length] : text;
}

/// <summary>Posts explicitly queued diary entries, finding their marker before every retry.</summary>
public sealed class GitHubOutboundComments(IApplicationStore store, IGitHubApi api, IIdGenerator ids, TimeProvider clock)
{
    public async Task<int> RunAsync(string repositoryId, bool post, CancellationToken ct)
    {
        var (repo, connection) = await store.QueryAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubConnectionCommands.Context(s, token);
            var repo = await s.GitHub.GetRepositoryAsync(repositoryId, token) ?? throw AppException.NotFound("Repository subscription was not found.");
            var connection = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token);
            if (repo.State != 0 || repo.DeletedAtUtc is not null || connection is null || connection.State != 0 || connection.DeletedAtUtc is not null || connection.ProfileId != profile.Id || connection.SpaceId != space.Id)
                throw AppException.VersionConflict("The GitHub subscription changed during sync.");
            return (repo, connection);
        }, ct);
        var account = new GitHubAccount(connection.AccountId, connection.AccountLogin);
        string? after = null;
        int posted = 0;
        while (true)
        {
            var links = await store.QueryAsync((s, token) => s.GitHub.ListRepositoryLinksAsync(repo.Id, after, 200, token), ct);
            foreach (var link in links.Where(l => l.State == 0 && l.TaskId is not null && (l.NextAttemptUtc is null || l.NextAttemptUtc <= clock.GetUtcNow())))
            {
                try
                {
                var write = await store.QueryAsync((s, token) => s.GitHub.GetIssueWriteAsync(link.Id, token), ct);
                var intent = write is null ? null : StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(write.PayloadJson);
                if (write is not null && intent?.CommentEntryId is not null)
                {
                    var remote = (await ListComments(repo, account, link.IssueNumber, ct)).FirstOrDefault(c => c.Id == intent.CommentId);
                    await RecoverEdit(repo, link, write, intent, remote, ct);
                }
                var mappings = await store.QueryAsync((s, token) => s.GitHub.ListCommentLinksAsync(link.Id, token), ct);
                foreach (var mapping in mappings.Where(m => m.State == 1 && m.CommentId is null && m.Origin == 1))
                {
                    var entry = await store.QueryAsync((s, token) => s.Diary.GetIncludingDeletedAsync(mapping.DiaryEntryId, token), ct);
                    if (entry is null || entry.DeletedAtUtc is not null) continue;
                    var matches = (await ListComments(repo, account, link.IssueNumber, ct)).Where(c => c.User.Id == account.Id && c.Body.EndsWith(GitHubCommentMarker.Suffix(entry.Id), StringComparison.Ordinal)).ToList();
                    if (matches.Count > 1) throw AppException.Validation("comment", "More than one GitHub comment has this diary entry's marker.");
                    var comment = matches.SingleOrDefault();
                    if (comment is null && post)
                    {
                        if (!await CanWrite(repo, connection, link, mapping, entry.RawMarkdown, ct)) continue;
                        comment = await api.PostCommentAsync(account, repo.Owner, repo.Name, link.IssueNumber, GitHubCommentMarker.Body(entry.RawMarkdown, entry.Id), ct);
                        posted++;
                    }
                    if (comment is not null && (comment.Id <= 0 || comment.User.Id != account.Id || !comment.Body.EndsWith(GitHubCommentMarker.Suffix(entry.Id), StringComparison.Ordinal)))
                        throw new GitHubApiException(GitHubErrorKind.Validation);
                    if (comment is not null) await Acknowledge(repo, link, mapping, comment, ct);
                }
                if (post)
                {
                    mappings = await store.QueryAsync((s, token) => s.GitHub.ListCommentLinksAsync(link.Id, token), ct);
                    foreach (var mapping in mappings.Where(m => m.State == 2 && m.CommentId is not null))
                    {
                        var remote = (await ListComments(repo, account, link.IssueNumber, ct)).FirstOrDefault(c => c.Id == mapping.CommentId);
                        if (remote is null || remote.User.Id != account.Id) continue;
                        var remoteBody = mapping.Origin == 1 ? GitHubCommentMarker.Strip(remote.Body, mapping.DiaryEntryId) : remote.Body;
                        if (remoteBody != mapping.BaseBody)
                        {
                            var current = await store.QueryAsync((s, token) => s.GitHub.GetIssueLinkAsync(link.Id, token), ct) ?? throw AppException.VersionConflict("The GitHub link changed during comment sync.");
                            await new GitHubInboundComments(store, ids, clock).ApplyAsync(link.Id, current.Version, [remote], false, ct, 1, repo.Version);
                            continue;
                        }
                        var edit = await PrepareEdit(repo, link, mapping, ct);
                        if (edit is null) continue;
                        var latest = (await ListComments(repo, account, link.IssueNumber, ct)).FirstOrDefault(c => c.Id == mapping.CommentId);
                        if (latest != remote || !await CanWrite(repo, connection, link, mapping, edit, ct))
                        {
                            await store.ExecuteAsync(async (s, token) => { await s.GitHub.DeleteIssueWriteAsync(link.Id, token); return true; }, ct);
                            if (latest is not null && latest != remote)
                            {
                                var current = await store.QueryAsync((s, token) => s.GitHub.GetIssueLinkAsync(link.Id, token), ct);
                                if (current is { State: 0 }) await new GitHubInboundComments(store, ids, clock).ApplyAsync(link.Id, current.Version, [latest], false, ct, 1, repo.Version);
                            }
                            continue;
                        }
                        var body = mapping.Origin == 1 ? GitHubCommentMarker.Body(edit, mapping.DiaryEntryId) : edit;
                        await api.EditCommentAsync(account, repo.Owner, repo.Name, mapping.CommentId!.Value, body, ct);
                        posted++;
                        var saved = (await ListComments(repo, account, link.IssueNumber, ct)).FirstOrDefault(c => c.Id == mapping.CommentId);
                        var pendingWrite = await store.QueryAsync((s, token) => s.GitHub.GetIssueWriteAsync(link.Id, token), ct);
                        var pendingIntent = pendingWrite is null ? null : StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(pendingWrite.PayloadJson);
                        if (pendingWrite is not null && pendingIntent?.CommentEntryId is not null) await RecoverEdit(repo, link, pendingWrite, pendingIntent, saved, ct);
                    }
                    mappings = await store.QueryAsync((s, token) => s.GitHub.ListCommentLinksAsync(link.Id, token), ct);
                    foreach (var mapping in mappings.Where(m => m.State == 3 && m.CommentId is not null))
                    {
                        var remote = (await ListComments(repo, account, link.IssueNumber, ct)).FirstOrDefault(c => c.Id == mapping.CommentId);
                        var entry = await store.QueryAsync((s, token) => s.Diary.GetIncludingDeletedAsync(mapping.DiaryEntryId, token), ct);
                        if (entry is { DeletedAtUtc: null } && remote is not null)
                        {
                            await store.ExecuteAsync(async (s, token) =>
                            {
                                var current = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).FirstOrDefault(m => m.DiaryEntryId == mapping.DiaryEntryId);
                                var latest = await s.Diary.GetIncludingDeletedAsync(mapping.DiaryEntryId, token);
                                if (current is { State: 3 } && latest is { DeletedAtUtc: null })
                                    await s.GitHub.UpsertCommentLinkAsync(current with { State = latest.RawMarkdown == current.BaseBody ? 0 : 2 }, token);
                                return true;
                            }, ct);
                            continue;
                        }
                        if (remote is not null)
                        {
                            if (remote.User.Id != account.Id) throw new GitHubApiException(GitHubErrorKind.Forbidden);
                            var latest = (await ListComments(repo, account, link.IssueNumber, ct)).FirstOrDefault(c => c.Id == mapping.CommentId);
                            if (latest != remote) continue;
                            var stillPending = await CanWrite(repo, connection, link, mapping, null, ct);
                            if (!stillPending) continue;
                            await api.DeleteCommentAsync(account, repo.Owner, repo.Name, mapping.CommentId!.Value, ct);
                            posted++;
                        }
                        await store.ExecuteAsync(async (s, token) =>
                        {
                            var current = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).FirstOrDefault(m => m.DiaryEntryId == mapping.DiaryEntryId);
                            if (current is { State: 3 } && current.CommentId == mapping.CommentId)
                                await s.GitHub.UpsertCommentLinkAsync(current with { State = 4, LastErrorKind = null, LastErrorMessage = null, ErrorAttempts = 0, NextAttemptUtc = null }, token);
                            return true;
                        }, ct);
                    }
                }
                if (mappings.Count > 0) await RefreshDirty(repo, connection, link.Id, ct);
                }
                catch (GitHubApiException error) when (GitHubOutboundCommands.IsLinkError(error.Kind)) { await GitHubOutboundCommands.RecordLinkError(store, clock, repo.Id, link.Id, error.Kind, ct); }
                catch (AppException error) when (error.Kind == AppErrorKind.Validation) { await GitHubOutboundCommands.RecordLinkError(store, clock, repo.Id, link.Id, GitHubErrorKind.Validation, ct); }
            }
            if (links.Count < 200) break;
            after = links[^1].Id;
        }
        return posted;
    }

    private Task<bool> CanWrite(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, GitHubIssueLinkRecord fetched, GitHubCommentLinkRecord pending, string? expectedBody, CancellationToken ct) => store.QueryAsync(async (s, token) =>
    {
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var currentConnection = await s.GitHub.GetConnectionAsync(connection.Id, token);
        var link = await s.GitHub.GetIssueLinkAsync(fetched.Id, token);
        if (currentRepo is not { State: 0, DeletedAtUtc: null } || currentRepo.Version != repo.Version
            || currentConnection is not { State: 0, DeletedAtUtc: null } || currentConnection.Version != connection.Version
            || link is not { State: 0, TaskId: not null } || link.RepositorySubscriptionId != repo.Id || await s.Tasks.GetAsync(link.TaskId, token) is null) return false;
        var mapping = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).FirstOrDefault(m => m.DiaryEntryId == pending.DiaryEntryId);
        var entry = await s.Diary.GetIncludingDeletedAsync(pending.DiaryEntryId, token);
        return mapping is not null && mapping.State == pending.State && mapping.CommentId == pending.CommentId && entry is not null
            && (expectedBody is null ? entry.DeletedAtUtc is not null : entry.DeletedAtUtc is null && entry.RawMarkdown == expectedBody);
    }, ct);

    private Task<bool> RefreshDirty(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, string linkId, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var link = await s.GitHub.GetIssueLinkAsync(linkId, token);
        if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || link is not { State: 0, TaskId: not null } || link.RepositorySubscriptionId != repo.Id) return false;
        var task = await s.Tasks.GetAsync(link.TaskId, token);
        if (task is null) return false;
        var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson) ?? throw AppException.Validation("issue", "The GitHub issue baseline could not be read.");
        var projection = await GitHubInboundCommands.Project(s, repo, link, task, baseline, connection.AccountId, token);
        var pending = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).Any(m => m.State is 1 or 2 or 3);
        var dirty = pending || await s.GitHub.GetIssueWriteAsync(link.Id, token) is not null || !projection.EquivalentTo(baseline.Projection());
        if (link.LocalDirty == dirty) return true;
        return await s.GitHub.UpdateIssueLinkAsync(link with { LocalDirty = dirty, LocalDirtySinceUtc = dirty ? link.LocalDirtySinceUtc ?? clock.GetUtcNow() : null }, link.Version, token) is not null;
    }, ct);

    private async Task<IReadOnlyList<GitHubComment>> ListComments(GitHubRepositoryRecord repo, GitHubAccount account, long number, CancellationToken ct)
    {
        var comments = new List<GitHubComment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await api.ListCommentsAsync(account, repo.Owner, repo.Name, null, number, cursor, null, ct);
            if (page.NotModified || page.NextCursor is not null && (!seen.Add(page.NextCursor) || seen.Count > 100)) throw new GitHubApiException(GitHubErrorKind.Server);
            comments.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return comments;
    }

    private Task<string?> PrepareEdit(GitHubRepositoryRecord repo, GitHubIssueLinkRecord fetched, GitHubCommentLinkRecord fetchedComment, CancellationToken ct) => store.ExecuteAsync<string?>(async (s, token) =>
    {
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var link = await s.GitHub.GetIssueLinkAsync(fetched.Id, token);
        if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || link is null || link.State != 0 || link.RepositorySubscriptionId != repo.Id) throw AppException.VersionConflict("The GitHub link changed during comment sync.");
        if (await s.GitHub.GetIssueWriteAsync(link.Id, token) is not null) return null;
        var mapping = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).FirstOrDefault(m => m.DiaryEntryId == fetchedComment.DiaryEntryId);
        var entry = await s.Diary.GetAsync(fetchedComment.DiaryEntryId, token);
        if (entry is null || mapping is null || mapping.State != 2 || mapping.CommentId != fetchedComment.CommentId || mapping.BaseBody != fetchedComment.BaseBody) return null;
        if (entry.RawMarkdown.Length > 65450) throw AppException.Validation("markdown", "A GitHub comment can hold at most 65,450 characters.");
        var intent = new GitHubIssueWriteIntent(null, null, null, CommentEntryId: entry.Id, CommentId: mapping.CommentId, CommentBodyHash: StoredJsonFormats.Hash(entry.RawMarkdown));
        await s.GitHub.AddIssueWriteAsync(new(link.Id, StoredJsonFormats.Snapshot(intent), clock.GetUtcNow()), token);
        return entry.RawMarkdown;
    }, ct);

    private Task<bool> RecoverEdit(GitHubRepositoryRecord repo, GitHubIssueLinkRecord fetched, GitHubIssueWriteRecord write, GitHubIssueWriteIntent intent, GitHubComment? remote, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var link = await s.GitHub.GetIssueLinkAsync(fetched.Id, token);
        if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || link is null || link.RepositorySubscriptionId != repo.Id || await s.GitHub.GetIssueWriteAsync(link.Id, token) != write) throw AppException.VersionConflict("The GitHub link changed during comment recovery.");
        var mapping = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).FirstOrDefault(m => m.DiaryEntryId == intent.CommentEntryId);
        var entry = intent.CommentEntryId is null ? null : await s.Diary.GetIncludingDeletedAsync(intent.CommentEntryId, token);
        if (mapping is not null && entry is not null && remote is not null && mapping.CommentId == remote.Id)
        {
            var body = mapping.Origin == 1 ? GitHubCommentMarker.Strip(remote.Body, entry.Id) : remote.Body;
            if (StoredJsonFormats.Hash(body) == intent.CommentBodyHash)
                await s.GitHub.UpsertCommentLinkAsync(mapping with { BaseBody = body, BaseRemoteUpdatedUtc = remote.UpdatedAt,
                    State = mapping.State is 3 or 5 ? mapping.State : entry.RawMarkdown == body ? 0 : 2 }, token);
        }
        await s.GitHub.DeleteIssueWriteAsync(link.Id, token);
        return true;
    }, ct);

    private Task<bool> Acknowledge(GitHubRepositoryRecord repo, GitHubIssueLinkRecord fetched, GitHubCommentLinkRecord pending, GitHubComment comment, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        var currentRepo = await s.GitHub.GetRepositoryAsync(repo.Id, token);
        var link = await s.GitHub.GetIssueLinkAsync(fetched.Id, token);
        if (currentRepo is null || currentRepo.Version != repo.Version || currentRepo.State != 0 || link is null || link.RepositorySubscriptionId != repo.Id || link.TaskId is null) throw AppException.VersionConflict("The GitHub link changed during comment recovery.");
        var mapping = (await s.GitHub.ListCommentLinksAsync(link.Id, token)).FirstOrDefault(m => m.DiaryEntryId == pending.DiaryEntryId);
        var entry = await s.Diary.GetIncludingDeletedAsync(pending.DiaryEntryId, token);
        if (mapping is null || mapping.CommentId is not null || entry is null) return false;
        var body = GitHubCommentMarker.Strip(comment.Body, entry.Id);
        var state = mapping.State is 3 or 5 ? mapping.State : entry.DeletedAtUtc is not null ? 5 : entry.RawMarkdown == body ? 0 : 2;
        await s.GitHub.UpsertCommentLinkAsync(mapping with { CommentId = comment.Id, AuthorLogin = comment.User.Login, HtmlUrl = comment.HtmlUrl,
            BaseBody = body, BaseRemoteUpdatedUtc = comment.UpdatedAt, State = state, ErrorAttempts = 0, LastErrorKind = null, LastErrorMessage = null, NextAttemptUtc = null }, token);
        return true;
    }, ct);
}
