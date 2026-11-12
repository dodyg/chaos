using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

public sealed record GitHubCatalogPage(string? Etag, string? NextCursor);
public sealed record GitHubCatalogScan(string? NextCursor, string? Etag, int PagesRead, bool Finished, IReadOnlyDictionary<string, GitHubCatalogPage>? CachedPages = null, DateTimeOffset? FullReadAtUtc = null, bool Unconditional = false);
public sealed record GitHubReconcileScan(string? AfterLinkId, bool Force, DateTimeOffset? CompletedAtUtc);
public sealed record GitHubRefreshResult(int Updated, int Conflicts, bool MorePages);

/// <summary>Catalog definitions and bounded verification of known issue identities, including missing and transferred issues.</summary>
public sealed class GitHubInboundRefresh(IApplicationStore store, IGitHubApi api, GitHubInboundCommands inbound, TimeProvider clock)
{
    public async Task<GitHubRepositoryRecord> RefreshIdentityAsync(string repositoryId, CancellationToken ct)
    {
        var (repo, connection) = await Context(repositoryId, ct);
        var remote = await api.GetRepositoryAsync(new(connection.AccountId, connection.AccountLogin), repo.Owner, repo.Name, ct);
        if (remote.Id != repo.RepositoryId || remote.Owner is null || !GitHubIssueAddress.Segment(remote.Owner.Login) || !GitHubIssueAddress.Segment(remote.Name)
            || !GitHubIssueAddress.TryParse(remote.HtmlUrl.TrimEnd('/') + "/issues/1", out var remoteAddress) || !remoteAddress.IsIn(remote.Owner.Login, remote.Name)) throw new GitHubApiException(GitHubErrorKind.Validation);
        if (!remote.HasIssues) throw new GitHubApiException(GitHubErrorKind.Gone);
        return await store.ExecuteAsync(async (s, token) =>
        {
            if (!await s.GitHub.RefreshRepositoryIdentityAsync(repo.Id, repo.Version, remote, token)) throw Changed();
            return (await s.GitHub.GetRepositoryAsync(repo.Id, token))!;
        }, ct);
    }

