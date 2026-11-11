using Chaos.Application.Contracts;

namespace Chaos.Application.Abstractions;

/// <summary>GitHub metadata in the caller's transaction. Never contains credentials.</summary>
public interface IGitHubStore
{
    Task AddConnectionAsync(GitHubConnectionRecord row, CancellationToken ct);
    Task<GitHubConnectionRecord?> GetConnectionAsync(string id, CancellationToken ct);
    Task<GitHubConnectionRecord?> UpdateConnectionAsync(GitHubConnectionRecord row, long expectedVersion, CancellationToken ct);
    Task AddRepositoryAsync(GitHubRepositoryRecord row, CancellationToken ct);
    Task<GitHubRepositoryRecord?> GetRepositoryAsync(string id, CancellationToken ct);
    Task<GitHubRepositoryRecord?> UpdateRepositoryAsync(GitHubRepositoryRecord row, long expectedVersion, CancellationToken ct);
    Task AddIssueLinkAsync(GitHubIssueLinkRecord row, CancellationToken ct);
    Task<GitHubIssueLinkRecord?> GetIssueLinkAsync(string id, CancellationToken ct);
    Task<GitHubIssueLinkRecord?> UpdateIssueLinkAsync(GitHubIssueLinkRecord row, long expectedVersion, CancellationToken ct);
    Task UpsertChecklistItemAsync(GitHubChecklistItemRecord row, CancellationToken ct);
    Task UpsertCommentLinkAsync(GitHubCommentLinkRecord row, CancellationToken ct);
    Task UpsertLabelTagAsync(GitHubLabelTagRecord row, CancellationToken ct);
    Task UpsertMilestoneProjectAsync(GitHubMilestoneProjectRecord row, CancellationToken ct);
    Task AddSyncRunAsync(GitHubSyncRunRecord row, CancellationToken ct);
    Task<GitHubConnectionRecord?> GetConnectionForSpaceAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubRepositoryRecord>> ListSubscriptionsAsync(string id, CancellationToken ct);
    Task<GitHubIssueLinkRecord?> FindLinkByIssueAsync(string id, long issueId, CancellationToken ct);
    Task<GitHubIssueLinkRecord?> FindLinkByTaskAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubChecklistItemRecord>> ListChecklistItemsAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubCommentLinkRecord>> ListCommentLinksAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubLabelTagRecord>> ListLabelTagsAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubMilestoneProjectRecord>> ListMilestoneProjectsAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubSyncRunRecord>> ListSyncRunsAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<GitHubIssueLinkRecord>> ListDirtyLinksAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<GitHubIssueLinkRecord>> ListLinksDueForRetryAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task<IReadOnlyList<GitHubIssueLinkRecord>> ListLinksForTasksAsync(IReadOnlyList<string> ids, CancellationToken ct);
    Task ReplaceChecklistItemsAsync(string issueLinkId, IReadOnlyList<GitHubChecklistItemRecord> rows, CancellationToken ct);
}
