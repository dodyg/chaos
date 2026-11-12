using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

public sealed record GitHubSyncResult(int Created, int Updated, int CommentChanges, int Conflicts, bool MorePages);
public interface IGitHubSyncRunner
{
    Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null);
}

/// <summary>One explicit inbound pass. The desktop engine owns scheduling, cancellation and replacement quiescence.</summary>
public sealed class GitHubSyncCommands(IApplicationStore store, IGitHubApi api, GitHubIssueFetcher issues, GitHubCommentFetcher comments, IIdGenerator ids, TimeProvider clock) : IGitHubSyncRunner
{
    public async Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null)
    {
        var (connection, repositories) = await store.QueryAsync(async (s, token) =>
        {
            var (_, space) = await GitHubConnectionCommands.Context(s, token);
            var connection = await s.GitHub.GetConnectionForSpaceAsync(space.Id, token) ?? throw AppException.NotFound("Connect to GitHub first.");
            var all = await s.GitHub.ListSubscriptionsAsync(connection.Id, token);
            if (repositoryId is not null && !all.Any(r => r.Id == repositoryId)) throw AppException.NotFound("Repository subscription was not found.");
            var selected = all.Where(r => r.State == 0 && (repositoryId is null || r.Id == repositoryId)).ToArray();
            if (expectedConnectionId is not null && connection.Id != expectedConnectionId) throw AppException.VersionConflict("The GitHub connection changed before sync started. Try again.");
            return (connection, selected);
        }, ct);
        if (connection.State == 1) throw AppException.Validation("connection", "Resume the GitHub connection first.");
        if (connection.State == 2) throw new GitHubApiException(GitHubErrorKind.Unauthorized);
        if (connection.RateLimitResetUtc > clock.GetUtcNow()) throw new GitHubApiException(GitHubErrorKind.RateLimited, connection.RateLimitResetUtc);
        if (repositories.Length == 0) throw AppException.Validation("repository", "Add or resume a repository first.");
        var started = clock.GetUtcNow(); var requests = api.RequestCount; var notModified = api.NotModifiedCount;
        var result = new GitHubSyncResult(0, 0, 0, 0, false);
        string? currentRepository = null;
        try
        {
            foreach (var repo in repositories)
            {
                currentRepository = repo.Id;
                var refresh = new GitHubInboundRefresh(store, api, new GitHubInboundCommands(store, ids, clock), clock);
                await refresh.RefreshIdentityAsync(repo.Id, ct);
                var pull = await issues.PullAsync(repo.Id, ct: ct);
                result = result with { Created = result.Created + pull.Created, Updated = result.Updated + pull.Updated, Conflicts = result.Conflicts + pull.Conflicts, MorePages = result.MorePages || pull.MorePages };
                var verified = await refresh.PullAsync(repo.Id, ct: ct);
                result = result with { Updated = result.Updated + verified.Updated, Conflicts = result.Conflicts + verified.Conflicts, MorePages = result.MorePages || verified.MorePages };
                var diary = await comments.PullAsync(repo.Id, ct: ct);
                result = result with { CommentChanges = result.CommentChanges + diary.Changed, MorePages = result.MorePages || diary.MorePages };
            }
        }
        catch (GitHubApiException error)
        {
            await Record(error.Kind, error.Kind == GitHubErrorKind.RateLimited ? error.RetryAtUtc ?? clock.GetUtcNow().AddMinutes(1) : null);
            throw;
        }
        catch (AppException)
        {
            await Record(GitHubErrorKind.Validation, null);
            throw;
        }
        await Record(null, null);
        return result;

        Task<bool> Record(GitHubErrorKind? error, DateTimeOffset? retry) => store.ExecuteAsync(async (s, token) =>
        {
            var finished = clock.GetUtcNow();
            await s.GitHub.RecordSyncOutcomeAsync(connection.Id, error is null ? null : currentRepository, started, finished, error, retry, token);
            await s.GitHub.AddSyncRunAsync(new GitHubSyncRunRecord { Id = ids.NewId().ToString(), ConnectionId = connection.Id, StartedAtUtc = started, FinishedAtUtc = finished,
                RequestCount = api.RequestCount - requests, NotModifiedCount = api.NotModifiedCount - notModified, CreatedCount = result.Created, UpdatedCount = result.Updated, ConflictCount = result.Conflicts,
                ErrorKind = error is null ? null : (int)error }, token);
            return true;
        }, ct);
    }
}
