namespace Chaos.Domain.Tasks;

public enum TaskAssignMode
{
    Anyone = 0,
    Each = 1,
}

public sealed record AssignmentCompletion(DateTimeOffset? CompletedAtUtc, DateTimeOffset? DeletedAtUtc);

public static class TaskAssignment
{
    /// <summary>Returns the deterministic completion time when every live assignee has completed.</summary>
    public static DateTimeOffset? EachCompletion(IEnumerable<AssignmentCompletion> assignees)
    {
        DateTimeOffset? latest = null;
        var hasLiveAssignee = false;
        foreach (var assignee in assignees)
        {
            if (assignee.DeletedAtUtc is not null)
                continue;
            hasLiveAssignee = true;
            if (assignee.CompletedAtUtc is not { } completed)
                return null;
            if (latest is null || completed > latest)
                latest = completed;
        }

        return hasLiveAssignee ? latest : null;
    }
}
