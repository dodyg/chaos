using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Planning;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;
using Chaos.Sync;

namespace Chaos.Application.Planning;
public enum WeekCandidateReason
{
    Overdue = 0,
    DueThisWeek = 1,
    PlannedThisWeek = 2,
    OnMyDayToday = 3,
}

public enum WeekGoalSuggestionReason
{
    ContinueGoal = 0,
    ProjectDeadline = 1,
    ImportantTask = 2,
}

public sealed record WeekTask(TaskRecord Task, string ContainerName, bool ContainerArchived);
public sealed record WeekPlacement(string TaskId, DateOnly? Day, WeekAssignmentOrigin Origin);

/// <summary>Goal progress is Done linked tasks over noncancelled linked tasks; an unlinked goal is assessed by hand.</summary>
public sealed record WeekGoalProgress(
    string Id, string Title, string? ProjectId, string? ProjectName, IReadOnlyList<string> TaskIds,
    WeekGoalOutcome Outcome, int DoneCount, int CountedCount, long Version);

/// <summary>One day of a week board. <see cref="Availability"/> is what the person set; <see cref="BusyMinutes"/> is busy meeting time, and <see cref="Load"/> is measured against what is left after it.</summary>
public sealed record WeekDayLoad(DateOnly Date, DayAvailability Availability, bool IsOverride, DayLoad Load, int BusyMinutes = 0);

/// <summary>One week's goals, placements and per-day capacity (confirmed rows or the draft).</summary>
public sealed record WeekBoard(IReadOnlyList<WeekGoalProgress> Goals, IReadOnlyList<WeekPlacement> Placements, IReadOnlyList<WeekDayLoad> Days, DayLoad Pool);

/// <summary>
/// Historical facts of one planned week (ritual step 1). When a stored summary covers a complete
/// weekly summary of that week, its stored facts supply what was completed (with the titles
/// captured then) and the planned-task counts; otherwise they come live from the plan's
/// membership and the tasks' current state. Unfinished tasks and goals are always current,
/// because the leftover decisions and goal outcomes act on them now.
/// </summary>
/// <param name="SummaryId">The weekly summary the facts came from, or null for the live view.</param>
/// <param name="SummaryCompleted">Completed tasks as the summary captured them (only from a stored summary).</param>
public sealed record WeekLookBack(
    DateOnly WeekStart, DateOnly WeekEnd, bool HadPlan,
    IReadOnlyList<string> CompletedTaskIds, long CompletedCount,
    int AssignedCount, int AssignedDone, int AssignedCancelled,
    IReadOnlyList<string> UnfinishedTaskIds, IReadOnlyList<WeekGoalProgress> Goals,
    string? SummaryId = null, IReadOnlyList<SummaryTaskFact>? SummaryCompleted = null);

public sealed record WeekCandidate(string TaskId, WeekCandidateReason Reason);
public sealed record WeekGoalSuggestion(string Title, string? ProjectId, IReadOnlyList<string> TaskIds, WeekGoalSuggestionReason Reason);

/// <summary>
/// The explicit comparison shown when a confirmed week changes a day that already has a plan:
/// tasks the week now assigns there, and week-seeded tasks it no longer does. Manual choices
/// are never offered for removal, and a task the person removed is never offered again.
/// </summary>
public sealed record DayReconciliation(DateOnly Date, string WeekPlanId, long Revision, IReadOnlyList<string> AddTaskIds, IReadOnlyList<string> RemoveTaskIds);

public sealed record WeekSlip(string TaskId, DateOnly Day);

/// <summary>The deterministic midweek check-in: slipped and overdue assignments and overloaded remaining days. It changes nothing.</summary>
public sealed record WeekCheckIn(bool Midweek, IReadOnlyList<WeekSlip> Slipped, IReadOnlyList<string> OverdueTaskIds, IReadOnlyList<DateOnly> OverloadedDays);

public sealed record WeekOverview(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    DateOnly Today,
    DayOfWeek FirstWeekday,
    string? PlanId,
    WeekPlanState? State,
    long ConfirmedRevision,
    DateTimeOffset? ConfirmedAtUtc,
    bool Editable,
    bool AvailabilityConfigured,
    WeekBoard Confirmed,
    WeekDraft? Draft,
    long DraftRevision,
    WeekBoard? DraftBoard,
    WeekLookBack LookBack,
    IReadOnlyList<WeekCandidate> Candidates,
    IReadOnlyList<WeekGoalSuggestion> GoalSuggestions,
    IReadOnlyList<DayReconciliation> PendingReconciliation,
    WeekCheckIn? CheckIn,
    IReadOnlyList<WeekTask> Tasks);

public sealed record WeekCommand(string OperationId, DateOnly WeekStart);
public sealed record WeekDraftCommand(string OperationId, DateOnly WeekStart, long ExpectedDraftRevision, WeekDraft Draft);
public sealed record WeekRevisionCommand(string OperationId, DateOnly WeekStart, long ExpectedDraftRevision);
public sealed record WeekReconcileCommand(string OperationId, DateOnly Date, string WeekPlanId, long Revision, IReadOnlyList<string> AddTaskIds, IReadOnlyList<string> RemoveTaskIds);
public sealed record WeekGoalOutcomeCommand(string OperationId, string GoalId, long ExpectedVersion, WeekGoalOutcome Outcome);
public sealed record AvailabilityDay(DayOfWeek Weekday, int TotalMinutes, int? WorkMinutes);
public sealed record WeekAvailabilityCommand(string OperationId, IReadOnlyList<AvailabilityDay> Days);
public sealed record WeekStartConflict(DateOnly WeekStart, WeekPlanState State, bool HasDraft, int AssignmentCount, int GoalCount);
public sealed record WeekStartPreview(DayOfWeek Current, DayOfWeek Proposed, IReadOnlyList<WeekStartConflict> Conflicts, DateOnly NewCurrentWeekStart);
public sealed record WeekStartCommand(string OperationId, DayOfWeek FirstWeekday, bool Remap);
/// <param name="DraftWeekStarts">Weeks that received remapped work as drafts and need confirming again.</param>
public sealed record WeekStartResult(DayOfWeek FirstWeekday, IReadOnlyList<DateOnly> DraftWeekStarts, bool Replayed);
public sealed record WeekChangeResult(WeekOverview Week, bool Replayed);
