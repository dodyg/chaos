namespace Chaos.Application.Contracts;

public sealed record GitHubConnectionRecord
{
    public required string Id { get; init; }
    public required string SpaceId { get; init; }
    public required string ProfileId { get; init; }
    public required string WebBaseUrl { get; init; }
    public required string ApiBaseUrl { get; init; }
    public long AccountId { get; init; }
    public required string AccountLogin { get; init; }
    public int State { get; init; }
    public bool PostDiaryByDefault { get; init; } = true;
    public bool CreateLabelsForTags { get; init; }
    public DateTimeOffset? RateLimitResetUtc { get; init; }
    public DateTimeOffset? LastSyncStartedUtc { get; init; }
    public DateTimeOffset? LastSyncCompletedUtc { get; init; }
    public int? LastErrorKind { get; init; }
    public string? LastErrorMessage { get; init; }
    public long Version { get; init; } = 1;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }
}

public sealed record GitHubRepositoryRecord
{
    public required string Id { get; init; }
    public required string ConnectionId { get; init; }
    public long RepositoryId { get; init; }
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string HtmlUrl { get; init; }
    public bool IsPrivate { get; init; }
    public required string TargetContainerId { get; init; }
    public string? TargetSectionId { get; init; }
    public required string FilterJson { get; init; }
    public bool MilestonesAsProjects { get; init; }
    public int State { get; init; }
    public DateTimeOffset? IssuesCursorUtc { get; init; }
    public DateTimeOffset? CommentsCursorUtc { get; init; }
    public string? IssuesEtag { get; init; }
    public string? CommentsEtag { get; init; }
    public string? LabelsEtag { get; init; }
    public string? MilestonesEtag { get; init; }
    public DateTimeOffset? InitialImportDoneUtc { get; init; }
    public DateTimeOffset? LastSyncedUtc { get; init; }
    public int? LastErrorKind { get; init; }
    public string? LastErrorMessage { get; init; }
    public long Version { get; init; } = 1;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? DeletedAtUtc { get; init; }
}

public sealed record GitHubIssueLinkRecord
{
    public required string Id { get; init; }
    public required string RepositorySubscriptionId { get; init; }
    public required string ConnectionId { get; init; }
    public string? TaskId { get; init; }
    public long IssueId { get; init; }
    public required string IssueNodeId { get; init; }
    public long IssueNumber { get; init; }
    public required string HtmlUrl { get; init; }
    public int State { get; init; }
    public required string BaseJson { get; init; }
    public required DateTimeOffset BaseRemoteUpdatedUtc { get; init; }
    public string? IssueEtag { get; init; }
    public int? LocalAssigneeIntent { get; init; }
    public bool PlacementManaged { get; init; } = true;
    public bool LocalDirty { get; init; }
    public DateTimeOffset? LocalDirtySinceUtc { get; init; }
    public DateTimeOffset? LastSyncedUtc { get; init; }
    public DateTimeOffset? ConflictNoticeUtc { get; init; }
    public int? LastErrorKind { get; init; }
    public string? LastErrorMessage { get; init; }
    public int ErrorAttempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public long Version { get; init; } = 1;
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record GitHubChecklistItemRecord
{
    public required string SubtaskId { get; init; }
    public required string IssueLinkId { get; init; }
    public long BaseOrdinal { get; init; }
    public required string BaseText { get; init; }
    public bool BaseChecked { get; init; }
}

public sealed record GitHubCommentLinkRecord
{
    public required string DiaryEntryId { get; init; }
    public required string IssueLinkId { get; init; }
    public long? CommentId { get; init; }
    public string? AuthorLogin { get; init; }
    public string? HtmlUrl { get; init; }
    public int Origin { get; init; }
    public int State { get; init; }
    public string? BaseBody { get; init; }
    public DateTimeOffset? BaseRemoteUpdatedUtc { get; init; }
    public int? LastErrorKind { get; init; }
    public string? LastErrorMessage { get; init; }
    public int ErrorAttempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
}

public sealed record GitHubLabelTagRecord
{
    public required string RepositorySubscriptionId { get; init; }
    public long LabelId { get; init; }
    public required string LabelName { get; init; }
    public required string LabelColor { get; init; }
    public required string TagId { get; init; }
}

public sealed record GitHubMilestoneProjectRecord
{
    public required string RepositorySubscriptionId { get; init; }
    public long MilestoneNumber { get; init; }
    public long MilestoneId { get; init; }
    public required string ContainerId { get; init; }
    public required string BaseTitle { get; init; }
    public string? BaseDueOn { get; init; }
}

public sealed record GitHubSyncRunRecord
{
    public required string Id { get; init; }
    public required string ConnectionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public long RequestCount { get; init; }
    public long NotModifiedCount { get; init; }
    public long CreatedCount { get; init; }
    public long UpdatedCount { get; init; }
    public long PushedCount { get; init; }
    public long ConflictCount { get; init; }
    public int? ErrorKind { get; init; }
}
