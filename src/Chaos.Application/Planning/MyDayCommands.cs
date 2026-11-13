using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Planning;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Planning;

/// <summary>Why a task is suggested for today, most urgent first (PLAN §10).</summary>
public enum DaySuggestionReason
{
    Overdue = 0,
    DueToday = 1,
    PlannedToday = 2,
    DueSoon = 3,
    RecurrenceToday = 4,
    UnfinishedYesterday = 5,
}

public sealed record DaySuggestion(TaskRecord Task, string ContainerName, DaySuggestionReason Reason);

/// <summary>The profile's local today and when the next local date begins (for rollover refresh).</summary>
public sealed record DayClock(DateOnly Today, DateTimeOffset NextDayStartsAtUtc, string ZoneId);

/// <summary>
/// Totals for one day plan. Remaining minutes count open items only; items without an
/// estimate are counted separately and never treated as zero effort.
/// </summary>
public sealed record DaySummary(
    DateOnly Date,
    DayClock Clock,
    bool Exists,
    int OpenCount,
    int FinishedCount,
    int RemainingMinutes,
    int UnknownEstimates,
    int AvailableMinutes = 0,
    int? WorkAllowanceMinutes = null,
    int WorkMinutes = 0);

public sealed record DayItemCommand(string OperationId, DateOnly Date, string TaskId);
public sealed record DayMoveCommand(string OperationId, DateOnly Date, string TaskId, int Index);
/// <summary>Carries open items of <see cref="FromDate"/> to the next date; null task IDs means every open item.</summary>
public sealed record DayCarryOverCommand(string OperationId, DateOnly FromDate, IReadOnlyList<string>? TaskIds);
/// <param name="Changed">How many items this operation added, removed, moved or carried.</param>
public sealed record DayChangeResult(DaySummary Summary, int Changed, bool Replayed);

