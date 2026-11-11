using Chaos.Domain.Tasks;

namespace Chaos.Domain.GitHub;

public readonly record struct GitHubIssueState(bool IsOpen, string? Reason);

public static class IssueStateMapping
{
    public static TaskItemStatus ToTask(GitHubIssueState state, TaskItemStatus current, TaskItemStatus? previousNonterminal = null)
    {
        if (state.IsOpen)
        {
            if (!TaskItemStatusRules.IsTerminal(current)) return current;
            return previousNonterminal is { } previous && !TaskItemStatusRules.IsTerminal(previous)
                ? previous : TaskItemStatus.NotStarted;
        }

        return state.Reason is "not_planned" or "duplicate" ? TaskItemStatus.Cancelled : TaskItemStatus.Done;
    }

    public static GitHubIssueState FromTask(TaskItemStatus status, GitHubIssueState previous) => status switch
    {
        TaskItemStatus.Done => new(false, "completed"),
        TaskItemStatus.Cancelled => new(false, !previous.IsOpen && previous.Reason == "duplicate" ? "duplicate" : "not_planned"),
        TaskItemStatus.NotStarted or TaskItemStatus.InProgress or TaskItemStatus.WaitingOnSomeone or TaskItemStatus.NeedsReview =>
            new(true, previous.IsOpen ? previous.Reason : "reopened"),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
