namespace Chaos.Domain.GitHub;

/// <summary>The fields compared and synchronized with an issue; personal task fields stay local.</summary>
public sealed record IssueProjection(
    string Title,
    string Body,
    GitHubIssueState State,
    IReadOnlySet<long> LabelIds,
    IReadOnlySet<long> AssigneeIds);
