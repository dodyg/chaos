using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

public sealed record GitHubCommentScan(DateTimeOffset Since, string? NextCursor, DateTimeOffset Watermark, string? Etag, int PagesRead, bool Finished);
public sealed record GitHubCommentRotation(string? AfterLinkId);
public sealed record GitHubCommentState(bool InitialDone = false, bool SeekingTail = true, string? InitialCursor = null, int Remaining = 100,
    DateTimeOffset? LastCheckedUtc = null, int LastKnownCount = -1, string? ListEtag = null, bool Checking = false, string? CheckCursor = null,
    int CheckPages = 0, long[]? Targets = null, long[]? Seen = null, long? AfterCommentId = null);
public sealed record GitHubCommentFetchResult(int Changed, int PagesRead, bool MorePages);

/// <summary>Incremental comments plus newest-100 imports and bounded daily full-list deletion checks. Checkpoints contain IDs, never comment bodies.</summary>
public sealed class GitHubCommentFetcher(IApplicationStore store, IGitHubApi api, GitHubInboundComments inbound, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<GitHubCommentFetchResult> PullAsync(string repositoryId, int pageLimit = 20, CancellationToken ct = default)
    {
        if (pageLimit is < 2 or > 100) throw new ArgumentOutOfRangeException(nameof(pageLimit));
        await _gate.WaitAsync(ct);
        try { return await PullCore(repositoryId, pageLimit, ct); }
        finally { _gate.Release(); }
    }

    private async Task<GitHubCommentFetchResult> PullCore(string repositoryId, int pageLimit, CancellationToken ct)
    {
        var (repo, account, checkpoint, rotationRow) = await store.QueryAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubConnectionCommands.Context(s, token);
            var repo = await s.GitHub.GetRepositoryAsync(repositoryId, token) ?? throw AppException.NotFound("Repository subscription was not found.");
            var c = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token);
            if (repo.DeletedAtUtc is not null || repo.State != 0 || c is null || c.State != 0 || c.DeletedAtUtc is not null || c.ProfileId != profile.Id || c.SpaceId != space.Id) throw Changed();
            return (repo, new GitHubAccount(c.AccountId, c.AccountLogin), await s.GitHub.GetCheckpointAsync(repo.Id, "comments", token), await s.GitHub.GetCheckpointAsync(repo.Id, "comment-check", token));
        }, ct);
        var previous = checkpoint?.RepositoryVersion == repo.Version ? StoredJsonFormats.ReadSnapshot<GitHubCommentScan>(checkpoint.PayloadJson) : null;
        var since = repo.CommentsCursorUtc?.AddSeconds(-1) ?? previous?.Since ?? clock.GetUtcNow();
        var scan = previous is { Finished: false } ? previous : new(since, null, repo.CommentsCursorUtc ?? since, previous?.Since == since ? previous?.Etag : null, 0, false);
        int pages = 0, changed = 0;
        for (; pages < Math.Max(1, pageLimit / 2);)
        {
            var page = await api.ListCommentsAsync(account, repo.Owner, repo.Name, scan.Since, null, scan.NextCursor, scan.NextCursor is null ? scan.Etag : null, ct);
            pages++;
            if (page.NotModified)
            {
                if (scan.NextCursor is not null || scan.Etag is null) throw new GitHubApiException(GitHubErrorKind.Server);
                scan = scan with { Finished = true }; await SaveFeed(repo, scan, ct); break;
            }
            CheckCursor(scan.NextCursor, page.NextCursor);
            foreach (var group in page.Items.GroupBy(c => IssueNumber(repo, c.IssueUrl)))
            {
                var link = await store.QueryAsync((s, token) => s.GitHub.FindLinkByNumberAsync(repo.Id, group.Key, token), ct);
                if (link is not { State: 0, TaskId: not null }) continue;
                try { changed += await inbound.ApplyAsync(link.Id, link.Version, group.DistinctBy(c => c.Id).ToArray(), false, ct, 2000, repo.Version); }
                catch (AppException error) when (error.Kind == AppErrorKind.Validation) { await GitHubOutboundCommands.RecordLinkError(store, clock, repo.Id, link.Id, GitHubErrorKind.Validation, ct); }
            }
            scan = scan with { NextCursor = page.NextCursor, PagesRead = scan.PagesRead + 1, Finished = page.NextCursor is null,
                Watermark = page.Items.Aggregate(scan.Watermark, (value, c) => c.UpdatedAt > value ? c.UpdatedAt : value), Etag = scan.PagesRead == 0 && page.NextCursor is null ? page.Etag : null };
            await SaveFeed(repo, scan, ct);
            if (scan.Finished) break;
        }
        var after = rotationRow?.RepositoryVersion == repo.Version ? StoredJsonFormats.ReadSnapshot<GitHubCommentRotation>(rotationRow.PayloadJson)?.AfterLinkId : null;
        var pending = !scan.Finished;
        var reachedEnd = false;
        for (var inspected = 0; inspected < 200 && pages < pageLimit; inspected++)
        {
            var link = (await store.QueryAsync((s, token) => s.GitHub.ListRepositoryLinksAsync(repo.Id, after, 1, token), ct)).SingleOrDefault();
            if (link is null) { reachedEnd = true; after = null; break; }
            if (link.State != 0 || link.TaskId is null || link.NextAttemptUtc > clock.GetUtcNow() || !await store.QueryAsync(async (s, token) => await s.Tasks.GetAsync(link.TaskId, token) is not null, ct)) { after = link.Id; continue; }
            try
            {
            var row = await store.QueryAsync((s, token) => s.GitHub.GetCommentCheckpointAsync(link.Id, token), ct);
            var state = row?.RepositoryVersion == repo.Version ? StoredJsonFormats.ReadSnapshot<GitHubCommentState>(row.PayloadJson) ?? new() : new GitHubCommentState();
            var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson) ?? throw Changed();
            var mappings = await store.QueryAsync((s, token) => s.GitHub.ListCommentLinksAsync(link.Id, token), ct);
            if (!state.InitialDone && baseline.Comments == 0 && mappings.Count == 0)
            {
                state = state with { InitialDone = true, LastCheckedUtc = clock.GetUtcNow(), LastKnownCount = 0 };
                await SaveIssue(repo, link.Id, state, ct);
            }
            var activeCount = mappings.Count(m => m.CommentId is not null && m.State is not (3 or 4 or 5));
            var due = state.Checking || state.LastCheckedUtc is null || state.LastCheckedUtc <= clock.GetUtcNow().AddDays(-1) || (baseline.Comments != state.LastKnownCount && baseline.Comments < activeCount);
            while (pages < pageLimit && (!state.InitialDone || due))
            {
                // A preceding issue scan may have advanced the link version; configuration remains guarded independently.
                link = await store.QueryAsync((s, token) => s.GitHub.GetIssueLinkAsync(link.Id, token), ct) ?? throw Changed();
                if (!state.InitialDone)
                {
                    var page = await api.ListCommentsAsync(account, repo.Owner, repo.Name, null, link.IssueNumber, state.InitialCursor, null, ct); pages++;
                    if (page.NotModified) throw new GitHubApiException(GitHubErrorKind.Server);
                    if (state.SeekingTail && page.NextCursor is not null)
                    {
                        var next = page.LastCursor ?? page.NextCursor; CheckCursor(state.InitialCursor, next);
                        state = state with { InitialCursor = next };
                    }
                    else
                    {
                        var selected = page.Items.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).DistinctBy(c => c.Id).Take(state.Remaining).ToArray();
                        var complete = state.SeekingTail && state.InitialCursor is null && page.NextCursor is null && page.Items.Count <= 100;
                        changed += await inbound.ApplyAsync(link.Id, link.Version, selected, complete, ct, state.Remaining, repo.Version);
                        var remaining = state.Remaining - selected.Length;
                        var done = remaining == 0 || page.PreviousCursor is null;
                        CheckCursor(state.InitialCursor, done ? null : page.PreviousCursor);
                        state = state with { InitialDone = done, SeekingTail = false, Remaining = remaining, InitialCursor = done ? null : page.PreviousCursor,
                            LastCheckedUtc = complete ? clock.GetUtcNow() : null, LastKnownCount = complete ? baseline.Comments : -1, ListEtag = complete ? page.Etag : null };
                    }
                    await SaveIssue(repo, link.Id, state, ct);
                    due = state.InitialDone && state.LastCheckedUtc is null;
                    continue;
                }
                if (!state.Checking)
                {
                    var targets = mappings.Where(m => m.CommentId is not null && m.State is not (3 or 4 or 5) && (state.AfterCommentId is null || m.CommentId > state.AfterCommentId)).Select(m => m.CommentId!.Value).Order().Take(200).ToArray();
                    state = state with { Checking = true, CheckCursor = null, CheckPages = 0, Targets = targets, Seen = [] };
                }
                var check = await api.ListCommentsAsync(account, repo.Owner, repo.Name, null, link.IssueNumber, state.CheckCursor, state.CheckCursor is null ? state.ListEtag : null, ct); pages++;
                if (check.NotModified)
                {
                    if (state.CheckCursor is not null || state.ListEtag is null) throw new GitHubApiException(GitHubErrorKind.Server);
                    state = state with { Checking = false, LastCheckedUtc = clock.GetUtcNow(), LastKnownCount = baseline.Comments, AfterCommentId = null, Targets = null, Seen = null };
                    await SaveIssue(repo, link.Id, state, ct); due = false; break;
                }
                CheckCursor(state.CheckCursor, check.NextCursor);
                changed += await inbound.ApplyAsync(link.Id, link.Version, check.Items.DistinctBy(c => c.Id).ToArray(), false, ct, 0, repo.Version);
                var targetSet = state.Targets!.ToHashSet();
                var seen = state.Seen!.Concat(check.Items.Where(c => targetSet.Contains(c.Id)).Select(c => c.Id)).Distinct().ToArray();
                state = state with { CheckCursor = check.NextCursor, CheckPages = state.CheckPages + 1, Seen = seen };
                if (check.NextCursor is null)
                {
                    changed += await inbound.MarkMissingAsync(link.Id, link.Version, repo.Version, targetSet, seen.ToHashSet(), ct);
                    var targets = state.Targets ?? throw Changed();
                    var lastId = targets.Length == 0 ? state.AfterCommentId : targets[^1];
                    var moreTargets = mappings.Any(m => m.CommentId > lastId && m.State is not (3 or 4 or 5));
                    state = state with { Checking = false, LastCheckedUtc = moreTargets ? null : clock.GetUtcNow(), LastKnownCount = baseline.Comments, AfterCommentId = moreTargets ? lastId : null,
                        ListEtag = state.CheckPages == 1 ? check.Etag : null, Targets = null, Seen = null };
                    due = moreTargets;
                }
                await SaveIssue(repo, link.Id, state, ct);
            }
            if (!state.InitialDone || due) { pending = true; break; }
            after = link.Id;
            }
            catch (GitHubApiException error) when (GitHubOutboundCommands.IsLinkError(error.Kind))
            {
                await GitHubOutboundCommands.RecordLinkError(store, clock, repo.Id, link.Id, error.Kind, ct);
                after = link.Id;
            }
            catch (AppException error) when (error.Kind == AppErrorKind.Validation)
            {
                await GitHubOutboundCommands.RecordLinkError(store, clock, repo.Id, link.Id, GitHubErrorKind.Validation, ct);
                after = link.Id;
            }
        }
        if (!reachedEnd) pending = true;
        await store.ExecuteAsync(async (s, token) =>
        {
            if (!await s.GitHub.SaveCheckpointAsync(new(repo.Id, "comment-check", repo.Version, StoredJsonFormats.Snapshot(new GitHubCommentRotation(after)), clock.GetUtcNow()), token)) throw Changed();
            return true;
        }, ct);
        return new(changed, pages, pending);
    }

    private Task<bool> SaveIssue(GitHubRepositoryRecord repo, string id, GitHubCommentState state, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        if (!await s.GitHub.SaveCommentCheckpointAsync(new(id, repo.Version, StoredJsonFormats.Snapshot(state), clock.GetUtcNow()), token)) throw Changed();
        return true;
    }, ct);
    private Task<bool> SaveFeed(GitHubRepositoryRecord repo, GitHubCommentScan state, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        if (!await s.GitHub.SaveCheckpointAsync(new(repo.Id, "comments", repo.Version, StoredJsonFormats.Snapshot(state), clock.GetUtcNow()), token)) throw Changed();
        if (state.Finished && !await s.GitHub.CompleteCommentScanAsync(repo.Id, repo.Version, state.Watermark, state.Etag, token)) throw Changed();
        return true;
    }, ct);
    private static long IssueNumber(GitHubRepositoryRecord repo, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new GitHubApiException(GitHubErrorKind.Server);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "repos" || !parts[1].Equals(repo.Owner, StringComparison.OrdinalIgnoreCase) || !parts[2].Equals(repo.Name, StringComparison.OrdinalIgnoreCase) || parts[3] != "issues" || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) throw new GitHubApiException(GitHubErrorKind.Server);
        return number;
    }
    private static void CheckCursor(string? current, string? next) { if (next is not null && current == next) throw new GitHubApiException(GitHubErrorKind.Server); }
    private static AppException Changed() => AppException.VersionConflict("GitHub settings or the issue changed during comment retrieval. Run sync again.");
    public void Dispose() => _gate.Dispose();
}
