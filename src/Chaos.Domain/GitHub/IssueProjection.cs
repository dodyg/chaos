namespace Chaos.Domain.GitHub;

/// <summary>
/// The fields compared and synchronized with an issue; personal task fields stay local. Assignees are compared
/// by login: a member's GitHub login is all the app knows until GitHub reports the assignee's numeric ID.
/// </summary>
public sealed record IssueProjection(
    string Title,
    string Body,
    GitHubIssueState State,
    IReadOnlySet<long> LabelIds,
    IReadOnlySet<string> Assignees)
{
    public static StringComparer LoginComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>An assignee set of logins, compared without regard to case as GitHub does.</summary>
    public static HashSet<string> Logins(params string[] logins) => new(logins, LoginComparer);

    /// <summary>Compares synchronized values independently of set instance and ordering.</summary>
    public bool EquivalentTo(IssueProjection other) =>
        Title == other.Title && Body == other.Body && State == other.State &&
        LabelIds.SetEquals(other.LabelIds) && Assignees.SetEquals(other.Assignees);

    public bool Equals(IssueProjection? other) => other is not null && EquivalentTo(other);

    public override int GetHashCode() => HashCode.Combine(Title, Body, State,
        LabelIds.Aggregate(0, (hash, id) => hash ^ id.GetHashCode()),
        Assignees.Aggregate(0, (hash, login) => hash ^ LoginComparer.GetHashCode(login)));
}
