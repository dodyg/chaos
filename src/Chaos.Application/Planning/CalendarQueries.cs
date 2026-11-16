using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Views;
using Chaos.Domain.Planning;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Planning;

public enum CalendarEntryKind
{
    /// <summary>A task on its planned date; timed when it has a planned time.</summary>
    Planned = 0,
    /// <summary>A task on its due date: a deadline marker, never a block.</summary>
    Due = 1,
    /// <summary>A read-only projection of a future occurrence of a repeating task.</summary>
    Repeat = 2,
}

/// <summary>One thing on the calendar. For a repeat, <see cref="TaskId"/> is the series' active task.</summary>
public sealed record CalendarEntry(
    CalendarEntryKind Kind,
    string TaskId,
    string Title,
    TaskItemStatus Status,
    TaskPriority Priority,
    string ContainerName,
    DateOnly Date,
    TimeOnly? Time,
    /// <summary>The end of a time block on the same date (start plus estimate, or 30 minutes); null otherwise.</summary>
    TimeOnly? EndTime,
    bool IsBlock,
    bool IsEstimated,
    int? EstimateMinutes,
    CapacityCategory Category);

/// <summary>
/// A meeting from another calendar, read-only. A meeting across midnight or over several days appears once
/// per local day it touches, clipped to that day; an all-day meeting has no times.
/// </summary>
public sealed record CalendarMeeting(
    string Id,
    string SourceId,
    string EventId,
    string SourceName,
    string Title,
    DateOnly Date,
    TimeOnly? Start,
    TimeOnly? End,
    bool AllDay,
    string? Color,
    bool Busy,
    string? Location,
    string? VideoUrl,
    string? WebUrl);

/// <summary>One day's load against My Week's availability, from the shared capacity formula.</summary>
public sealed record CalendarDayTotal(DateOnly Date, DayAvailability Availability, int BusyMinutes, DayLoad Load);

public sealed record CalendarFilters
{
    /// <summary>Work/Personal focus; null shows both.</summary>
    public CapacityCategory? Category { get; init; }
    public string? TagId { get; init; }
    public string? ContainerId { get; init; }
    /// <summary>A custom view whose conditions the calendar is filtered by.</summary>
    public string? ViewId { get; init; }
    public bool ShowPlanned { get; init; } = true;
    public bool ShowDue { get; init; } = true;
    public bool ShowMeetings { get; init; } = true;
    public bool ShowCompleted { get; init; }
}

public sealed record CalendarRangeResult(
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    string ZoneId,
    IReadOnlyList<CalendarEntry> Entries,
    IReadOnlyList<CalendarMeeting> Meetings,
    IReadOnlyList<CalendarDayTotal> Days,
    bool Truncated);

/// <summary>
/// The calendar read (PLAN2 §10.1). Planned and due entries come from the ordinary task query so
/// filters, the focus and a custom view's conditions behave exactly as in lists; repeats come from
/// the recurrence projection; day totals use <see cref="Capacity"/> and My Week's availability.
/// </summary>
public sealed class CalendarQueries(IApplicationStore store, TaskQueries queries, TimeProvider clock)
{
    public const int MaxDays = 62;
    public const int MaxEntries = 2000;
    public const int MaxUnscheduledPage = 50;
    private const int DefaultBlockMinutes = 30;
    private const int MembershipCap = 5000;

