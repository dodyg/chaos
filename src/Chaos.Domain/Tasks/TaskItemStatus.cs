namespace Chaos.Domain.Tasks;

/// <summary>Six built-in statuses (PLAN §1 product decision 7). Done and Cancelled are terminal; only Done counts as completed.</summary>
public enum TaskItemStatus
{
    NotStarted = 0,
    InProgress = 1,
    WaitingOnSomeone = 2,
    NeedsReview = 3,
    Done = 4,
    Cancelled = 5,
}

public static class TaskItemStatusRules
{
    public const int MinValue = (int)TaskItemStatus.NotStarted;
    public const int MaxValue = (int)TaskItemStatus.Cancelled;

    public static bool IsTerminal(TaskItemStatus status) => status is TaskItemStatus.Done or TaskItemStatus.Cancelled;

    public static bool IsCompleted(TaskItemStatus status) => status == TaskItemStatus.Done;

    /// <summary>SQL CHECK fragment keeping the enum and its constraints in one place.</summary>
    public const string CheckSql = "Status BETWEEN 0 AND 5";
}
