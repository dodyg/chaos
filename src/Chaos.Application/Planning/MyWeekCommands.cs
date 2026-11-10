using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Planning;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

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

public sealed record WeekDayLoad(DateOnly Date, DayAvailability Availability, bool IsOverride, DayLoad Load);

/// <summary>One week's goals, placements and per-day capacity (confirmed rows or the draft).</summary>
public sealed record WeekBoard(IReadOnlyList<WeekGoalProgress> Goals, IReadOnlyList<WeekPlacement> Placements, IReadOnlyList<WeekDayLoad> Days, DayLoad Pool);

/// <summary>
/// Historical facts of one planned week (ritual step 1). Counts come from the plan's
/// membership and the tasks' current state; package 11 replaces this live view with stored
/// journal snapshots when they exist.
/// </summary>
public sealed record WeekLookBack(
    DateOnly WeekStart, DateOnly WeekEnd, bool HadPlan,
    IReadOnlyList<string> CompletedTaskIds, long CompletedCount,
    int AssignedCount, int AssignedDone, int AssignedCancelled,
    IReadOnlyList<string> UnfinishedTaskIds, IReadOnlyList<WeekGoalProgress> Goals);

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

/// <summary>
/// My Week (PLAN §10 "Weekly ritual and capacity", work package 9). A Draft is stored apart
/// from the Confirmed plan, so editing or cancelling it never changes the confirmed set.
/// Confirming atomically applies the reviewed leftover decisions, replaces goals, assignments
/// and the week's availability overrides, keeps the previous revision as history, and either
/// marks today's existing day plan as following the new revision or leaves an explicit
/// add/remove comparison for the person. Weekly assignments never change a task's planned or
/// due date; only an explicit "later" date chosen in the review does.
/// </summary>
public sealed class MyWeekCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxGoals = 20;
    public const int MaxAssignments = 500;
    public const int MaxGoalTasks = 200;

    /// <summary>Reads the week containing <paramref name="date"/> (today when null). Never writes.</summary>
    public async Task<WeekOverview> OpenAsync(DateOnly? date, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            return await OverviewAsync(session, context, date ?? context.Today, token);
        }, ct);

    /// <summary>Starts (or returns) the week's draft, beginning from the confirmed plan when there is one.</summary>
    public Task<WeekChangeResult> StartDraftAsync(WeekCommand command, CancellationToken ct = default) =>
        WriteAsync(command.OperationId, "week.draft.start", new { weekStart = command.WeekStart }, command.WeekStart, async (session, context, now, token) =>
        {
            var plan = await EnsureWeekAsync(session, context, command.WeekStart, now, token);
            if (plan.DraftPayload is not null)
            {
                return 0;
            }

            var draft = WeekDraft.Empty;
            if (plan.State == WeekPlanState.Confirmed)
            {
                var overrides = await session.Weeks.ListOverridesAsync(context.Profile.Id, plan.WeekStartDate, WeekCalendar.EndOf(plan.WeekStartDate), token);
                draft = new WeekDraft(
                    [.. (await session.Weeks.ListGoalsAsync(plan.Id, token)).Select(goal => new WeekDraftGoal(goal.Id, goal.Title, goal.ProjectId, goal.TaskIds, goal.Outcome))],
                    [.. (await session.Weeks.ListAssignmentsAsync(plan.Id, token)).Select(item => new WeekDraftAssignment(item.TaskId, item.LocalDate))],
                    [],
                    [.. overrides.Select(item => new WeekDraftOverride(item.Date!.Value, item.TotalMinutes, item.WorkMinutes))],
                    Step: 3);
            }

            await UpdatePlanAsync(session, plan with
            {
                DraftPayload = StoredJsonFormats.Snapshot(draft),
                DraftRevision = plan.DraftRevision + 1,
                DraftBaseRevision = plan.ConfirmedRevision,
                Version = plan.Version + 1,
                UpdatedAtUtc = now,
            }, plan.Version, token);
            return 1;
        }, ct);

    /// <summary>Saves the whole reviewed draft. A stale revision is a version conflict; the confirmed plan is untouched.</summary>
    public Task<WeekChangeResult> SaveDraftAsync(WeekDraftCommand command, CancellationToken ct = default) =>
        WriteAsync(command.OperationId, "week.draft.save", new { weekStart = command.WeekStart, expected = command.ExpectedDraftRevision, draft = command.Draft }, command.WeekStart, async (session, context, now, token) =>
        {
            var plan = await EnsureWeekAsync(session, context, command.WeekStart, now, token);
            RequireDraftRevision(plan, command.ExpectedDraftRevision);
            var draft = await ValidateDraftAsync(session, context, plan, command.Draft, token);
            await UpdatePlanAsync(session, plan with
            {
                DraftPayload = StoredJsonFormats.Snapshot(draft),
                DraftRevision = plan.DraftRevision + 1,
                DraftBaseRevision = plan.DraftBaseRevision ?? plan.ConfirmedRevision,
                Version = plan.Version + 1,
                UpdatedAtUtc = now,
            }, plan.Version, token);
            return 1;
        }, ct);

    /// <summary>Cancels the ritual: the draft is dropped and the previously confirmed plan stays exactly as it was.</summary>
    public Task<WeekChangeResult> DiscardDraftAsync(WeekRevisionCommand command, CancellationToken ct = default) =>
        WriteAsync(command.OperationId, "week.draft.discard", new { weekStart = command.WeekStart, expected = command.ExpectedDraftRevision }, command.WeekStart, async (session, context, now, token) =>
        {
            var plan = await session.Weeks.FindByStartAsync(context.Profile.Id, command.WeekStart, token);
            if (plan?.DraftPayload is null)
            {
                return 0;
            }

            RequireDraftRevision(plan, command.ExpectedDraftRevision);
            await UpdatePlanAsync(session, plan with { DraftPayload = null, DraftBaseRevision = null, Version = plan.Version + 1, UpdatedAtUtc = now }, plan.Version, token);
            return 1;
        }, ct);

    /// <summary>
    /// Confirms the reviewed draft in one transaction. Any invalid part (a task deleted
    /// meanwhile, a stale revision) refuses the whole confirmation and changes nothing.
    /// </summary>
    public Task<WeekChangeResult> ConfirmAsync(WeekRevisionCommand command, CancellationToken ct = default) =>
        WriteAsync(command.OperationId, "week.confirm", new { weekStart = command.WeekStart, expected = command.ExpectedDraftRevision }, command.WeekStart, async (session, context, now, token) =>
        {
            var plan = await EnsureWeekAsync(session, context, command.WeekStart, now, token);
            if (plan.DraftPayload is null)
            {
                throw AppException.Validation("draft", "There is no draft to confirm. Start planning the week first.");
            }

            RequireDraftRevision(plan, command.ExpectedDraftRevision);
            var draft = await ValidateDraftAsync(session, context, plan, ReadDraft(plan) ?? WeekDraft.Empty, token);
            var weekEnd = WeekCalendar.EndOf(plan.WeekStartDate);

            // Step 2 decisions that change a task are applied here, never while drafting.
            var placements = draft.Assignments.Select(item => (item.TaskId, item.Day, Origin: WeekAssignmentOrigin.Planned)).ToList();
            foreach (var leftover in draft.Leftovers)
            {
                switch (leftover.Decision)
                {
                    case LeftoverDecision.Carry:
                        var index = placements.FindIndex(item => item.TaskId == leftover.TaskId);
                        if (index < 0)
                        {
                            placements.Add((leftover.TaskId, null, WeekAssignmentOrigin.Carried));
                        }
                        else
                        {
                            placements[index] = placements[index] with { Origin = WeekAssignmentOrigin.Carried };
                        }

                        break;
                    case LeftoverDecision.Cancel:
                        await CancelTaskAsync(session, leftover.TaskId, command.OperationId, now, token);
                        break;
                    case LeftoverDecision.Later when leftover.LaterDate is { } later:
                        await ReplanTaskAsync(session, leftover.TaskId, later, command.OperationId, now, token);
                        break;
                }
            }

            var existingGoals = (await session.Weeks.ListGoalsAsync(plan.Id, token)).ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            var goals = draft.Goals.Select((goal, index) => existingGoals.TryGetValue(goal.Id, out var before)
                ? before with { Title = goal.Title, ProjectId = goal.ProjectId, TaskIds = goal.TaskIds, Position = index * RankOrdering.Gap, Version = before.Version + 1, UpdatedAtUtc = now }
                : new WeekGoalRecord
                {
                    Id = goal.Id,
                    WeekPlanId = plan.Id,
                    Title = goal.Title,
                    Position = index * RankOrdering.Gap,
                    ProjectId = goal.ProjectId,
                    Outcome = goal.Outcome,
                    TaskIds = goal.TaskIds,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }).ToList();
            var perDay = new Dictionary<DateOnly, long>();
            long poolPosition = 0;
            var assignments = placements.Select(item =>
            {
                long position;
                if (item.Day is { } day)
                {
                    position = perDay.GetValueOrDefault(day);
                    perDay[day] = position + RankOrdering.Gap;
                }
                else
                {
                    position = poolPosition;
                    poolPosition += RankOrdering.Gap;
                }

                return new WeekAssignmentRecord
                {
                    Id = StorageFormats.Id(ids.NewId()),
                    WeekPlanId = plan.Id,
                    TaskId = item.TaskId,
                    LocalDate = item.Day,
                    Position = position,
                    Origin = item.Origin,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
            }).ToList();
            await session.Weeks.ReplaceContentAsync(plan.Id, goals, assignments, token);

            var overrides = draft.Overrides.ToDictionary(item => item.Date);
            foreach (var date in WeekCalendar.Dates(plan.WeekStartDate))
            {
                if (overrides.TryGetValue(date, out var item))
                {
                    await session.Weeks.UpsertOverrideAsync(context.Profile.Id, date, item.TotalMinutes, item.WorkMinutes, now, token);
                }
                else
                {
                    await session.Weeks.DeleteOverrideAsync(context.Profile.Id, date, token);
                }
            }

            var revision = plan.ConfirmedRevision + 1;
            await session.Weeks.AddRevisionAsync(plan.Id, revision, StoredJsonFormats.Snapshot(new
            {
                goals = draft.Goals,
                assignments = assignments.Select(item => new { item.TaskId, day = item.LocalDate, item.Origin }),
                draft.Overrides,
                draft.Leftovers,
            }), now, token);
            var confirmed = await UpdatePlanAsync(session, plan with
            {
                State = WeekPlanState.Confirmed,
                ConfirmedRevision = revision,
                ConfirmedAtUtc = now,
                DraftPayload = null,
                DraftBaseRevision = null,
                Version = plan.Version + 1,
                UpdatedAtUtc = now,
            }, plan.Version, token);

            // Days that already have a plan follow the new revision at once when nothing
            // differs; otherwise the comparison stays pending for an explicit choice.
            foreach (var day in await session.DayPlans.ListRangeAsync(context.Profile.Id, Max(context.Today, plan.WeekStartDate), weekEnd, token))
            {
                var difference = await ReconciliationAsync(session, confirmed, day, token);
                if (difference is { AddTaskIds.Count: 0, RemoveTaskIds.Count: 0 })
                {
                    await MarkSeededAsync(session, day, confirmed, now, token);
                }
            }

            return 1;
        }, ct);

    /// <summary>
    /// Applies the person's choice from a day comparison: only offered additions and removals
    /// are accepted, and the day then follows the confirmed revision (an empty choice keeps the
    /// day as it is).
    /// </summary>
    public async Task<WeekChangeResult> ApplyReconciliationAsync(WeekReconcileCommand command, CancellationToken ct = default)
    {
        var payload = new
        {
            date = command.Date,
            weekPlanId = command.WeekPlanId,
            revision = command.Revision,
            add = command.AddTaskIds.Order(StringComparer.Ordinal).ToArray(),
            remove = command.RemoveTaskIds.Order(StringComparer.Ordinal).ToArray(),
        };
        return await WriteAsync(command.OperationId, "week.reconcile", payload, command.Date, async (session, context, now, token) =>
        {
            if (command.Date < context.Today)
            {
                throw AppException.Validation("date", "Earlier days are kept as they were.");
            }

            var plan = await session.Weeks.GetAsync(command.WeekPlanId, token);
            if (plan is not { State: WeekPlanState.Confirmed } || plan.ConfirmedRevision != command.Revision || !WeekCalendar.Contains(plan.WeekStartDate, command.Date))
            {
                throw AppException.VersionConflict("The week plan changed since this comparison. Review My Week again.");
            }

            var day = await session.DayPlans.FindAsync(context.Profile.Id, command.Date, token)
                ?? throw AppException.NotFound("This day has no plan yet; it will follow the week when it is opened.");
            var difference = await ReconciliationAsync(session, plan, day, token);
            if (difference is null)
            {
                return 0;
            }

            var offeredAdd = difference.AddTaskIds.ToHashSet(StringComparer.Ordinal);
            var offeredRemove = difference.RemoveTaskIds.ToHashSet(StringComparer.Ordinal);
            if (command.AddTaskIds.Any(id => !offeredAdd.Contains(id)) || command.RemoveTaskIds.Any(id => !offeredRemove.Contains(id)))
            {
                throw AppException.Validation("taskIds", "Choose only tasks from the comparison shown. Refresh My Week and review again.");
            }

            var changed = 0;
            foreach (var taskId in difference.AddTaskIds.Where(command.AddTaskIds.Contains))
            {
                changed += await DayPlanning.PlaceAsync(session, ids, day, taskId, DayPlanItemOrigin.Week, now, token) ? 1 : 0;
            }

            foreach (var taskId in difference.RemoveTaskIds.Where(command.RemoveTaskIds.Contains))
            {
                changed += await DayPlanning.RemoveAsync(session, day, taskId, now, token) ? 1 : 0;
            }

            var current = await session.DayPlans.FindAsync(context.Profile.Id, command.Date, token) ?? day;
            await MarkSeededAsync(session, current, plan, now, token);
            return changed;
        }, ct);
    }

    /// <summary>Records the person's assessment of a confirmed goal (this week's or a past one's).</summary>
    public async Task<WeekChangeResult> SetGoalOutcomeAsync(WeekGoalOutcomeCommand command, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(command.Outcome))
        {
            throw AppException.Validation("outcome", "Choose pending, achieved or let go.");
        }

        RequireOperation(command.OperationId);
        var (weekStart, replayed) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var context = await TaskQueries.ContextAsync(session, now, token);
            return await TaskMutationSupport.ReplayOrRunAsync(session, context.Profile.Id, command.OperationId, "week.goal.outcome", command, now, async inner =>
            {
                var goal = await session.Weeks.GetGoalAsync(command.GoalId, inner) ?? throw AppException.NotFound("This goal no longer exists. Refresh My Week.");
                var plan = await session.Weeks.GetAsync(goal.WeekPlanId, inner);
                if (plan is null || plan.ProfileId != context.Profile.Id)
                {
                    throw AppException.NotFound("This goal no longer exists. Refresh My Week.");
                }

                if (goal.Version != command.ExpectedVersion)
                {
                    throw AppException.VersionConflict("This goal changed in the meantime. Refresh My Week.");
                }

                if (goal.Outcome != command.Outcome
                    && await session.Weeks.UpdateGoalOutcomeAsync(goal with { Outcome = command.Outcome, Version = goal.Version + 1, UpdatedAtUtc = now }, goal.Version, inner) is null)
                {
                    throw AppException.VersionConflict("This goal changed in the meantime. Refresh My Week.");
                }

                return plan.WeekStartDate;
            }, token);
        }, ct);

        return new WeekChangeResult(await OpenAsync(weekStart, ct), replayed);
    }

    /// <summary>Saves the default availability for each weekday (the first-use editor and later changes).</summary>
    public async Task<WeekChangeResult> SetAvailabilityAsync(WeekAvailabilityCommand command, CancellationToken ct = default)
    {
        RequireOperation(command.OperationId);
        if (command.Days.Count is 0 or > WeekCalendar.DaysInWeek || command.Days.Select(day => day.Weekday).Distinct().Count() != command.Days.Count)
        {
            throw AppException.Validation("days", "Give each weekday at most once.");
        }

        for (var index = 0; index < command.Days.Count; index++)
        {
            var day = command.Days[index];
            if (!Enum.IsDefined(day.Weekday))
            {
                throw AppException.Validation($"days[{index}].weekday", "Choose a weekday.");
            }

            if (new DayAvailability(day.TotalMinutes, day.WorkMinutes).Problem() is { } problem)
            {
                throw AppException.Validation($"days[{index}]", problem);
            }
        }

        var normalized = command with { Days = [.. command.Days.OrderBy(day => day.Weekday)] };
        var (_, replayed) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var context = await TaskQueries.ContextAsync(session, now, token);
            return await TaskMutationSupport.ReplayOrRunAsync(session, context.Profile.Id, normalized.OperationId, "week.availability", normalized, now, async inner =>
            {
                foreach (var day in normalized.Days)
                {
                    await session.Weeks.UpsertRuleAsync(context.Profile.Id, day.Weekday, day.TotalMinutes, day.WorkMinutes, now, inner);
                }

                return normalized.Days.Count;
            }, token);
        }, ct);

        return new WeekChangeResult(await OpenAsync(null, ct), replayed);
    }

    /// <summary>The current and future plans a first-weekday change would cut across; they need an explicit remap.</summary>
    public async Task<WeekStartPreview> PreviewWeekStartAsync(DayOfWeek firstWeekday, CancellationToken ct = default)
    {
        RequireWeekday(firstWeekday);
        return await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            var conflicts = new List<WeekStartConflict>();
            foreach (var plan in await MisalignedPlansAsync(session, context, firstWeekday, token))
            {
                var content = await ContentOfAsync(session, plan, token);
                if (plan.State == WeekPlanState.Confirmed || plan.DraftPayload is not null)
                {
                    conflicts.Add(new WeekStartConflict(plan.WeekStartDate, plan.State, plan.DraftPayload is not null, content.Assignments.Count, content.Goals.Count));
                }
            }

            return new WeekStartPreview(context.Profile.FirstWeekday, firstWeekday, conflicts, WeekCalendar.StartOf(context.Today, firstWeekday));
        }, ct);
    }

    /// <summary>
    /// Changes the first weekday. Historical plans keep their dates. A current or future plan
    /// that would cross the new boundary is only changed with <see cref="WeekStartCommand.Remap"/>:
    /// it is marked superseded (its rows and revisions stay as history) and its remaining work
    /// moves, by date, into drafts on the new boundaries for the person to confirm again.
    /// </summary>
    public async Task<WeekStartResult> ChangeWeekStartAsync(WeekStartCommand command, CancellationToken ct = default)
    {
        RequireOperation(command.OperationId);
        RequireWeekday(command.FirstWeekday);
        var (outcome, replayed) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var context = await TaskQueries.ContextAsync(session, now, token);
            return await TaskMutationSupport.ReplayOrRunAsync(session, context.Profile.Id, command.OperationId, "week.first-weekday", command, now, async inner =>
            {
                var profile = context.Profile;
                if (profile.FirstWeekday == command.FirstWeekday)
                {
                    return new WeekStartResult(command.FirstWeekday, [], Replayed: false);
                }

                var misaligned = await MisalignedPlansAsync(session, context, command.FirstWeekday, inner);
                var withContent = misaligned.Where(plan => plan.State == WeekPlanState.Confirmed || plan.DraftPayload is not null).ToList();
                if (withContent.Count > 0 && !command.Remap)
                {
                    throw AppException.Validation("remap", "Some planned weeks cross the new week boundary. Review the preview and choose to remap them.");
                }

                // Collect remaining work by date before superseding anything.
                var moved = new List<(DateOnly Target, WeekDraftAssignment? Assignment, WeekDraftGoal? Goal)>();
                foreach (var plan in misaligned)
                {
                    var content = await ContentOfAsync(session, plan, inner);
                    var poolTarget = WeekCalendar.StartOf(Max(context.Today, plan.WeekStartDate), command.FirstWeekday);
                    foreach (var assignment in content.Assignments)
                    {
                        if (assignment.Day is { } day && day < context.Today)
                        {
                            continue; // past days stay in the superseded plan's history
                        }

                        moved.Add((assignment.Day is { } d ? WeekCalendar.StartOf(d, command.FirstWeekday) : poolTarget, assignment, null));
                    }

                    moved.AddRange(content.Goals.Select(goal => (poolTarget, (WeekDraftAssignment?)null, (WeekDraftGoal?)goal)));
                    await UpdatePlanAsync(session, plan with { State = WeekPlanState.Superseded, DraftPayload = null, DraftBaseRevision = null, Version = plan.Version + 1, UpdatedAtUtc = now }, plan.Version, inner);
                }

                if (await session.Profiles.UpdateFirstWeekdayAsync(profile with { FirstWeekday = command.FirstWeekday, Version = profile.Version + 1, UpdatedAtUtc = now }, profile.Version, inner) is null)
                {
                    throw AppException.VersionConflict("Settings changed in the meantime. Try again.");
                }

                var updatedContext = context with { Profile = profile with { FirstWeekday = command.FirstWeekday, Version = profile.Version + 1 } };
                var targets = new List<DateOnly>();
                foreach (var group in moved.GroupBy(item => item.Target).OrderBy(group => group.Key))
                {
                    var plan = await EnsureWeekAsync(session, updatedContext, group.Key, now, inner);
                    var draft = ReadDraft(plan) ?? (plan.State == WeekPlanState.Confirmed ? (await ContentOfAsync(session, plan, inner)).AsDraft() : WeekDraft.Empty);
                    var assignments = draft.Assignments.ToList();
                    var goals = draft.Goals.ToList();
                    foreach (var (_, assignment, goal) in group)
                    {
                        if (assignment is not null && assignments.All(item => item.TaskId != assignment.TaskId))
                        {
                            assignments.Add(assignment);
                        }

                        if (goal is not null && goals.All(item => item.Id != goal.Id))
                        {
                            goals.Add(goal with { Id = StorageFormats.Id(ids.NewId()) });
                        }
                    }

                    await UpdatePlanAsync(session, plan with
                    {
                        DraftPayload = StoredJsonFormats.Snapshot(draft with { Assignments = assignments, Goals = goals, Step = 4 }),
                        DraftRevision = plan.DraftRevision + 1,
                        DraftBaseRevision = plan.DraftBaseRevision ?? plan.ConfirmedRevision,
                        Version = plan.Version + 1,
                        UpdatedAtUtc = now,
                    }, plan.Version, inner);
                    targets.Add(group.Key);
                }

                return new WeekStartResult(command.FirstWeekday, targets, Replayed: false);
            }, token);
        }, ct);

        return outcome with { Replayed = replayed };
    }

    // ---------------------------------------------------------------- internals

    private sealed record WeekContent(IReadOnlyList<WeekDraftGoal> Goals, IReadOnlyList<WeekDraftAssignment> Assignments, IReadOnlyList<WeekDraftOverride> Overrides)
    {
        public WeekDraft AsDraft() => new(Goals, Assignments, [], Overrides, 4);
    }

    /// <summary>The plan's latest intent: its draft when there is one, otherwise its confirmed rows.</summary>
    private static async Task<WeekContent> ContentOfAsync(IDataSession session, WeekPlanRecord plan, CancellationToken token)
    {
        if (ReadDraft(plan) is { } draft)
        {
            return new WeekContent(draft.Goals, draft.Assignments, draft.Overrides);
        }

        if (plan.State != WeekPlanState.Confirmed)
        {
            return new WeekContent([], [], []);
        }

        var overrides = await session.Weeks.ListOverridesAsync(plan.ProfileId, plan.WeekStartDate, WeekCalendar.EndOf(plan.WeekStartDate), token);
        return new WeekContent(
            [.. (await session.Weeks.ListGoalsAsync(plan.Id, token)).Select(goal => new WeekDraftGoal(goal.Id, goal.Title, goal.ProjectId, goal.TaskIds, goal.Outcome))],
            [.. (await session.Weeks.ListAssignmentsAsync(plan.Id, token)).Select(item => new WeekDraftAssignment(item.TaskId, item.LocalDate))],
            [.. overrides.Select(item => new WeekDraftOverride(item.Date!.Value, item.TotalMinutes, item.WorkMinutes))]);
    }

    /// <summary>Non-superseded plans that end today or later and start on a different weekday than <paramref name="firstWeekday"/>.</summary>
    private static async Task<IReadOnlyList<WeekPlanRecord>> MisalignedPlansAsync(IDataSession session, TaskQueries.QueryContext context, DayOfWeek firstWeekday, CancellationToken token) =>
        [.. (await session.Weeks.ListOverlappingAsync(context.Profile.Id, context.Today, context.Today.AddYears(10), token))
            .Where(plan => plan.WeekStartDate.DayOfWeek != firstWeekday)];

    private async Task<WeekChangeResult> WriteAsync(
        string operationId,
        string commandKind,
        object payload,
        DateOnly date,
        Func<IDataSession, TaskQueries.QueryContext, DateTimeOffset, CancellationToken, Task<int>> body,
        CancellationToken ct)
    {
        RequireOperation(operationId);
        var (_, replayed) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var context = await TaskQueries.ContextAsync(session, now, token);
            return await TaskMutationSupport.ReplayOrRunAsync(session, context.Profile.Id, operationId, commandKind, payload, now, inner => body(session, context, now, inner), token);
        }, ct);

        return new WeekChangeResult(await OpenAsync(date, ct), replayed);
    }

    /// <summary>
    /// The plan row for a week that can still be planned (it ends today or later). A date no
    /// plan covers starts a week on the profile's first weekday; that new week may not share a
    /// current or future date with another active plan.
    /// </summary>
    private async Task<WeekPlanRecord> EnsureWeekAsync(IDataSession session, TaskQueries.QueryContext context, DateOnly weekStart, DateTimeOffset now, CancellationToken token)
    {
        var profileId = context.Profile.Id;
        var weekEnd = WeekCalendar.EndOf(weekStart);
        if (weekEnd < context.Today)
        {
            throw AppException.Validation("weekStart", "Earlier weeks are kept for review and cannot be changed.");
        }

        var existing = await session.Weeks.FindByStartAsync(profileId, weekStart, token);
        if (existing is { State: not WeekPlanState.Superseded })
        {
            return existing;
        }

        if (weekStart.DayOfWeek != context.Profile.FirstWeekday)
        {
            throw AppException.Validation("weekStart", "Weeks start on your first weekday. Refresh My Week.");
        }

        foreach (var other in await session.Weeks.ListOverlappingAsync(profileId, weekStart, weekEnd, token))
        {
            var sharedEnd = Min(weekEnd, WeekCalendar.EndOf(other.WeekStartDate));
            if (sharedEnd >= context.Today)
            {
                throw AppException.Validation("weekStart", $"This week overlaps the plan starting {StorageFormats.Date(other.WeekStartDate)}. Change the first weekday through its preview to remap it.");
            }
        }

        if (existing is not null)
        {
            // A week that was superseded earlier starts over as a draft-only plan; its old
            // revisions stay as history.
            await session.Weeks.ReplaceContentAsync(existing.Id, [], [], token);
            return await UpdatePlanAsync(session, existing with { State = WeekPlanState.Draft, DraftPayload = null, DraftBaseRevision = null, Version = existing.Version + 1, UpdatedAtUtc = now }, existing.Version, token);
        }

        await session.Weeks.TryAddAsync(new WeekPlanRecord
        {
            Id = StorageFormats.Id(ids.NewId()),
            ProfileId = profileId,
            WeekStartDate = weekStart,
            State = WeekPlanState.Draft,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }, token);
        return await session.Weeks.FindByStartAsync(profileId, weekStart, token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "My Week could not be prepared. Try again.");
    }

    private static async Task<WeekPlanRecord> UpdatePlanAsync(IDataSession session, WeekPlanRecord plan, long expectedVersion, CancellationToken token) =>
        await session.Weeks.UpdateAsync(plan, expectedVersion, token)
        ?? throw AppException.VersionConflict("My Week changed in the meantime. Refresh and try again.");

    private static void RequireDraftRevision(WeekPlanRecord plan, long expected)
    {
        if (plan.DraftRevision != expected)
        {
            throw AppException.VersionConflict("This week's draft was changed elsewhere. Refresh My Week to continue from the latest draft.");
        }
    }

    private static void RequireOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw AppException.Validation("operationId", "An operation ID is required.");
        }
    }

    private static void RequireWeekday(DayOfWeek weekday)
    {
        if (!Enum.IsDefined(weekday))
        {
            throw AppException.Validation("firstWeekday", "Choose a weekday.");
        }
    }

    private static WeekDraft? ReadDraft(WeekPlanRecord plan) =>
        plan.DraftPayload is null ? null : StoredJsonFormats.ReadSnapshot<WeekDraft>(plan.DraftPayload);

    /// <summary>Validates and normalizes a draft against the plan and the current tasks.</summary>
    private static async Task<WeekDraft> ValidateDraftAsync(IDataSession session, TaskQueries.QueryContext context, WeekPlanRecord plan, WeekDraft draft, CancellationToken token)
    {
        var start = plan.WeekStartDate;
        var goals = draft.Goals ?? [];
        var assignments = draft.Assignments ?? [];
        var leftovers = draft.Leftovers ?? [];
        var overrides = draft.Overrides ?? [];
        if (goals.Count > MaxGoals)
        {
            throw AppException.Validation("goals", $"A week can hold at most {MaxGoals} goals.");
        }

        if (assignments.Count > MaxAssignments || leftovers.Count > MaxAssignments)
        {
            throw AppException.Validation("assignments", $"A week can hold at most {MaxAssignments} tasks.");
        }

        if (draft.Step is < 1 or > 5)
        {
            throw AppException.Validation("step", "The planning step must be between 1 and 5.");
        }

        async Task<TaskRecord> TaskAsync(string? taskId, string field)
        {
            var task = string.IsNullOrWhiteSpace(taskId) ? null : await session.Tasks.GetAsync(taskId, token);
            return task is not null && task.SpaceId == context.Space.Id
                ? task
                : throw AppException.Validation(field, "One of the chosen tasks no longer exists. Refresh My Week and review the plan.");
        }

        var confirmedDays = new Dictionary<string, DateOnly?>(StringComparer.Ordinal);
        if (plan.State == WeekPlanState.Confirmed)
        {
            foreach (var item in await session.Weeks.ListAssignmentsAsync(plan.Id, token))
            {
                confirmedDays[item.TaskId] = item.LocalDate;
            }
        }

        var normalizedGoals = new List<WeekDraftGoal>();
        var goalIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < goals.Count; index++)
        {
            var goal = goals[index];
            var field = $"goals[{index}]";
            if (!Guid.TryParse(goal.Id, out var goalGuid) || !goalIds.Add(StorageFormats.Id(goalGuid)))
            {
                throw AppException.Validation($"{field}.id", "Each goal needs its own ID.");
            }

            string title;
            try
            {
                title = TaskTitle.Normalize(goal.Title);
            }
            catch (DomainValidationException)
            {
                throw AppException.Validation($"{field}.title", $"A goal needs a title of 1 to {TaskTitle.MaxUnicodeScalarValues} characters.");
            }

            if (!Enum.IsDefined(goal.Outcome))
            {
                throw AppException.Validation($"{field}.outcome", "Choose pending, achieved or let go.");
            }

            var projectId = string.IsNullOrWhiteSpace(goal.ProjectId) ? null : goal.ProjectId;
            if (projectId is not null)
            {
                var project = await session.Containers.GetAsync(projectId, token);
                if (project is null || project.Kind != ContainerKind.Project || project.SpaceId != context.Space.Id)
                {
                    throw AppException.Validation($"{field}.projectId", "Choose one of your projects, or none.");
                }
            }

            var taskIds = (goal.TaskIds ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (taskIds.Count > MaxGoalTasks)
            {
                throw AppException.Validation($"{field}.taskIds", $"A goal can link at most {MaxGoalTasks} tasks.");
            }

            foreach (var taskId in taskIds)
            {
                await TaskAsync(taskId, $"{field}.taskIds");
            }

            normalizedGoals.Add(new WeekDraftGoal(StorageFormats.Id(goalGuid), title, projectId, taskIds, goal.Outcome));
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < assignments.Count; index++)
        {
            var item = assignments[index];
            var field = $"assignments[{index}]";
            await TaskAsync(item.TaskId, $"{field}.taskId");
            if (!placed.Add(item.TaskId))
            {
                throw AppException.Validation($"{field}.taskId", "A task can be on only one day of the week.");
            }

            if (item.Day is { } day)
            {
                if (!WeekCalendar.Contains(start, day))
                {
                    throw AppException.Validation($"{field}.day", "Choose one of this week's seven days.");
                }

                // Earlier days are kept as they were: a past-day assignment may stay, not be added or moved there.
                if (day < context.Today && !(confirmedDays.TryGetValue(item.TaskId, out var before) && before == day))
                {
                    throw AppException.Validation($"{field}.day", "Earlier days cannot receive new work. Choose today or a later day.");
                }
            }
        }

        var decided = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < leftovers.Count; index++)
        {
            var item = leftovers[index];
            var field = $"leftovers[{index}]";
            var task = await TaskAsync(item.TaskId, $"{field}.taskId");
            if (!decided.Add(item.TaskId))
            {
                throw AppException.Validation($"{field}.taskId", "Decide each unfinished task once.");
            }

            if (!Enum.IsDefined(item.Decision))
            {
                throw AppException.Validation($"{field}.decision", "Choose carry, later, remove or cancel.");
            }

            if (item.Decision != LeftoverDecision.Carry && placed.Contains(item.TaskId))
            {
                throw AppException.Validation($"{field}.decision", $"\"{task.Title}\" is planned this week. Carry it, or take it off the week first.");
            }

            if (item.LaterDate is not null && item.Decision != LeftoverDecision.Later)
            {
                throw AppException.Validation($"{field}.laterDate", "A new planned date belongs to a \"later\" decision.");
            }

            if (item.LaterDate is { } later && later < context.Today)
            {
                throw AppException.Validation($"{field}.laterDate", "Choose today or a later date.");
            }

            var recurring = task.RecurrenceOccurrenceKey is not null;
            if (recurring && (item.Decision == LeftoverDecision.Cancel || item.LaterDate is not null))
            {
                throw AppException.Validation($"{field}.decision", $"\"{task.Title}\" repeats. Use its repeat controls to skip or move this occurrence.");
            }
        }

        var overrideDates = new HashSet<DateOnly>();
        for (var index = 0; index < overrides.Count; index++)
        {
            var item = overrides[index];
            var field = $"overrides[{index}]";
            if (!WeekCalendar.Contains(start, item.Date) || !overrideDates.Add(item.Date))
            {
                throw AppException.Validation($"{field}.date", "Set availability once for each of this week's days.");
            }

            if (new DayAvailability(item.TotalMinutes, item.WorkMinutes).Problem() is { } problem)
            {
                throw AppException.Validation(field, problem);
            }
        }

        return new WeekDraft(normalizedGoals, [.. assignments], [.. leftovers], [.. overrides.OrderBy(item => item.Date)], draft.Step);
    }

    private async Task CancelTaskAsync(IDataSession session, string taskId, string operationId, DateTimeOffset now, CancellationToken token)
    {
        var task = await session.Tasks.GetAsync(taskId, token);
        if (task is null || task.IsTerminal)
        {
            return; // already finished elsewhere: nothing to cancel
        }

        var before = await RecordingDataSession.BuildAggregateAsync(session, task.Id, token);
        var updated = await session.Tasks.UpdateAsync(task with
        {
            Status = TaskItemStatus.Cancelled,
            CancelledAtUtc = now,
            CompletedAtUtc = null,
            Version = task.Version + 1,
            UpdatedAtUtc = now,
        }, task.Version, token) ?? throw AppException.VersionConflict($"\"{task.Title}\" changed in the meantime. Review the week again.");
        await WriteHistoryAsync(session, operationId, before, updated, "task.cancelled", ["Status"], now, token);
    }

    private async Task ReplanTaskAsync(IDataSession session, string taskId, DateOnly plannedDate, string operationId, DateTimeOffset now, CancellationToken token)
    {
        var task = await session.Tasks.GetAsync(taskId, token);
        if (task is null || task.PlannedDate == plannedDate)
        {
            return;
        }

        var before = await RecordingDataSession.BuildAggregateAsync(session, task.Id, token);
        var updated = await session.Tasks.UpdateAsync(task with { PlannedDate = plannedDate, Version = task.Version + 1, UpdatedAtUtc = now }, task.Version, token)
            ?? throw AppException.VersionConflict($"\"{task.Title}\" changed in the meantime. Review the week again.");
        await WriteHistoryAsync(session, operationId, before, updated, "task.updated", ["PlannedDate"], now, token);
    }

    private async Task WriteHistoryAsync(IDataSession session, string operationId, TaskAggregateSnapshot? before, TaskRecord updated, string eventKind, IReadOnlyList<string> fields, DateTimeOffset now, CancellationToken token)
    {
        var after = await RecordingDataSession.BuildAggregateAsync(session, updated.Id, token) ?? new TaskAggregateSnapshot(updated, []);
        await session.History.AddAsync(new TaskHistoryRecord
        {
            Id = StorageFormats.Id(ids.NewId()),
            TaskId = updated.Id,
            OperationId = operationId,
            EffectiveAtUtc = now,
            Actor = "local",
            EventKind = eventKind,
            ChangedFields = fields,
            SnapshotBefore = before is null ? null : StoredJsonFormats.Snapshot(before),
            SnapshotAfter = StoredJsonFormats.Snapshot(after),
            AggregateVersion = updated.Version,
            RecordedAtUtc = now,
        }, token);
    }

    private static async Task MarkSeededAsync(IDataSession session, DayPlanRecord day, WeekPlanRecord week, DateTimeOffset now, CancellationToken token)
    {
        if (day.SeededWeekPlanId == week.Id && day.SeededWeekRevision == week.ConfirmedRevision)
        {
            return;
        }

        if (await session.DayPlans.UpdateSeedAsync(day with { SeededWeekPlanId = week.Id, SeededWeekRevision = week.ConfirmedRevision, Version = day.Version + 1, UpdatedAtUtc = now }, day.Version, token) is null)
        {
            throw AppException.VersionConflict("My Day changed in the meantime. Refresh and try again.");
        }
    }

    /// <summary>Null when the day already follows this revision; otherwise the add/remove comparison (possibly empty).</summary>
    private static async Task<DayReconciliation?> ReconciliationAsync(IDataSession session, WeekPlanRecord week, DayPlanRecord day, CancellationToken token)
    {
        if (day.SeededWeekPlanId == week.Id && day.SeededWeekRevision == week.ConfirmedRevision)
        {
            return null;
        }

        var assigned = await DayPlanning.AssignedOpenTaskIdsAsync(session, week.Id, day.LocalDate, token);
        var assignedSet = assigned.ToHashSet(StringComparer.Ordinal);
        var items = await session.DayPlans.ListItemsAsync(day.Id, includeRemoved: true, token);
        var known = items.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        var add = assigned.Where(taskId => !known.Contains(taskId)).ToList();
        var remove = new List<string>();
        foreach (var item in items)
        {
            if (item is { RemovedAtUtc: null, Origin: DayPlanItemOrigin.Week } && !assignedSet.Contains(item.TaskId)
                && await session.Tasks.GetAsync(item.TaskId, token) is { IsTerminal: false })
            {
                remove.Add(item.TaskId);
            }
        }

        return new DayReconciliation(day.LocalDate, week.Id, week.ConfirmedRevision, add, remove);
    }

    // ---------------------------------------------------------------- read model

    private sealed class TaskCache(IDataSession session, CancellationToken token)
    {
        private readonly Dictionary<string, WeekTask?> _tasks = new(StringComparer.Ordinal);

        public IEnumerable<WeekTask> Values => _tasks.Values.OfType<WeekTask>().OrderBy(task => task.Task.Id, StringComparer.Ordinal);

        public void Remember(TaskRecord task, string containerName, bool archived) => _tasks.TryAdd(task.Id, new WeekTask(task, containerName, archived));

        public async Task<WeekTask?> GetAsync(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var known))
            {
                return known;
            }

            WeekTask? found = null;
            if (await session.Tasks.GetAsync(taskId, token) is { } task)
            {
                var container = await session.Containers.GetIncludingDeletedAsync(task.ContainerId, token);
                found = new WeekTask(task, container?.Name ?? "", container?.ArchivedAtUtc is not null || container?.DeletedAtUtc is not null);
            }

            _tasks[taskId] = found;
            return found;
        }
    }

    private static async Task<WeekOverview> OverviewAsync(IDataSession session, TaskQueries.QueryContext context, DateOnly date, CancellationToken token)
    {
        var profile = context.Profile;
        var today = context.Today;
        var plan = await WeekResolution.PlanContainingAsync(session, profile.Id, date, token);
        var start = plan?.WeekStartDate ?? WeekCalendar.StartOf(date, profile.FirstWeekday);
        var end = WeekCalendar.EndOf(start);
        var cache = new TaskCache(session, token);
        var rules = await session.Weeks.ListRulesAsync(profile.Id, token);
        var storedOverrides = (await session.Weeks.ListOverridesAsync(profile.Id, start, end, token))
            .Select(item => new WeekDraftOverride(item.Date!.Value, item.TotalMinutes, item.WorkMinutes)).ToList();

        var confirmed = plan is { State: WeekPlanState.Confirmed }
            ? await ContentOfConfirmedAsync(session, plan, token)
            : (Goals: (IReadOnlyList<WeekGoalRecord>)[], Assignments: (IReadOnlyList<WeekAssignmentRecord>)[]);
        var confirmedBoard = await BoardAsync(session, cache, start, rules, storedOverrides,
            [.. confirmed.Goals.Select(goal => (goal.Id, goal.Title, goal.ProjectId, goal.TaskIds, goal.Outcome, goal.Version))],
            [.. confirmed.Assignments.Select(item => new WeekPlacement(item.TaskId, item.LocalDate, item.Origin))], token);

        var draft = plan is null ? null : ReadDraft(plan);
        WeekBoard? draftBoard = null;
        if (draft is not null)
        {
            var versions = confirmed.Goals.ToDictionary(goal => goal.Id, goal => goal.Version, StringComparer.Ordinal);
            draftBoard = await BoardAsync(session, cache, start, rules, draft.Overrides,
                [.. draft.Goals.Select(goal => (goal.Id, goal.Title, goal.ProjectId, goal.TaskIds, goal.Outcome, versions.GetValueOrDefault(goal.Id)))],
                [.. draft.Assignments.Select(item => new WeekPlacement(item.TaskId, item.Day, WeekAssignmentOrigin.Planned))], token);
            foreach (var leftover in draft.Leftovers)
            {
                await cache.GetAsync(leftover.TaskId);
            }
        }

        var lookBack = await LookBackAsync(session, context, cache, start, token);
        var editable = end >= today;
        var inWeek = (draft?.Assignments.Select(item => item.TaskId) ?? confirmed.Assignments.Select(item => item.TaskId))
            .Concat(lookBack.UnfinishedTaskIds)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = editable ? await CandidatesAsync(session, context, cache, start, inWeek, token) : [];
        var suggestions = editable ? await GoalSuggestionsAsync(session, context, cache, start, lookBack, draft?.Goals ?? [], token) : [];

        var pending = new List<DayReconciliation>();
        WeekCheckIn? checkIn = null;
        if (plan is { State: WeekPlanState.Confirmed } && editable)
        {
            foreach (var day in await session.DayPlans.ListRangeAsync(profile.Id, Max(today, start), end, token))
            {
                if (await ReconciliationAsync(session, plan, day, token) is { } difference && (difference.AddTaskIds.Count > 0 || difference.RemoveTaskIds.Count > 0))
                {
                    pending.Add(difference);
                    foreach (var taskId in difference.AddTaskIds.Concat(difference.RemoveTaskIds))
                    {
                        await cache.GetAsync(taskId);
                    }
                }
            }

            if (WeekCalendar.Contains(start, today))
            {
                checkIn = await CheckInAsync(session, context, confirmedBoard, today, start, token);
            }
        }

        return new WeekOverview(
            start, end, today, profile.FirstWeekday, plan?.Id, plan?.State, plan?.ConfirmedRevision ?? 0, plan?.ConfirmedAtUtc,
            editable, rules.Count > 0, confirmedBoard, draft, plan?.DraftRevision ?? 0, draftBoard, lookBack, candidates, suggestions, pending, checkIn,
            [.. cache.Values]);
    }

    private static async Task<(IReadOnlyList<WeekGoalRecord> Goals, IReadOnlyList<WeekAssignmentRecord> Assignments)> ContentOfConfirmedAsync(IDataSession session, WeekPlanRecord plan, CancellationToken token) =>
        (await session.Weeks.ListGoalsAsync(plan.Id, token), await session.Weeks.ListAssignmentsAsync(plan.Id, token));

    private static DayAvailability AvailabilityOn(DateOnly date, IReadOnlyList<AvailabilityRecord> rules, IReadOnlyList<WeekDraftOverride> overrides, out bool isOverride)
    {
        if (overrides.FirstOrDefault(item => item.Date == date) is { } chosen)
        {
            isOverride = true;
            return new DayAvailability(chosen.TotalMinutes, chosen.WorkMinutes);
        }

        isOverride = false;
        return rules.FirstOrDefault(rule => rule.Weekday == date.DayOfWeek) is { } rule
            ? new DayAvailability(rule.TotalMinutes, rule.WorkMinutes)
            : DayAvailability.DefaultFor(date.DayOfWeek);
    }

    private static async Task<WeekBoard> BoardAsync(
        IDataSession session,
        TaskCache cache,
        DateOnly start,
        IReadOnlyList<AvailabilityRecord> rules,
        IReadOnlyList<WeekDraftOverride> overrides,
        IReadOnlyList<(string Id, string Title, string? ProjectId, IReadOnlyList<string> TaskIds, WeekGoalOutcome Outcome, long Version)> goals,
        IReadOnlyList<WeekPlacement> placements,
        CancellationToken token)
    {
        var items = new Dictionary<string, CapacityItem>(StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            if (await cache.GetAsync(placement.TaskId) is { } task)
            {
                items[placement.TaskId] = new CapacityItem(task.Task.EstimateMinutes, task.Task.CapacityCategory, !task.Task.IsTerminal);
            }
        }

        IEnumerable<CapacityItem> On(DateOnly? day) =>
            placements.Where(item => item.Day == day && items.ContainsKey(item.TaskId)).Select(item => items[item.TaskId]);

        var days = WeekCalendar.Dates(start).Select(date =>
        {
            var availability = AvailabilityOn(date, rules, overrides, out var isOverride);
            return new WeekDayLoad(date, availability, isOverride, Capacity.Measure(availability, On(date)));
        }).ToList();
        var pool = Capacity.Measure(new DayAvailability(int.MaxValue, null), On(null));

        var progress = new List<WeekGoalProgress>();
        foreach (var goal in goals)
        {
            int done = 0, counted = 0;
            foreach (var taskId in goal.TaskIds)
            {
                if (await cache.GetAsync(taskId) is not { } linked || linked.Task.Status == TaskItemStatus.Cancelled)
                {
                    continue;
                }

                counted++;
                done += linked.Task.Status == TaskItemStatus.Done ? 1 : 0;
            }

            var project = goal.ProjectId is null ? null : await session.Containers.GetIncludingDeletedAsync(goal.ProjectId, token);
            progress.Add(new WeekGoalProgress(goal.Id, goal.Title, goal.ProjectId, project?.Name, goal.TaskIds, goal.Outcome, done, counted, goal.Version));
        }

        return new WeekBoard(progress, placements, days, pool);
    }

    /// <summary>
    /// The reusable historical-facts query for one planned week (PLAN §15 WP9): tasks completed
    /// during the seven local dates, and the previous plan's assignments and goals as they stand.
    /// </summary>
    private static async Task<WeekLookBack> LookBackAsync(IDataSession session, TaskQueries.QueryContext context, TaskCache cache, DateOnly weekStart, CancellationToken token)
    {
        var previous = await WeekResolution.ConfirmedContainingAsync(session, context.Profile.Id, weekStart.AddDays(-1), token);
        var start = previous?.WeekStartDate ?? WeekCalendar.StartOf(weekStart.AddDays(-1), context.Profile.FirstWeekday);
        var end = Min(WeekCalendar.EndOf(start), weekStart.AddDays(-1));
        var completedSpec = new TaskQuerySpec { View = TaskView.RecentlyCompleted, RecentDays = 3660, Completed = new LocalDateRange(start, end), IncludeArchived = true };
        var criteria = await TaskQueries.ResolveAsync(session, context, completedSpec, token);
        var completed = await session.Queries.QueryAsync(criteria, token);
        foreach (var row in completed.Rows)
        {
            cache.Remember(row.Task, row.ContainerName, row.ContainerArchived);
        }

        var completedCount = await session.Queries.CountAsync(criteria, token);
        if (previous is null)
        {
            return new WeekLookBack(start, end, false, [.. completed.Rows.Select(row => row.Task.Id)], completedCount, 0, 0, 0, [], []);
        }

        int assigned = 0, done = 0, cancelled = 0;
        var unfinished = new List<string>();
        foreach (var assignment in await session.Weeks.ListAssignmentsAsync(previous.Id, token))
        {
            if (await cache.GetAsync(assignment.TaskId) is not { } task)
            {
                continue; // deleted since
            }

            assigned++;
            switch (task.Task.Status)
            {
                case TaskItemStatus.Done: done++; break;
                case TaskItemStatus.Cancelled: cancelled++; break;
                default: unfinished.Add(task.Task.Id); break;
            }
        }

        var goals = await session.Weeks.ListGoalsAsync(previous.Id, token);
        var board = await BoardAsync(session, cache, previous.WeekStartDate, [], [],
            [.. goals.Select(goal => (goal.Id, goal.Title, goal.ProjectId, goal.TaskIds, goal.Outcome, goal.Version))], [], token);
        return new WeekLookBack(previous.WeekStartDate, WeekCalendar.EndOf(previous.WeekStartDate), true,
            [.. completed.Rows.Select(row => row.Task.Id)], completedCount, assigned, done, cancelled, unfinished, board.Goals);
    }

    /// <summary>Open tasks worth planning this week that it does not hold yet: overdue, due or planned in the week, chosen for today.</summary>
    private static async Task<IReadOnlyList<WeekCandidate>> CandidatesAsync(
        IDataSession session, TaskQueries.QueryContext context, TaskCache cache, DateOnly start, HashSet<string> inWeek, CancellationToken token)
    {
        var end = WeekCalendar.EndOf(start);
        var found = new List<WeekCandidate>();
        var seen = new HashSet<string>(inWeek, StringComparer.Ordinal);

        async Task OfferAsync(TaskQuerySpec spec, WeekCandidateReason reason)
        {
            var page = await session.Queries.QueryAsync(await TaskQueries.ResolveAsync(session, context, spec with { Limit = TaskQueries.MaxLimit }, token), token);
            foreach (var row in page.Rows)
            {
                if (!row.Task.IsTerminal && !row.ContainerArchived && seen.Add(row.Task.Id))
                {
                    cache.Remember(row.Task, row.ContainerName, row.ContainerArchived);
                    found.Add(new WeekCandidate(row.Task.Id, reason));
                }
            }
        }

        await OfferAsync(new TaskQuerySpec { View = TaskView.Overdue }, WeekCandidateReason.Overdue);
        await OfferAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Due = new LocalDateRange(start, end) }, WeekCandidateReason.DueThisWeek);
        await OfferAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Planned = new LocalDateRange(start, end) }, WeekCandidateReason.PlannedThisWeek);
        if (WeekCalendar.Contains(start, context.Today))
        {
            await OfferAsync(new TaskQuerySpec { View = TaskView.MyDay, Day = context.Today }, WeekCandidateReason.OnMyDayToday);
        }

        return found;
    }

    /// <summary>
    /// Two or three text goals to start from: last week's goals still pending, projects with a
    /// deadline soon, and important tasks due this week. Suggestions are never added by themselves.
    /// </summary>
    private static async Task<IReadOnlyList<WeekGoalSuggestion>> GoalSuggestionsAsync(
        IDataSession session, TaskQueries.QueryContext context, TaskCache cache, DateOnly start, WeekLookBack lookBack, IReadOnlyList<WeekDraftGoal> chosen, CancellationToken token)
    {
        const int Wanted = 3;
        var end = WeekCalendar.EndOf(start);
        var titles = chosen.Select(goal => goal.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<WeekGoalSuggestion>();
        void Offer(WeekGoalSuggestion suggestion)
        {
            if (suggestions.Count < Wanted && titles.Add(suggestion.Title))
            {
                suggestions.Add(suggestion);
            }
        }

        foreach (var goal in lookBack.Goals.Where(goal => goal.Outcome == WeekGoalOutcome.Pending && (goal.CountedCount == 0 || goal.DoneCount < goal.CountedCount)))
        {
            var open = new List<string>();
            foreach (var taskId in goal.TaskIds)
            {
                if (await cache.GetAsync(taskId) is { Task.IsTerminal: false })
                {
                    open.Add(taskId);
                }
            }

            Offer(new WeekGoalSuggestion(goal.Title, goal.ProjectId, open, WeekGoalSuggestionReason.ContinueGoal));
        }

        foreach (var project in (await session.Containers.ListBySpaceAsync(context.Space.Id, token))
            .Where(container => container is { Kind: ContainerKind.Project, ArchivedAtUtc: null, DeadlineDate: { } deadline } && deadline >= context.Today && deadline <= end.AddDays(7))
            .OrderBy(container => container.DeadlineDate).ThenBy(container => container.Id, StringComparer.Ordinal))
        {
            Offer(new WeekGoalSuggestion($"Move {project.Name} forward before {StorageFormats.Date(project.DeadlineDate!.Value)}", project.Id, [], WeekGoalSuggestionReason.ProjectDeadline));
        }

        var important = await session.Queries.QueryAsync(await TaskQueries.ResolveAsync(session, context, new TaskQuerySpec
        {
            View = TaskView.AssignedToMe,
            Due = new LocalDateRange(start, end),
            Priorities = [TaskPriority.Urgent, TaskPriority.High],
            Limit = TaskQueries.MaxLimit,
        }, token), token);
        foreach (var row in important.Rows.OrderByDescending(row => row.Task.Priority).ThenBy(row => row.Task.DueDate).ThenBy(row => row.Task.Id, StringComparer.Ordinal))
        {
            cache.Remember(row.Task, row.ContainerName, row.ContainerArchived);
            Offer(new WeekGoalSuggestion($"Finish {row.Task.Title}", null, [row.Task.Id], WeekGoalSuggestionReason.ImportantTask));
        }

        return suggestions;
    }

    private static async Task<WeekCheckIn> CheckInAsync(IDataSession session, TaskQueries.QueryContext context, WeekBoard board, DateOnly today, DateOnly start, CancellationToken token)
    {
        var assigned = board.Placements.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        var slipped = new List<WeekSlip>();
        foreach (var placement in board.Placements)
        {
            if (placement.Day is { } day && day < today && await session.Tasks.GetAsync(placement.TaskId, token) is { IsTerminal: false })
            {
                slipped.Add(new WeekSlip(placement.TaskId, day));
            }
        }

        var overdue = await session.Queries.QueryAsync(await TaskQueries.ResolveAsync(session, context, new TaskQuerySpec { View = TaskView.Overdue, Limit = TaskQueries.MaxLimit }, token), token);
        return new WeekCheckIn(
            today > start,
            slipped,
            [.. overdue.Rows.Where(row => assigned.Contains(row.Task.Id)).Select(row => row.Task.Id)],
            [.. board.Days.Where(day => day.Date >= today && (day.Load.OverTotal || day.Load.OverWork)).Select(day => day.Date)]);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
