using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

public sealed record GitHubIssueScan(DateTimeOffset? Since, string? NextCursor, DateTimeOffset? Watermark, string? Etag, int PagesRead, bool Finished, DateTimeOffset StartedAtUtc, string? State = null, bool FirstImport = false);
public sealed record GitHubImportBackup(string FileName);
public sealed record GitHubIssueFetchResult(int Created, int Updated, int Conflicts, int PagesRead, bool MorePages, bool NotModified);

/// <summary>Bounded, restartable issue retrieval. Only fetched snapshots enter the inbound transactions.</summary>
public sealed class GitHubIssueFetcher(IApplicationStore store, IGitHubApi api, GitHubInboundCommands inbound,
    Func<CancellationToken, Task<string>> preImportBackup, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<GitHubIssueFetchResult> PullAsync(string repositoryId, int pageLimit = 20, CancellationToken ct = default)
    {
        if (pageLimit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageLimit));
        await _gate.WaitAsync(ct);
        try { return await PullCore(repositoryId, pageLimit, ct); }
        finally { _gate.Release(); }
    }

    private async Task<GitHubIssueFetchResult> PullCore(string repositoryId, int pageLimit, CancellationToken ct)
    {
        var (repo, connection, checkpoint) = await store.QueryAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubConnectionCommands.Context(s, token);
            var repo = await s.GitHub.GetRepositoryAsync(repositoryId, token) ?? throw AppException.NotFound("Repository subscription was not found.");
            var connection = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token);
            if (repo.State != 0 || repo.DeletedAtUtc is not null || connection is null || connection.State != 0 || connection.DeletedAtUtc is not null || connection.ProfileId != profile.Id || connection.SpaceId != space.Id)
                throw AppException.VersionConflict("The GitHub subscription is not active.");
            return (repo, connection, await s.GitHub.GetCheckpointAsync(repo.Id, "issues", token));
        }, ct);
        var previous = checkpoint?.RepositoryVersion == repo.Version ? StoredJsonFormats.ReadSnapshot<GitHubIssueScan>(checkpoint.PayloadJson) : null;
        if (repo.InitialImportDoneUtc is null && previous is { FirstImport: false }) previous = null;
        var since = repo.InitialImportDoneUtc is null ? null : repo.IssuesCursorUtc?.AddSeconds(-1); // API timestamps have second precision; overlap protects boundary updates.
        var scan = previous is { Finished: false } ? previous : new GitHubIssueScan(since, null, repo.IssuesCursorUtc,
            previous?.Since == since && previous?.State is null ? previous?.Etag : null, 0, false, clock.GetUtcNow(), repo.InitialImportDoneUtc is null ? "open" : null, repo.InitialImportDoneUtc is null);
        var account = new GitHubAccount(connection.AccountId, connection.AccountLogin);
        var filter = GitHubIssueScope.Parse(repo.FilterJson);
        int created = 0, updated = 0, conflicts = 0;
        for (var pageIndex = 0; pageIndex < pageLimit; pageIndex++)
        {
            var conditional = scan.NextCursor is null ? scan.Etag : null;
            var page = await api.ListIssuesAsync(account, repo.Owner, repo.Name, scan.Since, scan.NextCursor, conditional, ct, scan.State);
            if (page.NotModified)
            {
                if (conditional is null || scan.NextCursor is not null) throw new GitHubApiException(GitHubErrorKind.Server);
                scan = scan with { Finished = true, Watermark = scan.Watermark ?? scan.StartedAtUtc };
                await Save(repo, scan, ct);
                return new(created, updated, conflicts, pageIndex + 1, false, true);
            }
            if (page.NextCursor is not null && page.NextCursor == scan.NextCursor) throw new GitHubApiException(GitHubErrorKind.Server);
            if (repo.InitialImportDoneUtc is null && page.Items.Count > 0)
            {
                var needsBackup = await store.QueryAsync(async (s, token) =>
                {
                    if (await s.GitHub.GetCheckpointAsync(repo.Id, "initial-import-backup", token) is not null) return false;
                    foreach (var issue in page.Items)
                    {
                        var linked = await s.GitHub.FindLinkByIssueAsync(connection.Id, issue.Id, token) is not null;
                        if (GitHubIssueScope.Matches(filter, issue, connection.AccountId, clock.GetUtcNow(), linked)) return true;
                    }
                    return false;
                }, ct);
                if (needsBackup)
                {
                    // Conservatively protect even small first imports; this remains a pre-import backup if later pages exceed 50.
                    var file = await preImportBackup(ct);
                    if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file)) throw new InvalidOperationException("The backup provider returned an invalid file name.");
                    await store.ExecuteAsync(async (s, token) =>
                    {
                        if (!await s.GitHub.SaveCheckpointAsync(new(repo.Id, "initial-import-backup", repo.Version, StoredJsonFormats.Snapshot(new GitHubImportBackup(file)), clock.GetUtcNow()), token)) throw Changed();
                        return true;
                    }, ct);
                }
            }
            foreach (var batch in page.Items.Chunk(50))
            {
                var result = await inbound.ApplyAsync(repo.Id, repo.Version, batch, ct);
                created += result.Created; updated += result.Updated; conflicts += result.Conflicts;
            }
            var watermark = scan.Watermark;
            foreach (var issue in page.Items) if (watermark is null || issue.UpdatedAt > watermark) watermark = issue.UpdatedAt;
            scan = scan with { NextCursor = page.NextCursor, Watermark = watermark, PagesRead = scan.PagesRead + 1, Finished = page.NextCursor is null,
                Etag = scan.PagesRead == 0 && page.NextCursor is null ? page.Etag : null };
            if (scan.Finished && scan.FirstImport && scan.State == "open" && filter.IncludeClosedDays > 0)
                scan = scan with { State = "closed", Since = scan.StartedAtUtc.AddDays(-filter.IncludeClosedDays), NextCursor = null, Etag = null, PagesRead = 0, Finished = false };
            if (scan.Finished) scan = scan with { Watermark = scan.FirstImport && (scan.Watermark is null || scan.Watermark < scan.StartedAtUtc) ? scan.StartedAtUtc : scan.Watermark ?? scan.StartedAtUtc };
            await Save(repo, scan, ct);
            if (scan.Finished) return new(created, updated, conflicts, pageIndex + 1, false, false);
        }
        return new(created, updated, conflicts, pageLimit, true, false);
    }

    private Task<bool> Save(GitHubRepositoryRecord repo, GitHubIssueScan scan, CancellationToken ct) => store.ExecuteAsync(async (s, token) =>
    {
        if (!await s.GitHub.SaveCheckpointAsync(new(repo.Id, "issues", repo.Version, StoredJsonFormats.Snapshot(scan), clock.GetUtcNow()), token)) throw Changed();
        if (scan.Finished && !await s.GitHub.CompleteIssueScanAsync(repo.Id, repo.Version, scan.Watermark!.Value, scan.Etag, clock.GetUtcNow(), token)) throw Changed();
        return true;
    }, ct);
    private static AppException Changed() => AppException.VersionConflict("Repository settings changed during the fetch. Run sync again.");
    public void Dispose() => _gate.Dispose();
}
