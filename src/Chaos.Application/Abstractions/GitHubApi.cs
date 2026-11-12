namespace Chaos.Application.Abstractions;

public enum GitHubErrorKind
{
    Unauthorized, CliUnavailable, AccountMismatch, Forbidden, NotFound, Gone, Moved,
    RateLimited, Validation, Network, Server,
}

/// <summary>Sanitized transport failure. Never contains process output, response bodies or credentials.</summary>
public sealed class GitHubApiException(GitHubErrorKind kind, DateTimeOffset? retryAtUtc = null, string? movedToUrl = null) : Exception("GitHub request failed: " + kind)
{
    public GitHubErrorKind Kind { get; } = kind;
    public DateTimeOffset? RetryAtUtc { get; } = retryAtUtc;
    public string? MovedToUrl { get; } = movedToUrl;
}

public sealed record GitHubAccount(long Id, string Login);
public sealed record GitHubUser(long Id, string Login);
public sealed record GitHubRepository(long Id, string NodeId, string Name, string FullName, GitHubUser Owner, string HtmlUrl, bool Private, bool HasIssues);
public sealed record GitHubLabel(long Id, string Name, string Color, string? NodeId = null);
public sealed record GitHubMilestone(long Id, long Number, string Title, string State, DateTimeOffset? DueOn);
public sealed record GitHubIssueType(long Id, string Name);
public sealed record GitHubPullRequestMarker(string Url);
public sealed record GitHubIssue(long Id, string NodeId, long Number, string HtmlUrl, string Title, string? Body,
    string State, string? StateReason, GitHubUser User, IReadOnlyList<GitHubLabel> Labels, IReadOnlyList<GitHubUser> Assignees,
    GitHubMilestone? Milestone, GitHubIssueType? Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ClosedAt,
    int Comments, GitHubPullRequestMarker? PullRequest);
public sealed record GitHubComment(long Id, string Body, GitHubUser User, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string HtmlUrl, string? IssueUrl = null);
public sealed record GitHubPage<T>(IReadOnlyList<T> Items, string? NextCursor, string? Etag, bool NotModified, long? RateLimitRemaining, DateTimeOffset? RateLimitResetUtc, string? PreviousCursor = null, string? LastCursor = null);
public sealed record GitHubIssueUpdate(string? Title = null, string? Body = null, string? State = null, string? StateReason = null);
public sealed record GitHubIssueRead(GitHubIssue? Issue, string? Etag, bool NotModified);
public sealed record GitHubPullRequestRef(long Id, string NodeId, string RepositoryFullName, long Number, string Title, string HtmlUrl, string State, bool IsDraft, bool Merged, string? ReviewDecision);

/// <summary>No credentials or HTTP types cross this port. Setup is called only by explicit user action.</summary>
public interface IGitHubApi
{
    long RequestCount { get; }
    long NotModifiedCount { get; }
    Task<GitHubAccount> GetSetupAccountAsync(CancellationToken ct);
    Task<GitHubPage<GitHubRepository>> ListRepositoriesAsync(GitHubAccount account, string? cursor, string? etag, CancellationToken ct);
    Task<GitHubRepository> GetRepositoryAsync(GitHubAccount account, string owner, string name, CancellationToken ct);
    Task<GitHubPage<GitHubIssue>> ListIssuesAsync(GitHubAccount account, string owner, string name, DateTimeOffset? since, string? cursor, string? etag, CancellationToken ct, string? state = null);
    Task<GitHubIssue> GetIssueAsync(GitHubAccount account, string owner, string name, long number, CancellationToken ct);
    Task<GitHubIssueRead> ReadIssueAsync(GitHubAccount account, string owner, string name, long number, string? etag, CancellationToken ct);
    Task<GitHubIssue> UpdateIssueAsync(GitHubAccount account, string owner, string name, long number, GitHubIssueUpdate update, CancellationToken ct);
    Task<GitHubPage<GitHubLabel>> ListLabelsAsync(GitHubAccount account, string owner, string name, string? cursor, string? etag, CancellationToken ct);
    Task<GitHubPage<GitHubMilestone>> ListMilestonesAsync(GitHubAccount account, string owner, string name, string? cursor, string? etag, CancellationToken ct);
    Task<GitHubPage<GitHubComment>> ListCommentsAsync(GitHubAccount account, string owner, string name, DateTimeOffset? since, long? issueNumber, string? cursor, string? etag, CancellationToken ct);
    Task<GitHubComment> PostCommentAsync(GitHubAccount account, string owner, string name, long number, string body, CancellationToken ct);
    Task<GitHubComment> EditCommentAsync(GitHubAccount account, string owner, string name, long commentId, string body, CancellationToken ct);
    Task DeleteCommentAsync(GitHubAccount account, string owner, string name, long commentId, CancellationToken ct);
    Task AddLabelAsync(GitHubAccount account, string owner, string name, long number, string label, CancellationToken ct);
    Task RemoveLabelAsync(GitHubAccount account, string owner, string name, long number, string label, CancellationToken ct);
    Task<GitHubLabel> CreateLabelAsync(GitHubAccount account, string owner, string name, string label, CancellationToken ct);
    Task SetSelfAssignmentAsync(GitHubAccount account, string owner, string name, long number, bool assign, CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyList<GitHubPullRequestRef>>> GetClosingPullRequestsAsync(GitHubAccount account, IReadOnlyList<string> issueNodeIds, CancellationToken ct);
}
