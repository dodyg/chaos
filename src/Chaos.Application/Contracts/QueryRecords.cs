using Chaos.Domain.Tasks;

namespace Chaos.Application.Contracts;

/// <summary>
/// Built-in task views (PLAN §10). My Day lists one local date's day-plan items in their
/// manual order; Waiting lists Waiting-on-someone tasks grouped by outside person (tasks
/// without a named person last), earliest follow-up first within a person.
/// </summary>
public enum TaskView
{
    Container = 0,
    Tag = 1,
    Today = 2,
    Upcoming = 3,
    Overdue = 4,
    AssignedToMe = 5,
    RecentlyCompleted = 6,
    Waiting = 7,
    Search = 8,
    MyDay = 9,
}

/// <summary>An inclusive range of local dates; either end may be open.</summary>
public sealed record LocalDateRange(DateOnly? From, DateOnly? To);

/// <summary>
/// What the person asked for (PLAN §10): a built-in view plus temporary filters that compose
/// with it. The same specification produces both rows and counts, so sidebar counts match
/// the view. Nothing here is saved; there is no custom-view builder in Phase 1.
/// </summary>
public sealed record TaskQuerySpec
{
    public required TaskView View { get; init; }
    public string? ContainerId { get; init; }
    /// <summary>Tag filter; always includes descendant tags.</summary>
    public string? TagId { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<TaskItemStatus> Statuses { get; init; } = [];
    public IReadOnlyList<TaskPriority> Priorities { get; init; } = [];
    /// <summary>Work/Personal focus: only tasks of this capacity category; null shows both.</summary>
    public CapacityCategory? Category { get; init; }
    public LocalDateRange? Planned { get; init; }
    public LocalDateRange? Due { get; init; }
    public LocalDateRange? Completed { get; init; }
    /// <summary>Show Done/Cancelled tasks too. Ignored when an explicit status filter is given.</summary>
    public bool IncludeCompleted { get; init; }
    /// <summary>Show tasks of archived lists/projects (they are hidden by default).</summary>
    public bool IncludeArchived { get; init; }
    public int UpcomingDays { get; init; } = 14;
    public int RecentDays { get; init; } = 30;
    /// <summary>My Day: the local date whose plan to show; null means the profile's today.</summary>
    public DateOnly? Day { get; init; }
    public int Limit { get; init; } = 100;
    /// <summary>Opaque cursor from the previous page; null for the first page.</summary>
    public string? Cursor { get; init; }
}

/// <summary>A time-of-day cutoff in one zone: timed due values before it have passed.</summary>
public sealed record ZoneCutoff(string ZoneId, DateOnly LocalDate, TimeOnly LocalTime);

/// <summary>
/// The specification with every calendar-dependent value resolved by the application
/// (today, ranges as dates or UTC instants, per-zone "now"), so the store translates it into
/// parameterized SQL without knowing about clocks or zones.
/// </summary>
public sealed record TaskQueryCriteria
{
    public required string SpaceId { get; init; }
    public required string ProfileId { get; init; }
    public required TaskView View { get; init; }
    public string? ContainerId { get; init; }
    public string? TagId { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<TaskItemStatus> Statuses { get; init; } = [];
    public IReadOnlyList<TaskPriority> Priorities { get; init; } = [];
    public CapacityCategory? Category { get; init; }
    public bool OpenOnly { get; init; }
    public bool DoneOnly { get; init; }
    public bool IncludeArchived { get; init; }
    public DateOnly? PlannedFrom { get; init; }
    public DateOnly? PlannedTo { get; init; }
    public DateOnly? DueFrom { get; init; }
    public DateOnly? DueTo { get; init; }
    public DateTimeOffset? CompletedFromUtc { get; init; }
    public DateTimeOffset? CompletedBeforeUtc { get; init; }
    /// <summary>Today view: planned or due on this local date.</summary>
    public DateOnly? PlannedOrDueOn { get; init; }
    /// <summary>Upcoming view: planned or due after <see cref="UpcomingAfter"/> through <see cref="UpcomingThrough"/>.</summary>
    public DateOnly? UpcomingAfter { get; init; }
    public DateOnly? UpcomingThrough { get; init; }
    /// <summary>Overdue view: date-only due values before this local date have passed.</summary>
    public DateOnly? OverdueBefore { get; init; }
    /// <summary>Overdue view: per-zone "now" for timed due values.</summary>
    public IReadOnlyList<ZoneCutoff> OverdueZoneCutoffs { get; init; } = [];
    /// <summary>My Day view: active (not removed) items of the profile's plan for this local date.</summary>
    public DateOnly? DayPlanOn { get; init; }
    public required int Limit { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>Where a search hit came from; a subtask or diary match links to its parent task.</summary>
public enum SearchMatchKind
{
    Task = 1,
    Subtask = 2,
    Diary = 3,
    /// <summary>The name of the outside person the task waits on.</summary>
    WaitingPerson = 5,
}

/// <summary>One result row: a top-level task with the context a list needs to show it.</summary>
public sealed record TaskQueryRow(
    TaskRecord Task,
    string ContainerName,
    bool ContainerArchived,
    IReadOnlyList<string> TagIds,
    IReadOnlyList<SearchMatchKind> Matches,
    DateOnly? GroupDate,
    string? WaitingPerson = null,
    DateTimeOffset? NextFollowUpAtUtc = null);

/// <summary>A page of rows, the total row count for the same criteria, and the next cursor (null at the end).</summary>
public sealed record TaskQueryPage(IReadOnlyList<TaskQueryRow> Rows, long TotalCount, string? NextCursor);

/// <summary>Sidebar counts computed with the same specification as each view (PLAN §10).</summary>
public sealed record ViewCounts(IReadOnlyDictionary<TaskView, long> Views, IReadOnlyDictionary<string, long> Tags);
