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

public sealed partial class MyWeekCommands
{
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

        var profile = await session.Profiles.GetPrimaryAsync(token);
        var zone = profile is null ? TimeZoneInfo.Utc : Chaos.Application.Reminders.ReminderSync.ProfileZone(profile);
        var weekDates = WeekCalendar.Dates(start).ToList();
        var meetings = await MeetingPlanning.LoadAsync(session, zone, weekDates[0], weekDates[^1], token);
        var days = weekDates.Select(date =>
        {
            var availability = AvailabilityOn(date, rules, overrides, out var isOverride);
            var busy = meetings.BusyMinutes(date);
            return new WeekDayLoad(date, availability, isOverride, Capacity.Measure(availability.AfterMeetings(busy), On(date)), busy);
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
            return await FromSummaryAsync(session, context, new WeekLookBack(start, end, false, [.. completed.Rows.Select(row => row.Task.Id)], completedCount, 0, 0, 0, [], []), token);
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
        return await FromSummaryAsync(session, context, new WeekLookBack(previous.WeekStartDate, WeekCalendar.EndOf(previous.WeekStartDate), true,
            [.. completed.Rows.Select(row => row.Task.Id)], completedCount, assigned, done, cancelled, unfinished, board.Goals), token);
    }

    /// <summary>Replaces the live completion facts with the stored weekly summary of that week, when a complete one exists.</summary>
    private static async Task<WeekLookBack> FromSummaryAsync(IDataSession session, TaskQueries.QueryContext context, WeekLookBack live, CancellationToken token)
    {
        if (await session.Summaries.FindLatestAsync(context.Profile.Id, Chaos.Domain.Summaries.SummaryKind.Weekly, live.WeekStart, token) is not { IsPartial: false } summary
            || StoredJsonFormats.ReadSnapshot<SummaryFacts>(summary.FactsJson) is not { } facts)
        {
            return live;
        }

        var completed = facts.Completions.Where(item => item.StillDone)
            .GroupBy(item => item.Task.TaskId, StringComparer.Ordinal).Select(group => group.Last().Task).ToList();
        return live with
        {
            CompletedTaskIds = [.. completed.Select(task => task.TaskId)],
            CompletedCount = completed.Count,
            AssignedCount = facts.Week?.AssignedCount ?? live.AssignedCount,
            AssignedDone = facts.Week?.DoneCount ?? live.AssignedDone,
            AssignedCancelled = facts.Week?.CancelledCount ?? live.AssignedCancelled,
            SummaryId = summary.Id,
            SummaryCompleted = completed,
        };
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
        var assignedDue = await session.Queries.QueryAsync(await TaskQueries.ResolveAsync(session, context,
            new TaskQuerySpec { View = TaskView.AssignedToMe, Due = new LocalDateRange(null, context.Today.AddDays(2)), Limit = TaskQueries.MaxLimit }, token), token);
        foreach (var row in assignedDue.Rows)
        {
            var task = row.Task;
            var overdue = task.DueDate < context.Today;
            if (task.DueTime is { } dueTime)
            {
                var local = TimeZoneInfo.ConvertTime(context.Now, TaskQueries.FindZone(task.DueZoneId ?? string.Empty) ?? context.Zone);
                var date = DateOnly.FromDateTime(local.DateTime);
                overdue = task.DueDate < date || (task.DueDate == date && dueTime < TimeOnly.FromDateTime(local.DateTime));
            }
            if (overdue && !task.IsTerminal && !row.ContainerArchived && seen.Add(task.Id))
            {
                cache.Remember(task, row.ContainerName, row.ContainerArchived);
                found.Add(new WeekCandidate(task.Id, WeekCandidateReason.Overdue));
            }
        }
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