    public async Task<CalendarRangeResult> RangeAsync(DateOnly from, DateOnly to, CalendarFilters filters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (to < from)
        {
            throw AppException.Validation("to", "The end date must be on or after the start date.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
        {
            throw AppException.Validation("to", $"The calendar shows at most {MaxDays} days at a time.");
        }

        var custom = await DefinitionAsync(filters.ViewId, ct);
        var focusOnly = BaseSpec(new CalendarFilters { Category = filters.Category }, null, TaskView.Search);
        var filtered = BaseSpec(filters, custom, custom is null ? TaskView.Search : TaskView.Custom);
        var narrowed = filters.TagId is not null || filters.ContainerId is not null || custom is not null;

        // Day totals: open tasks planned on each day under the focus only; a filter must not hide load.
        var loadRows = await FetchAsync(focusOnly with { Planned = new LocalDateRange(from, to), IncludeCompleted = false }, ct);
        var entries = new List<CalendarEntry>();
        var truncated = false;

        if (filters.ShowPlanned)
        {
            var rows = !narrowed && !filters.ShowCompleted
                ? loadRows
                : await FetchAsync(filtered with { Planned = new LocalDateRange(from, to), IncludeCompleted = filters.ShowCompleted }, ct);
            foreach (var row in rows.Where(row => Visible(row.Task, filters.ShowCompleted) && row.Task.PlannedDate is not null))
            {
                entries.Add(PlannedEntry(row));
            }
        }

        if (filters.ShowDue)
        {
            var rows = await FetchAsync(filtered with { Due = new LocalDateRange(from, to), IncludeCompleted = filters.ShowCompleted }, ct);
            foreach (var row in rows.Where(row => Visible(row.Task, filters.ShowCompleted) && row.Task.DueDate is not null))
            {
                entries.Add(DueEntry(row));
            }
        }

        HashSet<string>? members = null;
        if (filters.TagId is not null || custom is not null)
        {
            members = [.. (await FetchAsync(filtered with { IncludeCompleted = false }, ct, MembershipCap)).Select(row => row.Task.Id)];
        }

        var detail = await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            var projections = await ProjectionsAsync(session, context, from, to, filters, members, token);
            var overrides = await session.Weeks.ListOverridesAsync(context.Profile.Id, from, to, token);
            var rules = await session.Weeks.ListRulesAsync(context.Profile.Id, token);
            var meetings = await MeetingPlanning.LoadAsync(session, context.Zone, from, to, token);
            return (context.Today, ZoneId: context.Zone.Id, projections, overrides, rules, meetings);
        }, ct);

        entries.AddRange(detail.projections);
        var ordered = entries
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.Time ?? TimeOnly.MinValue)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.TaskId, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count > MaxEntries)
        {
            ordered = ordered.Take(MaxEntries).ToList();
            truncated = true;
        }

        var days = new List<CalendarDayTotal>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var availability = AvailabilityOn(date, detail.overrides, detail.rules);
            var busy = detail.meetings.BusyMinutes(date);
            var load = Capacity.Measure(availability.AfterMeetings(busy), loadRows
                .Where(row => row.Task.PlannedDate == date)
                .Select(row => new CapacityItem(row.Task.EstimateMinutes, row.Task.CapacityCategory, true)));
            days.Add(new CalendarDayTotal(date, availability, busy, load));
        }

        var shown = filters.ShowMeetings ? detail.meetings.Meetings : [];
        return new CalendarRangeResult(from, to, detail.Today, detail.ZoneId, ordered, shown, days, truncated);
    }

    /// <summary>Open tasks with no planned date, for the side panel (paged, filterable).</summary>
    public async Task<TaskQueryPage> UnscheduledAsync(CalendarFilters filters, string? text, string? cursor, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var custom = await DefinitionAsync(filters.ViewId, ct);
        var spec = BaseSpec(filters, custom, custom is null ? TaskView.Search : TaskView.Custom) with
        {
            Unscheduled = true,
            Text = text,
            Cursor = cursor,
            Limit = Math.Clamp(limit, 1, MaxUnscheduledPage),
            // A custom view does not hide finished tasks by itself.
            Statuses = custom is null ? [] : [TaskItemStatus.NotStarted, TaskItemStatus.InProgress, TaskItemStatus.WaitingOnSomeone, TaskItemStatus.NeedsReview],
        };
        return await queries.RunAsync(spec, ct);
    }

    private async Task<Chaos.Domain.Views.ViewDefinition?> DefinitionAsync(string? viewId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(viewId))
        {
            return null;
        }

        var view = await store.QueryAsync(async (session, token) =>
            await session.SavedViews.GetAsync(viewId, token) ?? throw AppException.NotFound("That view no longer exists. Refresh and try again."), ct);
        return view.DeletedAtUtc is null ? SavedViewCommands.ParseDefinition(view.DefinitionJson) : throw AppException.NotFound("That view was deleted.");
    }

    private static TaskQuerySpec BaseSpec(CalendarFilters filters, Chaos.Domain.Views.ViewDefinition? definition, TaskView view) => new()
    {
        View = view,
        Category = filters.Category,
        TagId = filters.TagId,
        ContainerId = filters.ContainerId,
        Definition = definition,
        Limit = TaskQueries.MaxLimit,
    };

    private async Task<List<TaskQueryRow>> FetchAsync(TaskQuerySpec spec, CancellationToken ct, int cap = MaxEntries + 1)
    {
        var rows = new List<TaskQueryRow>();
        var cursor = spec.Cursor;
        do
        {
            var page = await queries.RunAsync(spec with { Cursor = cursor }, ct);
            rows.AddRange(page.Rows);
            cursor = page.NextCursor;
        }
        while (cursor is not null && rows.Count < cap);
        return rows;
    }

    private static bool Visible(TaskRecord task, bool showCompleted) =>
        task.Status != TaskItemStatus.Cancelled && (showCompleted || task.Status != TaskItemStatus.Done);

    private static CalendarEntry PlannedEntry(TaskQueryRow row)
    {
        var task = row.Task;
        TimeOnly? end = task.PlannedTime is { } begin ? EndOf(begin, task.EstimateMinutes) : null;
        var block = task.PlannedTime is not null && task.EstimateMinutes is not null;

        return new CalendarEntry(CalendarEntryKind.Planned, task.Id, task.Title, task.Status, task.Priority, row.ContainerName,
            task.PlannedDate!.Value, task.PlannedTime, end, block, task.EstimateMinutes is not null, task.EstimateMinutes, task.CapacityCategory);
    }

    private static CalendarEntry DueEntry(TaskQueryRow row)
    {
        var task = row.Task;
        return new CalendarEntry(CalendarEntryKind.Due, task.Id, task.Title, task.Status, task.Priority, row.ContainerName,
            task.DueDate!.Value, task.DueTime, null, false, task.EstimateMinutes is not null, task.EstimateMinutes, task.CapacityCategory);
    }

    private static async Task<IReadOnlyList<CalendarEntry>> ProjectionsAsync(
        IDataSession session, TaskQueries.QueryContext context, DateOnly from, DateOnly to, CalendarFilters filters, HashSet<string>? members, CancellationToken token)
    {
        // Missed slots of a repeating task are suppressed, so nothing is projected for today or earlier
        // beyond the series' own active task.
        var start = from > context.Today.AddDays(1) ? from : context.Today.AddDays(1);
        var result = new List<CalendarEntry>();
        if (start > to)
        {
            return result;
        }

        foreach (var active in await session.Recurrence.ListActiveBySpaceAsync(context.Space.Id, token))
        {
            var series = await session.Recurrence.GetSeriesAsync(active.SeriesId, token);
            var rule = series is null ? null : StoredJsonFormats.ReadSnapshot<RecurrenceRule>(series.RuleJson);
            var task = active.TaskId is null ? null : await session.Tasks.GetAsync(active.TaskId, token);
            if (series is null || rule is null || task is null || series.EndedAtUtc is not null || task.DeletedAtUtc is not null || task.IsTerminal)
            {
                continue;
            }

            if (filters.Category is { } category && task.CapacityCategory != category
                || filters.ContainerId is not null && task.ContainerId != filters.ContainerId
                || members is not null && !members.Contains(task.Id))
            {
                continue;
            }

            var container = await session.Containers.GetAsync(task.ContainerId, token);
            if (container is null || container.DeletedAtUtc is not null || container.ArchivedAtUtc is not null)
            {
                continue;
            }

            var remaining = (int)Math.Clamp(rule.EndKind == RecurrenceEndKind.Count ? rule.Count!.Value - series.ActionableCount : MaxEntries, 0, MaxEntries);
            var previous = DateTime.ParseExact(active.ScheduleKey, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            foreach (var slot in RecurrenceCalculator.ProjectRange(rule, previous, start, to, remaining))
            {
                var time = rule.AnchorTime;
                TimeOnly? end = time is { } begin ? EndOf(begin, task.EstimateMinutes) : null;
                result.Add(new CalendarEntry(CalendarEntryKind.Repeat, task.Id, task.Title, task.Status, task.Priority, container.Name,
                    DateOnly.FromDateTime(slot), time, end, false, task.EstimateMinutes is not null, task.EstimateMinutes, task.CapacityCategory));
            }
        }

        return result;
    }

    private static TimeOnly EndOf(TimeOnly begin, int? estimate)
    {
        var finish = begin.ToTimeSpan() + TimeSpan.FromMinutes(estimate is > 0 ? estimate.Value : DefaultBlockMinutes);
        return finish >= TimeSpan.FromDays(1) ? new TimeOnly(23, 59) : TimeOnly.FromTimeSpan(finish);
    }

    /// <summary>The same precedence My Day uses: a confirmed override, else the weekday rule, else the default.</summary>
    private static DayAvailability AvailabilityOn(DateOnly date, IReadOnlyList<AvailabilityRecord> overrides, IReadOnlyList<AvailabilityRecord> rules) =>
        overrides.FirstOrDefault(item => item.Date == date) is { } chosen ? new DayAvailability(chosen.TotalMinutes, chosen.WorkMinutes)
        : rules.FirstOrDefault(item => item.Weekday == date.DayOfWeek) is { } rule ? new DayAvailability(rule.TotalMinutes, rule.WorkMinutes)
        : DayAvailability.DefaultFor(date.DayOfWeek);
}
