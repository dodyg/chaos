using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

public sealed record GitHubSyncResult(int Created, int Updated, int CommentChanges, int Conflicts, bool MorePages);
public interface IGitHubSyncRunner
{
    Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? connectionId = null);
    Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct);
    /// <summary>The repository subscription a running full pass is working on right now; null between repositories or when idle.</summary>
    string? CurrentRepositoryId => null;
}

/// <summary>One explicit sync pass. The desktop engine owns scheduling, cancellation and replacement quiescence.</summary>
public sealed class GitHubSyncCommands(IApplicationStore store, IGitHubApi api, GitHubIssueFetcher issues, GitHubCommentFetcher comments, IIdGenerator ids, TimeProvider clock) : IGitHubSyncRunner
{
    private volatile string? _syncingRepositoryId;
    public string? CurrentRepositoryId => _syncingRepositoryId;

    /// <summary>Syncs only an existing linked issue after a local action; never advances repository feed cursors.</summary>
    public async Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct)
    {
        var context = await store.QueryAsync(async (s, token) =>
        {
            var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            var task = await s.Tasks.GetAsync(taskId, token);
            var link = await s.GitHub.FindLinkByTaskAsync(taskId, token);
            if (task is null || link is not { State: 0 or 2, LocalDirty: true } || link.NextAttemptUtc > clock.GetUtcNow()) return null;
            var repo = await s.GitHub.GetRepositoryAsync(link.RepositorySubscriptionId, token);
            var connection = await s.GitHub.GetConnectionAsync(link.ConnectionId, token);
            if (repo is not { State: 0, DeletedAtUtc: null } || connection is not { State: 0, DeletedAtUtc: null }
                || !await GitHubSpaces.CanRunAsync(s, connection, profile, token) || connection.RunnerDeviceId != await s.GitHub.GetDeviceIdAsync(token) || connection.RateLimitResetUtc > clock.GetUtcNow()
                || await s.GitHub.GetConnectionRetryAsync(connection.Id, token) > clock.GetUtcNow()) return null;
            return new { Link = link, Repo = repo, Connection = connection };
        }, ct);
        if (context is null) return new(0, 0, 0, 0, false);
        var started = clock.GetUtcNow();
        var repo = context.Repo;
        var inbound = new GitHubInboundCommands(store, ids, clock);
        var outbound = new GitHubOutboundCommands(store, api, inbound, clock);
        var diary = new GitHubOutboundComments(store, api, ids, clock);
        try
        {
            repo = await new GitHubInboundRefresh(store, api, inbound, clock).RefreshIdentityAsync(repo.Id, ct);
            await diary.RunAsync(repo.Id, false, ct, context.Link.Id);
            var recovery = await outbound.RecoverAsync(repo.Id, ct, context.Link.Id);
            var account = new GitHubAccount(context.Connection.AccountId, context.Connection.AccountLogin);
            var issue = await api.GetIssueAsync(account, repo.Owner, repo.Name, context.Link.IssueNumber, ct);
            if (issue.Id != context.Link.IssueId) throw new GitHubApiException(GitHubErrorKind.Validation);
            var pulled = await inbound.ApplyAsync(repo.Id, repo.Version, [issue], ct);
            var pulledComments = await comments.PullIssueAsync(context.Link.Id, ct);
            var pushed = await outbound.PushAsync(repo.Id, ct, context.Link.Id);
            var posted = await diary.RunAsync(repo.Id, true, ct, context.Link.Id);
            await store.ExecuteAsync(async (s, token) =>
            {
                await s.GitHub.RecordSyncOutcomeAsync(context.Connection.Id, null, started, clock.GetUtcNow(), null, null, token);
                return true;
            }, ct);
            return new(0, recovery.Updated + pulled.Updated + pushed.Updated, pulledComments.Changed + posted, recovery.Conflicts + pulled.Conflicts + pushed.Conflicts, pulledComments.MorePages);
        }
        catch (GitHubApiException error)
        {
            if (GitHubOutboundCommands.IsLinkError(error.Kind)) await GitHubOutboundCommands.RecordLinkError(store, clock, repo.Id, context.Link.Id, error.Kind, ct);
            else await store.ExecuteAsync(async (s, token) =>
            {
                await s.GitHub.RecordSyncOutcomeAsync(context.Connection.Id, null, started, clock.GetUtcNow(), error.Kind,
                    error.Kind == GitHubErrorKind.RateLimited ? error.RetryAtUtc ?? clock.GetUtcNow().AddMinutes(1) : null, token);
                return true;
            }, ct);
            throw;
        }
    }

    /// <summary>One pass of one connection: the one named, or else the Personal space's. The device must be the connection's runner.</summary>
    public async Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? connectionId = null)
    {
        var (connection, repositories) = await store.QueryAsync(async (s, token) =>
        {
            var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            GitHubConnectionRecord? connection = connectionId is null
                ? await s.GitHub.GetConnectionForSpaceAsync((await GitHubSpaces.ResolveAsync(s, null, token)).Space.Id, token)
                : await s.GitHub.GetConnectionAsync(connectionId, token);
            // A pass asked for a connection that has since gone is a stale request, not a missing setup.
            if (connection is null || connection.DeletedAtUtc is not null)
            {
                if (connectionId is null) throw AppException.NotFound("Connect to GitHub first.");
                throw AppException.VersionConflict("The GitHub connection changed before sync started. Try again.");
            }
            if (connection.RunnerDeviceId != await s.GitHub.GetDeviceIdAsync(token)) throw AppException.Validation("runner", "GitHub sync runs on another device. Select this device in GitHub settings first.");
            if (!await GitHubSpaces.CanRunAsync(s, connection, profile, token)) throw AppException.Validation("spaceId", GitHubSpaces.OwnerOnly(await GitHubSpaces.SpaceOfAsync(s, connection, token)));
            var all = await s.GitHub.ListSubscriptionsAsync(connection.Id, token);
            if (repositoryId is not null && !all.Any(r => r.Id == repositoryId)) throw AppException.NotFound("Repository subscription was not found.");
            var selected = all.Where(r => r.State == 0 && (repositoryId is null || r.Id == repositoryId)).ToArray();
            return (connection, selected);
        }, ct);
        if (connection.State == 1) throw AppException.Validation("connection", "Resume the GitHub connection first.");
        if (connection.State == 2) throw new GitHubApiException(GitHubErrorKind.Unauthorized);
        if (connection.RateLimitResetUtc > clock.GetUtcNow()) throw new GitHubApiException(GitHubErrorKind.RateLimited, connection.RateLimitResetUtc);
        if (repositories.Length == 0) throw AppException.Validation("repository", "Add or resume a repository first.");
        var started = clock.GetUtcNow(); var requests = api.RequestCount; var notModified = api.NotModifiedCount;
        var result = new GitHubSyncResult(0, 0, 0, 0, false);
        var pushed = 0;
        string? currentRepository = null;
        try
        {
            foreach (var repo in repositories)
            {
                currentRepository = repo.Id;
                _syncingRepositoryId = repo.Id;
                var refresh = new GitHubInboundRefresh(store, api, new GitHubInboundCommands(store, ids, clock), clock);
                await refresh.RefreshIdentityAsync(repo.Id, ct);
                var outboundComments = new GitHubOutboundComments(store, api, ids, clock);
                await outboundComments.RunAsync(repo.Id, false, ct);
                var outbound = new GitHubOutboundCommands(store, api, new GitHubInboundCommands(store, ids, clock), clock);
                var recovered = await outbound.RecoverAsync(repo.Id, ct);
                result = result with { Updated = result.Updated + recovered.Updated, Conflicts = result.Conflicts + recovered.Conflicts };
                var pull = await issues.PullAsync(repo.Id, ct: ct);
                result = result with { Created = result.Created + pull.Created, Updated = result.Updated + pull.Updated, Conflicts = result.Conflicts + pull.Conflicts, MorePages = result.MorePages || pull.MorePages };
                var verified = await refresh.PullAsync(repo.Id, ct: ct);
                result = result with { Updated = result.Updated + verified.Updated, Conflicts = result.Conflicts + verified.Conflicts, MorePages = result.MorePages || verified.MorePages };
                var diary = await comments.PullAsync(repo.Id, ct: ct);
                result = result with { CommentChanges = result.CommentChanges + diary.Changed, MorePages = result.MorePages || diary.MorePages };
                var sent = await outbound.PushAsync(repo.Id, ct);
                pushed += sent.Pushed;
                result = result with { Updated = result.Updated + sent.Updated, Conflicts = result.Conflicts + sent.Conflicts };
                pushed += await outboundComments.RunAsync(repo.Id, true, ct);
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
        finally { _syncingRepositoryId = null; }
        await Record(null, null);
        return result;

        Task<bool> Record(GitHubErrorKind? error, DateTimeOffset? retry) => store.ExecuteAsync(async (s, token) =>
        {
            var finished = clock.GetUtcNow();
            await s.GitHub.RecordSyncOutcomeAsync(connection.Id, error is null ? null : currentRepository, started, finished, error, retry, token);
            await s.GitHub.AddSyncRunAsync(new GitHubSyncRunRecord { Id = ids.NewId().ToString(), ConnectionId = connection.Id, StartedAtUtc = started, FinishedAtUtc = finished,
                RequestCount = api.RequestCount - requests, NotModifiedCount = api.NotModifiedCount - notModified, CreatedCount = result.Created, UpdatedCount = result.Updated, PushedCount = pushed, ConflictCount = result.Conflicts,
                ErrorKind = error is null ? null : (int)error }, token);
            return true;
        }, ct);
    }
}
