using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

/// <summary>Mirrors a fetched page or complete comment set without remote writes. Complete sets permit deletion detection.</summary>
public sealed class GitHubInboundComments(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public Task<int> MarkMissingAsync(string issueLinkId, long expectedVersion, long expectedRepositoryVersion, IReadOnlySet<long> targets, IReadOnlySet<long> seen, CancellationToken ct = default)
    {
        if (targets.Count > 200 || !seen.IsSubsetOf(targets)) throw new ArgumentException("Deletion checks use at most 200 mapped comment IDs.");
        return store.ExecuteAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubConnectionCommands.Context(s, token);
            var link = await s.GitHub.GetIssueLinkAsync(issueLinkId, token) ?? throw AppException.NotFound("GitHub issue link was not found.");
            var repo = await s.GitHub.GetRepositoryAsync(link.RepositorySubscriptionId, token);
            var connection = await s.GitHub.GetConnectionAsync(link.ConnectionId, token);
            if (link.Version != expectedVersion || link.State != 0 || link.TaskId is null || connection is null || connection.SpaceId != space.Id || connection.ProfileId != profile.Id
                || connection.State != 0 || connection.DeletedAtUtc is not null || repo is null || repo.State != 0 || repo.DeletedAtUtc is not null || repo.Version != expectedRepositoryVersion || await s.Tasks.GetAsync(link.TaskId, token) is null)
                throw AppException.VersionConflict("The GitHub issue link changed during comment retrieval.");
            var changed = 0;
            var write = await s.GitHub.GetIssueWriteAsync(link.Id, token);
            var writingEntry = write is null ? null : StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(write.PayloadJson)?.CommentEntryId;
            foreach (var mapping in await s.GitHub.ListCommentLinksAsync(link.Id, token))
                if (mapping.DiaryEntryId != writingEntry && mapping.CommentId is { } id && targets.Contains(id) && !seen.Contains(id) && mapping.State is not (3 or 4 or 5))
                {
                    await s.GitHub.UpsertCommentLinkAsync(mapping with { State = 4, NextAttemptUtc = null, LastErrorKind = null, LastErrorMessage = null }, token);
                    changed++;
                }
            return changed;
        }, ct);
    }

    public Task<int> ApplyAsync(string issueLinkId, long expectedVersion, IReadOnlyList<GitHubComment> comments, bool complete, CancellationToken ct = default, int newCommentLimit = 100, long? expectedRepositoryVersion = null)
    {
        if (newCommentLimit is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(newCommentLimit));
        if (comments.Count > 2000) throw new ArgumentOutOfRangeException(nameof(comments));
        return store.ExecuteAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubConnectionCommands.Context(s, token);
            var link = await s.GitHub.GetIssueLinkAsync(issueLinkId, token) ?? throw AppException.NotFound("GitHub issue link was not found.");
            var repo = await s.GitHub.GetRepositoryAsync(link.RepositorySubscriptionId, token);
            var connection = await s.GitHub.GetConnectionAsync(link.ConnectionId, token);
            if (link.Version != expectedVersion || link.State != 0 || link.TaskId is null || connection is null || connection.SpaceId != space.Id || connection.ProfileId != profile.Id
                || connection.State != 0 || connection.DeletedAtUtc is not null || repo is null || repo.State != 0 || repo.DeletedAtUtc is not null || (expectedRepositoryVersion is not null && repo.Version != expectedRepositoryVersion) || await s.Tasks.GetAsync(link.TaskId, token) is null)
                throw AppException.VersionConflict("The GitHub issue link changed during comment retrieval.");
            var mappings = await s.GitHub.ListCommentLinksAsync(link.Id, token);
            var write = await s.GitHub.GetIssueWriteAsync(link.Id, token);
            var writingEntry = write is null ? null : StoredJsonFormats.ReadSnapshot<GitHubIssueWriteIntent>(write.PayloadJson)?.CommentEntryId;
            var now = clock.GetUtcNow();
            var changed = 0;
            // New imports are bounded to the newest 100, but every supplied ID participates in deletion detection.
            var newest = comments.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(newCommentLimit).Select(c => c.Id).ToHashSet();
            foreach (var fetched in comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id))
            {
                var comment = fetched;
                if (comment.Id <= 0 || comment.Body.Length > 65536) throw AppException.Validation("comment", "GitHub returned an unsupported comment.");
                var mapping = mappings.FirstOrDefault(m => m.CommentId == comment.Id);
                if (mapping is not null && mapping.DiaryEntryId == writingEntry) continue;
                var pending = mapping is null && comment.User.Id == connection.AccountId
                    ? mappings.FirstOrDefault(m => m.Origin == 1 && m.State == 1 && m.CommentId is null && comment.Body.EndsWith(GitHubCommentMarker.Suffix(m.DiaryEntryId), StringComparison.Ordinal)) : null;
                if (pending is not null)
                {
                    var pendingEntry = await s.Diary.GetIncludingDeletedAsync(pending.DiaryEntryId, token);
                    if (pendingEntry is not null)
                    {
                        var body = GitHubCommentMarker.Strip(comment.Body, pendingEntry.Id);
                        await s.GitHub.UpsertCommentLinkAsync(pending with { CommentId = comment.Id, AuthorLogin = comment.User.Login, HtmlUrl = comment.HtmlUrl,
                            BaseBody = body, BaseRemoteUpdatedUtc = comment.UpdatedAt, State = pendingEntry.DeletedAtUtc is not null ? 5 : pendingEntry.RawMarkdown == body ? 0 : 2 }, token);
                        changed++;
                    }
                    continue;
                }
                if (mapping is null && !newest.Contains(comment.Id)) continue;
                if (mapping is { State: 3 or 5 }) continue; // A local delete request or hide must not resurrect the entry.
                if (mapping is { Origin: 1 }) comment = comment with { Body = GitHubCommentMarker.Strip(comment.Body, mapping.DiaryEntryId) };
                if (mapping?.BaseRemoteUpdatedUtc > comment.UpdatedAt) continue;
                var entry = mapping is null ? null : await s.Diary.GetIncludingDeletedAsync(mapping.DiaryEntryId, token);
                if (mapping is not null && (entry is null || entry.DeletedAtUtc is not null)) continue;
                if (entry is null)
                {
                    entry = new DiaryEntryRecord { Id = ids.NewId().ToString(), TaskId = link.TaskId, AuthorProfileId = profile.Id,
                        EntryAtUtc = comment.CreatedAt, RawMarkdown = comment.Body, Revision = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
                    await s.Diary.AddAsync(entry, token);
                    mapping = new GitHubCommentLinkRecord { DiaryEntryId = entry.Id, IssueLinkId = link.Id, CommentId = comment.Id, Origin = 0 };
                    changed++;
                }
                else if (mapping!.BaseBody != comment.Body && entry.RawMarkdown != comment.Body)
                {
                    await s.Diary.UpdateAsync(entry with { Version = entry.Version + 1, RawMarkdown = comment.Body, Revision = entry.Revision + 1, EditedAtUtc = now, UpdatedAtUtc = now }, entry.Version, token);
                    changed++;
                }
                // Unchanged remote comments preserve unsent local edits and their pending state.
                var remoteChanged = mapping!.BaseBody != comment.Body;
                await s.GitHub.UpsertCommentLinkAsync(mapping with { AuthorLogin = comment.User.Login, HtmlUrl = comment.HtmlUrl, BaseBody = comment.Body,
                    BaseRemoteUpdatedUtc = comment.UpdatedAt, State = remoteChanged || mapping.State == 4 ? 0 : mapping.State, LastErrorKind = null, LastErrorMessage = null, ErrorAttempts = 0, NextAttemptUtc = null }, token);
            }
            if (complete)
            {
                var present = comments.Select(c => c.Id).ToHashSet();
                foreach (var mapping in mappings.Where(m => m.DiaryEntryId != writingEntry && m.CommentId is not null && m.State is not (3 or 4 or 5) && !present.Contains(m.CommentId.Value)))
                {
                    await s.GitHub.UpsertCommentLinkAsync(mapping with { State = 4, NextAttemptUtc = null, LastErrorKind = null, LastErrorMessage = null }, token);
                    changed++;
                }
            }
            return changed;
        }, ct);
    }
}