    public async Task<GitHubRefreshResult> PullAsync(string repositoryId, int pageLimit = 20, int linkLimit = 20, CancellationToken ct = default)
    {
        if (pageLimit is < 1 or > 100 || linkLimit is < 1 or > 199) throw new ArgumentOutOfRangeException(nameof(pageLimit));
        var (repo, connection) = await Context(repositoryId, ct);
        var account = new GitHubAccount(connection.AccountId, connection.AccountLogin);
        var labels = await Catalog("labels");
        var milestones = await Catalog("milestones");
        var checkpoint = await store.QueryAsync((s, token) => s.GitHub.GetCheckpointAsync(repo.Id, "reconcile", token), ct);
        var previous = checkpoint?.RepositoryVersion == repo.Version ? StoredJsonFormats.ReadSnapshot<GitHubReconcileScan>(checkpoint.PayloadJson) : null;
        var force = labels.Changed || milestones.Changed || previous?.Force == true || previous?.CompletedAtUtc is null || previous.CompletedAtUtc <= clock.GetUtcNow().AddDays(-1);
        var links = await store.QueryAsync((s, token) => s.GitHub.ListRepositoryLinksAsync(repo.Id, previous?.AfterLinkId, linkLimit + 1, token), ct);
        int updated = 0, conflicts = 0;
        foreach (var link in links.Take(linkLimit))
        {
            if (link.State == 4) continue;
            var task = link.TaskId is { } id ? await store.QueryAsync((s, token) => s.Tasks.GetAsync(id, token), ct) : null;
            if (task is null)
            {
                if (link.State != 3) await SaveLink(link, repo, row => row with { State = 3, LocalDirty = false, LocalDirtySinceUtc = null, NextAttemptUtc = null }, ct);
                continue;
            }
            GitHubIssueRead read;
            try { read = await api.ReadIssueAsync(account, repo.Owner, repo.Name, link.IssueNumber, force || link.State != 0 || link.LocalDirty ? null : link.IssueEtag, ct); }
            catch (GitHubApiException error) when (error.Kind is GitHubErrorKind.NotFound or GitHubErrorKind.Gone or GitHubErrorKind.Moved)
            {
                if (error.Kind == GitHubErrorKind.NotFound)
                {
                    try { read = await api.ReadIssueAsync(account, repo.Owner, repo.Name, link.IssueNumber, null, ct); }
                    catch (GitHubApiException confirmed) when (confirmed.Kind is GitHubErrorKind.NotFound or GitHubErrorKind.Gone)
                    { await Unavailable(link, repo, confirmed.Kind, null, ct); continue; }
                }
                else { await Unavailable(link, repo, error.Kind, GitHubIssueAddress.TryParse(error.MovedToUrl, out var moved) ? moved.WebUrl : null, ct); continue; }
            }
            if (read.NotModified)
            {
                if (force || link.State != 0 || link.LocalDirty || link.IssueEtag is null) throw new GitHubApiException(GitHubErrorKind.Server);
                continue;
            }
            var issue = read.Issue ?? throw new GitHubApiException(GitHubErrorKind.Server);
            if (issue.Id != link.IssueId || !GitHubIssueAddress.TryParse(issue.HtmlUrl, out var address) || address.Number != issue.Number) throw new GitHubApiException(GitHubErrorKind.Validation);
            var target = repo;
            if (!address.IsIn(repo.Owner, repo.Name))
            {
                var subscriptions = await store.QueryAsync((s, token) => s.GitHub.ListSubscriptionsAsync(connection.Id, token), ct);
                target = subscriptions.FirstOrDefault(r => r.State == 0 && address.IsIn(r.Owner, r.Name));
                if (target is null)
                {
                    GitHubRepository? destination = null;
                    try { destination = await api.GetRepositoryAsync(account, address.Owner, address.Repository, ct); }
                    catch (GitHubApiException inaccessible) when (inaccessible.Kind is GitHubErrorKind.NotFound or GitHubErrorKind.Forbidden or GitHubErrorKind.Gone) { await Unavailable(link, repo, GitHubErrorKind.Moved, address.WebUrl, ct); continue; }
                    target = subscriptions.FirstOrDefault(r => r.State == 0 && r.RepositoryId == destination.Id);
                    if (target is not null) target = await RefreshIdentityAsync(target.Id, ct);
                }
                if (target is null) { await Unavailable(link, repo, GitHubErrorKind.Moved, address.WebUrl, ct); continue; }
            }
            var applied = await inbound.ApplyAsync(target.Id, target.Version, [issue], ct);
            updated += applied.Updated; conflicts += applied.Conflicts;
            await SaveLink(link, target, row => row with { IssueEtag = read.Etag }, ct, false);
        }
        var moreLinks = links.Count > linkLimit;
        var after = moreLinks ? links[linkLimit - 1].Id : null;
        await store.ExecuteAsync(async (s, token) =>
        {
            if (!await s.GitHub.SaveCheckpointAsync(new(repo.Id, "reconcile", repo.Version, StoredJsonFormats.Snapshot(new GitHubReconcileScan(after, moreLinks && force, moreLinks ? previous?.CompletedAtUtc : clock.GetUtcNow())), clock.GetUtcNow()), token)) throw Changed();
            return true;
        }, ct);
        return new(updated, conflicts, labels.More || milestones.More || moreLinks);

        async Task<(bool More, bool Changed)> Catalog(string kind)
        {
            var row = await store.QueryAsync((s, token) => s.GitHub.GetCheckpointAsync(repo.Id, kind, token), ct);
            var saved = row?.RepositoryVersion == repo.Version ? StoredJsonFormats.ReadSnapshot<GitHubCatalogScan>(row.PayloadJson) : null;
            var fullRead = saved?.FullReadAtUtc is null || saved.FullReadAtUtc <= clock.GetUtcNow().AddDays(-1);
            var scan = saved is { Finished: false } ? saved : new GitHubCatalogScan(null, null, 0, false, fullRead ? null : saved?.CachedPages, saved?.FullReadAtUtc, fullRead);
            var changed = false;
            for (var pageIndex = 0; pageIndex < pageLimit; pageIndex++)
            {
                bool notModified; string? next; string? etag;
                var pageKey = scan.NextCursor ?? "$first";
                var cached = scan.CachedPages?.GetValueOrDefault(pageKey);
                if (kind == "labels")
                {
                    var page = await api.ListLabelsAsync(account, repo.Owner, repo.Name, scan.NextCursor, cached?.Etag, ct);
                    (notModified, next, etag) = (page.NotModified, page.NextCursor, page.Etag);
                    if (!notModified) await inbound.ApplyCatalogAsync(repo.Id, repo.Version, page.Items, [], ct);
                }
                else
                {
                    var page = await api.ListMilestonesAsync(account, repo.Owner, repo.Name, scan.NextCursor, cached?.Etag, ct);
                    (notModified, next, etag) = (page.NotModified, page.NextCursor, page.Etag);
                    if (!notModified) await inbound.ApplyCatalogAsync(repo.Id, repo.Version, [], page.Items, ct);
                }
                if (notModified && cached?.Etag is null) throw new GitHubApiException(GitHubErrorKind.Server);
                if (notModified) next ??= cached!.NextCursor;
                if (next is not null && next == scan.NextCursor) throw new GitHubApiException(GitHubErrorKind.Server);
                changed |= !notModified;
                var pages = new Dictionary<string, GitHubCatalogPage>(scan.CachedPages ?? new Dictionary<string, GitHubCatalogPage>(), StringComparer.Ordinal) { [pageKey] = new(notModified ? cached!.Etag : etag, next) };
                while (pages.Count > 20 || Encoding.UTF8.GetByteCount(StoredJsonFormats.Snapshot(pages)) > 32768) pages.Remove(pages.Keys.First());
                scan = scan with { NextCursor = next, Finished = next is null, PagesRead = scan.PagesRead + 1, Etag = notModified ? cached!.Etag : scan.PagesRead == 0 && next is null ? etag : null, CachedPages = pages,
                    FullReadAtUtc = next is null && scan.Unconditional ? clock.GetUtcNow() : scan.FullReadAtUtc };
                await store.ExecuteAsync(async (s, token) =>
                {
                    if (!await s.GitHub.SaveCheckpointAsync(new(repo.Id, kind, repo.Version, StoredJsonFormats.Snapshot(scan), clock.GetUtcNow()), token)) throw Changed();
                    if (scan.Finished && !await s.GitHub.SaveCatalogEtagAsync(repo.Id, repo.Version, kind, scan.Etag, token)) throw Changed();
                    return true;
                }, ct);
                if (scan.Finished) break;
            }
            return (!scan.Finished, changed);
        }
    }

