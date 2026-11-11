namespace Chaos.Domain.GitHub;

/// <summary>The fields compared and synchronized with an issue; personal task fields stay local.</summary>
public sealed record IssueProjection(
    string Title,
    string Body,
    GitHubIssueState State,
    IReadOnlySet<long> LabelIds,
    IReadOnlySet<long> AssigneeIds)
{
    /// <summary>Compares synchronized values independently of set instance and ordering.</summary>
    public bool EquivalentTo(IssueProjection other) =>
        Title == other.Title && Body == other.Body && State == other.State &&
        LabelIds.SetEquals(other.LabelIds) && AssigneeIds.SetEquals(other.AssigneeIds);

    public bool Equals(IssueProjection? other) => other is not null && EquivalentTo(other);

    public override int GetHashCode() => HashCode.Combine(Title, Body, State,
        LabelIds.Aggregate(0, (hash, id) => hash ^ id.GetHashCode()),
        AssigneeIds.Aggregate(0, (hash, id) => hash ^ id.GetHashCode()));
}
