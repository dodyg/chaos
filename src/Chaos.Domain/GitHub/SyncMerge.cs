namespace Chaos.Domain.GitHub;

public readonly record struct FieldMerge<T>(T Value, bool Push, bool Conflict);

/// <summary>Local values replaced by concurrent GitHub changes, for task history.</summary>
public sealed record IssueConflicts(string? Title, string? Body, GitHubIssueState? State);
public sealed record IssuePush(string? Title, string? Body, GitHubIssueState? State,
    IReadOnlySet<long> AddLabels, IReadOnlySet<long> RemoveLabels,
    IReadOnlySet<long> AddAssignees, IReadOnlySet<long> RemoveAssignees);
public sealed record IssueMerge(IssueProjection Value, IssuePush Push, IssueConflicts Conflicts);

public static class SyncMerge
{
    public static IssueMerge Issue(IssueProjection @base, IssueProjection local, IssueProjection remote)
    {
        var title = Field(@base.Title, local.Title, remote.Title);
        var body = Field(@base.Body, local.Body, remote.Body);
        var state = Field(@base.State, local.State, remote.State);
        var labels = Set(@base.LabelIds, local.LabelIds, remote.LabelIds);
        var assignees = Set(@base.AssigneeIds, local.AssigneeIds, remote.AssigneeIds);
        var value = new IssueProjection(title.Value, body.Value, state.Value, labels, assignees);
        var push = new IssuePush(title.Push ? title.Value : null, body.Push ? body.Value : null,
            state.Push ? state.Value : null,
            Difference(labels, remote.LabelIds), Difference(remote.LabelIds, labels),
            Difference(assignees, remote.AssigneeIds), Difference(remote.AssigneeIds, assignees));
        return new(value, push, new(title.Conflict ? local.Title : null,
            body.Conflict ? local.Body : null, state.Conflict ? local.State : null));
    }

    private static HashSet<long> Difference(IEnumerable<long> left, IEnumerable<long> right)
    {
        var difference = new HashSet<long>(left);
        difference.ExceptWith(right);
        return difference;
    }

    public static FieldMerge<T> Field<T>(T @base, T local, T remote, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        if (comparer.Equals(local, remote)) return new(remote, false, false);
        if (comparer.Equals(local, @base)) return new(remote, false, false);
        if (comparer.Equals(remote, @base)) return new(local, true, false);
        return new(remote, false, true);
    }

    public static HashSet<T> Set<T>(IEnumerable<T> @base, IEnumerable<T> local, IEnumerable<T> remote, IEqualityComparer<T>? comparer = null)
    {
        var original = new HashSet<T>(@base, comparer);
        var current = new HashSet<T>(local, comparer);
        var result = new HashSet<T>(remote, comparer);
        var removed = new HashSet<T>(original, comparer);
        removed.ExceptWith(current);
        current.ExceptWith(original);
        result.UnionWith(current);
        result.ExceptWith(removed);
        return result;
    }
}