    private Task<(GitHubRepositoryRecord Repo, GitHubConnectionRecord Connection)> Context(string id, CancellationToken ct) => store.QueryAsync(async (s, token) =>
    {
        var (profile, space) = await GitHubConnectionCommands.Context(s, token);
        var repo = await s.GitHub.GetRepositoryAsync(id, token) ?? throw AppException.NotFound("Repository subscription was not found.");
        var connection = await s.GitHub.GetConnectionAsync(repo.ConnectionId, token);
        if (repo.State != 0 || repo.DeletedAtUtc is not null || connection is null || connection.State != 0 || connection.DeletedAtUtc is not null || connection.ProfileId != profile.Id || connection.SpaceId != space.Id) throw Changed();
        return (repo, connection);
    }, ct);

    private Task<bool> SaveLink(GitHubIssueLinkRecord fetched, GitHubRepositoryRecord expected, Func<GitHubIssueLinkRecord, GitHubIssueLinkRecord> update, CancellationToken ct, bool requireVersion = true) => store.ExecuteAsync(async (s, token) =>
    {
        var row = await s.GitHub.GetIssueLinkAsync(fetched.Id, token) ?? throw Changed();
        var repo = await s.GitHub.GetRepositoryAsync(row.RepositorySubscriptionId, token);
        var connection = await s.GitHub.GetConnectionAsync(row.ConnectionId, token);
        if (row.RepositorySubscriptionId != expected.Id || repo is null || repo.Version != expected.Version || (requireVersion && row.Version != fetched.Version) || repo.State != 0 || repo.DeletedAtUtc is not null || connection is null || connection.State != 0 || connection.DeletedAtUtc is not null || row.State == 4) throw Changed();
        var next = update(row);
        if (next != row && await s.GitHub.UpdateIssueLinkAsync(next with { UpdatedAtUtc = clock.GetUtcNow() }, row.Version, token) is null) throw Changed();
        return true;
    }, ct);
    private Task<bool> Unavailable(GitHubIssueLinkRecord link, GitHubRepositoryRecord repo, GitHubErrorKind kind, string? movedUrl, CancellationToken ct) =>
        SaveLink(link, repo, row => row with { State = 2, LastErrorKind = (int)kind, LastErrorMessage = kind.ToString(), HtmlUrl = movedUrl ?? row.HtmlUrl }, ct);
    private static AppException Changed() => AppException.VersionConflict("The GitHub subscription changed during refresh. Run sync again.");
}