/// <summary>
/// My Day (PLAN §10, work package 8). Each local date has at most one plan per profile,
/// created on first use and never deleted, so earlier days stay reviewable. Membership is
/// profile-owned: adding, removing, ordering or carrying a task never edits its planned or
/// due fields. Suggestions are computed on request and never inserted automatically. Earlier
/// dates are read-only. Rows of a day come from the shared My Day query view, so the sidebar
/// count and the list always agree.
/// </summary>
public sealed class MyDayCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    private const int SuggestionSourceLimit = TaskQueries.MaxLimit;

    /// <summary>The current local date in the profile zone, read fresh each call (midnight, resume and zone changes).</summary>
    public async Task<DayClock> ClockAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) => ClockOf(await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token)), ct);

    /// <summary>
    /// Opens a day: today and later dates are initialized once (the unique key settles races);
    /// an earlier date is only read and may have no plan.
    /// </summary>
    public async Task<DaySummary> OpenAsync(DateOnly? date, CancellationToken ct = default)
    {
        var existing = await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            var day = date ?? context.Today;
            return (Day: day, Writable: day >= context.Today, Exists: await session.DayPlans.FindAsync(context.Profile.Id, day, token) is not null);
        }, ct);

        if (existing.Writable && !existing.Exists)
        {
            await store.ExecuteAsync(async (session, token) =>
            {
                var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
                return await DayPlanning.EnsurePlanAsync(session, ids, context.Profile.Id, existing.Day, clock.GetUtcNow(), token);
            }, ct);
        }

        return await store.QueryAsync(async (session, token) =>
            await SummaryAsync(session, await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token), existing.Day, token), ct);
    }

    /// <summary>
    /// Explicit suggestions for today: overdue, due today, planned today, due within the next
    /// two days, a repetition due today and yesterday's unfinished selected work. Each task
    /// appears once with its most urgent reason; selected, finished, deleted and archived
    /// tasks are left out. Ordered by reason, due date, priority (highest first), then ID.
    /// </summary>
    public async Task<IReadOnlyList<DaySuggestion>> SuggestAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            var today = context.Today;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            if (await session.DayPlans.FindAsync(context.Profile.Id, today, token) is { } plan)
            {
                foreach (var item in await session.DayPlans.ListItemsAsync(plan.Id, includeRemoved: false, token))
                {
                    selected.Add(item.TaskId);
                }
            }

            var found = new Dictionary<string, DaySuggestion>(StringComparer.Ordinal);
            void Offer(TaskRecord task, string containerName, DaySuggestionReason reason)
            {
                if (selected.Contains(task.Id) || task.IsTerminal || task.DeletedAtUtc is not null)
                {
                    return;
                }

                if (!found.TryGetValue(task.Id, out var current) || reason < current.Reason)
                {
                    found[task.Id] = new DaySuggestion(task, containerName, reason);
                }
            }

            async Task<IReadOnlyList<TaskQueryRow>> RowsAsync(TaskQuerySpec spec) =>
                (await session.Queries.QueryAsync(await TaskQueries.ResolveAsync(session, context, spec with { Limit = SuggestionSourceLimit }, token), token)).Rows;

            foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.Overdue }))
            {
                Offer(row.Task, row.ContainerName, DaySuggestionReason.Overdue);
            }

            foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.Today }))
            {
                Offer(row.Task, row.ContainerName, row.Task.DueDate == today ? DaySuggestionReason.DueToday : DaySuggestionReason.PlannedToday);
            }

            foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Due = new LocalDateRange(today.AddDays(1), today.AddDays(2)) }))
            {
                Offer(row.Task, row.ContainerName, DaySuggestionReason.DueSoon);
            }

            foreach (var taskId in await session.DayPlans.ListRecurrenceTaskIdsDueOnAsync(context.Space.Id, today, token))
            {
                if (await session.Tasks.GetAsync(taskId, token) is { } task && await session.Containers.GetAsync(task.ContainerId, token) is { } container)
                {
                    Offer(task, container.Name, DaySuggestionReason.RecurrenceToday);
                }
            }

            foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.MyDay, Day = today.AddDays(-1) }))
            {
                if (!row.ContainerArchived)
                {
                    Offer(row.Task, row.ContainerName, DaySuggestionReason.UnfinishedYesterday);
                }
            }

            return (IReadOnlyList<DaySuggestion>)[.. found.Values
                .OrderBy(suggestion => suggestion.Reason)
                .ThenBy(suggestion => suggestion.Task.DueDate ?? DateOnly.MaxValue)
                .ThenBy(suggestion => suggestion.Task.DueTime ?? TimeOnly.MaxValue)
                .ThenByDescending(suggestion => suggestion.Task.Priority)
                .ThenBy(suggestion => suggestion.Task.Id, StringComparer.Ordinal)];
        }, ct);

    /// <summary>Adds an open task to the end of a day; re-adding a removed task brings its row back.</summary>
    public Task<DayChangeResult> AddAsync(DayItemCommand command, CancellationToken ct = default) =>
        MutateAsync(command.OperationId, "myday.add", new { date = command.Date, taskId = command.TaskId }, command.Date, async (session, context, plan, now, token) =>
        {
            var task = await session.Tasks.GetAsync(command.TaskId, token)
                ?? throw AppException.NotFound("This task no longer exists. Refresh and choose another task.");
            if (task.SpaceId != context.Space.Id)
            {
                throw AppException.NotFound("This task no longer exists. Refresh and choose another task.");
            }

            if (task.IsTerminal)
            {
                throw AppException.Validation("taskId", "This task is already finished. Reopen it before adding it to My Day.");
            }

            return await DayPlanning.PlaceAsync(session, ids, plan, task.Id, DayPlanItemOrigin.Manual, now, token) ? 1 : 0;
        }, ct);

    /// <summary>Takes a task off a day. The row stays, so reopening the day never puts it back by itself.</summary>
    public Task<DayChangeResult> RemoveAsync(DayItemCommand command, CancellationToken ct = default) =>
        MutateAsync(command.OperationId, "myday.remove", new { date = command.Date, taskId = command.TaskId }, command.Date, async (session, _, plan, now, token) =>
            await DayPlanning.RemoveAsync(session, plan, command.TaskId, now, token) ? 1 : 0, ct);

    /// <summary>Moves a task to <see cref="DayMoveCommand.Index"/> among the day's open items (keyboard and drag use the same command).</summary>
    public Task<DayChangeResult> MoveAsync(DayMoveCommand command, CancellationToken ct = default) =>
        MutateAsync(command.OperationId, "myday.move", new { date = command.Date, taskId = command.TaskId, index = command.Index }, command.Date, async (session, _, plan, now, token) =>
        {
            var open = await OpenItemsAsync(session, plan.Id, token);
            var moving = open.FirstOrDefault(item => item.TaskId == command.TaskId)
                ?? throw AppException.NotFound("This task is not on this day any more. Refresh My Day.");
            var byId = open.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var assignments = RankOrdering.Place([.. open.Select(item => (item.Id, item.Position, item.Version))], moving.Id, command.Index);
            foreach (var assignment in assignments)
            {
                var item = byId[assignment.Id];
                await DayPlanning.UpdateItemAsync(session, item with { Position = assignment.NewPosition, UpdatedAtUtc = now, Version = item.Version + 1 }, assignment.ExpectedVersion, token);
            }

            return assignments.Count == 0 ? 0 : 1;
        }, ct);

    /// <summary>
    /// Copies open items of one day onto the next date, once: an item already there is left as
    /// it is, a removed one is brought back. The source day keeps its historical membership.
    /// </summary>
    public Task<DayChangeResult> CarryOverAsync(DayCarryOverCommand command, CancellationToken ct = default) =>
        MutateAsync(
            command.OperationId,
            "myday.carryOver",
            new { fromDate = command.FromDate, taskIds = command.TaskIds?.Order(StringComparer.Ordinal).ToArray() },
            command.FromDate.AddDays(1),
            async (session, context, target, now, token) =>
            {
                var source = await session.DayPlans.FindAsync(context.Profile.Id, command.FromDate, token);
                if (source is null)
                {
                    return 0;
                }

                var wanted = command.TaskIds is null ? null : new HashSet<string>(command.TaskIds, StringComparer.Ordinal);
                var carried = 0;
                foreach (var item in await OpenItemsAsync(session, source.Id, token))
                {
                    if ((wanted is null || wanted.Contains(item.TaskId)) && await DayPlanning.PlaceAsync(session, ids, target, item.TaskId, DayPlanItemOrigin.CarryOver, now, token))
                    {
                        carried++;
                    }
                }

                return carried;
            },
            ct);

    private async Task<DayChangeResult> MutateAsync(
        string operationId,
        string commandKind,
        object payload,
        DateOnly date,
        Func<IDataSession, TaskQueries.QueryContext, DayPlanRecord, DateTimeOffset, CancellationToken, Task<int>> body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw AppException.Validation("operationId", "An operation ID is required.");
        }

        var (changed, replayed) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var context = await TaskQueries.ContextAsync(session, now, token);
            return await TaskMutationSupport.ReplayOrRunAsync(session, context.Profile.Id, operationId, commandKind, payload, now, async innerToken =>
            {
                if (date < context.Today)
                {
                    throw AppException.Validation("date", "Earlier days are kept for review and cannot be changed. Use today's suggestions to pick up unfinished work.");
                }

                var plan = await DayPlanning.EnsurePlanAsync(session, ids, context.Profile.Id, date, now, innerToken);
                return await body(session, context, plan, now, innerToken);
            }, token);
        }, ct);

        var summary = await store.QueryAsync(async (session, token) =>
            await SummaryAsync(session, await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token), date, token), ct);
        return new DayChangeResult(summary, changed, replayed);
    }

    /// <summary>Active items whose task is still open and not deleted, in day order.</summary>
    private static async Task<IReadOnlyList<DayPlanItemRecord>> OpenItemsAsync(IDataSession session, string planId, CancellationToken token)
    {
        var open = new List<DayPlanItemRecord>();
        foreach (var item in await session.DayPlans.ListItemsAsync(planId, includeRemoved: false, token))
        {
            if (await session.Tasks.GetAsync(item.TaskId, token) is { IsTerminal: false })
            {
                open.Add(item);
            }
        }

        return open;
    }

    private static async Task<DaySummary> SummaryAsync(IDataSession session, TaskQueries.QueryContext context, DateOnly date, CancellationToken token)
    {
        var availability = await AvailabilityAsync(session, context.Profile.Id, date, token);
        var plan = await session.DayPlans.FindAsync(context.Profile.Id, date, token);
        if (plan is null)
        {
            return new DaySummary(date, ClockOf(context), false, 0, 0, 0, 0, availability.TotalMinutes, availability.WorkMinutes);
        }

        int open = 0, finished = 0, minutes = 0, unknown = 0, work = 0;
        foreach (var item in await session.DayPlans.ListItemsAsync(plan.Id, includeRemoved: false, token))
        {
            var task = await session.Tasks.GetAsync(item.TaskId, token);
            if (task is null || await session.Containers.GetAsync(task.ContainerId, token) is null)
            {
                continue;
            }

            if (task.IsTerminal)
            {
                finished++;
                continue;
            }

            open++;
            if (task.EstimateMinutes is { } estimate)
            {
                minutes += estimate;
                if (task.CapacityCategory == CapacityCategory.Work)
                {
                    work += estimate;
                }
            }
            else
            {
                unknown++;
            }
        }

        return new DaySummary(date, ClockOf(context), true, open, finished, minutes, unknown, availability.TotalMinutes, availability.WorkMinutes, work);
    }

    /// <summary>The day's available and work time from My Week: a confirmed override, else the weekday rule, else the default.</summary>
    private static async Task<DayAvailability> AvailabilityAsync(IDataSession session, string profileId, DateOnly date, CancellationToken token)
    {
        if ((await session.Weeks.ListOverridesAsync(profileId, date, date, token)).FirstOrDefault() is { } chosen)
        {
            return new DayAvailability(chosen.TotalMinutes, chosen.WorkMinutes);
        }

        return (await session.Weeks.ListRulesAsync(profileId, token)).FirstOrDefault(rule => rule.Weekday == date.DayOfWeek) is { } rule
            ? new DayAvailability(rule.TotalMinutes, rule.WorkMinutes)
            : DayAvailability.DefaultFor(date.DayOfWeek);
    }

    private static DayClock ClockOf(TaskQueries.QueryContext context) =>
        new(context.Today, TaskQueries.StartOfLocalDayUtc(context.Zone, context.Today.AddDays(1)), context.Zone.Id);
}
