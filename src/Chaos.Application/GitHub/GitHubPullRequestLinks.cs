using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;

namespace Chaos.Application.GitHub;

/// <summary>
/// Linked pull requests (GITHUB.md G9, PLAN3 10.6). GitHub is asked in batches of at most 50 issue node IDs, outside
/// any transaction, and the answer replaces the rows of each linked issue. Pull requests are supplementary: a failed
/// read leaves the rows as they were, and the next pass asks again.
/// </summary>
public sealed class GitHubPullRequestLinks(IApplicationStore store, IGitHubApi api, GitHubInboundCommands inbound, TimeProvider clock)
{
    /// <summary>The pull requests of the issues a fetch just applied, read while their links are fresh.</summary>
    public async Task RefreshChangedAsync(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, IReadOnlyList<GitHubIssue> issues, CancellationToken ct)
    {
        if (issues.Count == 0) return;
        var links = await store.QueryAsync(async (s, token) =>
        {
            var result = new List<(string LinkId, string NodeId)>();
            foreach (var issue in issues)
            {
                var link = await s.GitHub.FindLinkByIssueAsync(connection.Id, issue.Id, token);
                if (link is { State: 0 or 1 }) result.Add((link.Id, issue.NodeId));
            }
            return result;
        }, ct);
        await RefreshAsync(repo, connection, links, ct);
    }

    /// <summary>Re-reads the pull requests of issues whose open pull requests were last refreshed before <paramref name="olderThan"/>.</summary>
    public async Task RefreshStaleAsync(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, IReadOnlyList<GitHubIssueLinkRecord> links, CancellationToken ct)
    {
        if (links.Count == 0) return;
        var targets = await store.QueryAsync(async (s, token) =>
        {
            var result = new List<(string LinkId, string NodeId)>();
            foreach (var link in links)
            {
                var current = await s.GitHub.GetIssueLinkAsync(link.Id, token);
                if (current is { State: 0 or 1 }) result.Add((current.Id, current.IssueNodeId));
            }
            return result;
        }, ct);
        await RefreshAsync(repo, connection, targets, ct);
    }

    private async Task RefreshAsync(GitHubRepositoryRecord repo, GitHubConnectionRecord connection, IReadOnlyList<(string LinkId, string NodeId)> targets, CancellationToken ct)
    {
        var account = new GitHubAccount(connection.AccountId, connection.AccountLogin);
        foreach (var batch in targets.Chunk(50))
        {
            var byNode = batch.ToDictionary(target => target.NodeId, target => target.LinkId, StringComparer.Ordinal);
            IReadOnlyDictionary<string, IReadOnlyList<GitHubPullRequestRef>> found;
            try { found = await api.GetClosingPullRequestsAsync(account, [.. byNode.Keys], ct); }
            catch (GitHubApiException error) when (error.Kind is GitHubErrorKind.Server or GitHubErrorKind.Validation or GitHubErrorKind.Forbidden)
            {
                continue; // supplementary data; the rows stay as they were and the next pass asks again
            }
            var now = clock.GetUtcNow();
            foreach (var (nodeId, linkId) in byNode)
            {
                await inbound.ApplyPullRequestsAsync(repo.Id, repo.Version, linkId, found.GetValueOrDefault(nodeId) ?? [], now, ct);
            }
        }
    }
}
