namespace Chaos.Server.Sync;

/// <summary>
/// The task status numbers of <c>Chaos.Domain.Tasks.TaskItemStatus</c>, as the service reads and writes them in the
/// <c>status</c> group. The service references Chaos.Sync only, never a client project, so the values are mirrored here.
/// </summary>
internal static class TaskStatuses
{
    public const long NotStarted = 0;
    public const long InProgress = 1;
    public const long WaitingOnSomeone = 2;
    public const long NeedsReview = 3;
    public const long Done = 4;
    public const long Cancelled = 5;

    public static bool IsTerminal(long status) => status is Done or Cancelled;

    public static string Name(long status) => status switch
    {
        NotStarted => nameof(NotStarted),
        InProgress => nameof(InProgress),
        WaitingOnSomeone => nameof(WaitingOnSomeone),
        NeedsReview => nameof(NeedsReview),
        Done => nameof(Done),
        _ => nameof(Cancelled),
    };
}
