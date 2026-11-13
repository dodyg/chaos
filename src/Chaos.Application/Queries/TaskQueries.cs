using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Queries;

/// <summary>
/// Built-in views, temporary filters and search (PLAN §10, §15 WP3). One specification is
/// resolved against the profile's calendar into store criteria and then produces both the
/// rows and the count, so sidebar counts always match the view they open. Deleted tasks and
/// tasks of archived or deleted containers are excluded unless a filter asks for archives.
/// </summary>
public sealed class TaskQueries(IApplicationStore store, TimeProvider clock)
{
    public const int MaxLimit = 100;

    /// <summary>Views that have a sidebar count.</summary>
    public static readonly IReadOnlyList<TaskView> CountedViews =
        [TaskView.MyDay, TaskView.Today, TaskView.Upcoming, TaskView.Overdue, TaskView.AssignedToMe, TaskView.RecentlyCompleted, TaskView.Waiting];

    public async Task<TaskQueryPage> RunAsync(TaskQuerySpec spec, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await store.QueryAsync(async (session, token) =>
        {
            var context = await ContextAsync(session, token);
            var criteria = await ResolveAsync(session, context, spec, token);
            return await session.Queries.QueryAsync(criteria, token);
        }, ct);
    }

    public async Task<long> CountAsync(TaskQuerySpec spec, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var context = await ContextAsync(session, token);
            return await session.Queries.CountAsync(await ResolveAsync(session, context, spec, token), token);
        }, ct);

    /// <summary>
    /// Counts for the built-in views and for every active tag (descendants included), from one
    /// read snapshot. A category limits every count to that Work/Personal focus.
    /// </summary>
    public async Task<ViewCounts> CountsAsync(CancellationToken ct = default) => await CountsAsync(null, ct);

    public async Task<ViewCounts> CountsAsync(CapacityCategory? category, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var context = await ContextAsync(session, token);
            var views = new Dictionary<TaskView, long>();
            foreach (var view in CountedViews)
            {
                views[view] = await session.Queries.CountAsync(await ResolveAsync(session, context, new TaskQuerySpec { View = view, Category = category }, token), token);
            }

            var tags = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var tag in await session.Tags.ListByOwnerAsync(context.Profile.Id, token))
            {
                tags[tag.Id] = await session.Queries.CountAsync(await ResolveAsync(session, context, new TaskQuerySpec { View = TaskView.Tag, TagId = tag.Id, Category = category }, token), token);
            }

            var containers = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var container in await session.Containers.ListBySpaceAsync(context.Space.Id, token))
            {
                containers[container.Id] = await session.Queries.CountAsync(await ResolveAsync(session, context, new TaskQuerySpec { View = TaskView.Container, ContainerId = container.Id, Category = category }, token), token);
            }

            return new ViewCounts(views, tags) { Containers = containers };
        }, ct);

    /// <summary>Rebuilds the full-text index from the source tables (recovery command; PLAN §5).</summary>
    public async Task<long> RebuildSearchIndexAsync(CancellationToken ct = default) =>
        await store.ExecuteAsync((session, token) => session.Queries.RebuildSearchIndexAsync(clock.GetUtcNow(), token), ct);

    internal sealed record QueryContext(ProfileRecord Profile, SpaceRecord Space, TimeZoneInfo Zone, DateOnly Today, DateTimeOffset Now);

    private Task<QueryContext> ContextAsync(IDataSession session, CancellationToken token) => ContextAsync(session, clock.GetUtcNow(), token);

    /// <summary>The profile, Personal space, zone and local today at <paramref name="now"/> (shared with My Day).</summary>
    internal static async Task<QueryContext> ContextAsync(IDataSession session, DateTimeOffset now, CancellationToken token)
    {
        var profile = await session.Profiles.GetPrimaryAsync(token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");
        var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet. Restart Chaos to complete first-run setup.");
        var zone = FindZone(profile.ZoneId) ?? TimeZoneInfo.Utc;
        return new QueryContext(profile, space, zone, LocalDate(now, zone), now);
    }

    internal static async Task<TaskQueryCriteria> ResolveAsync(IDataSession session, QueryContext context, TaskQuerySpec spec, CancellationToken token)
    {
        if (!Enum.IsDefined(spec.View))
        {
            throw AppException.Validation("view", "Choose one of the built-in views.");
        }

        if (spec.View == TaskView.Container && string.IsNullOrWhiteSpace(spec.ContainerId))
        {
            throw AppException.Validation("containerId", "Choose a list or project.");
        }

        if (spec.View == TaskView.Tag && string.IsNullOrWhiteSpace(spec.TagId))
        {
            throw AppException.Validation("tagId", "Choose a tag.");
        }

        if (spec.Statuses.Any(status => !Enum.IsDefined(status)))
        {
            throw AppException.Validation("statuses", "Choose valid statuses.");
        }

        if (spec.Priorities.Any(priority => !Enum.IsDefined(priority)))
        {
            throw AppException.Validation("priorities", "Choose valid priorities.");
        }

        if (spec.UpcomingDays is < 1 or > 366)
        {
            throw AppException.Validation("upcomingDays", "Upcoming can look ahead from 1 to 366 days.");
        }

        if (spec.RecentDays is < 1 or > 3660)
        {
            throw AppException.Validation("recentDays", "Recently completed can look back from 1 to 3660 days.");
        }

        if (spec.Category is { } category && !Enum.IsDefined(category))
        {
            throw AppException.Validation("category", "Choose Work or Personal.");
        }

        RequireOrdered(spec.Planned, "planned");
        RequireOrdered(spec.Due, "due");
        RequireOrdered(spec.Completed, "completed");

        var today = context.Today;
        DateTimeOffset? completedFrom = spec.Completed?.From is { } from ? StartOfLocalDayUtc(context.Zone, from) : null;
        DateTimeOffset? completedBefore = spec.Completed?.To is { } to ? StartOfLocalDayUtc(context.Zone, to.AddDays(1)) : null;
        if (spec.View == TaskView.RecentlyCompleted)
        {
            var recent = StartOfLocalDayUtc(context.Zone, today.AddDays(-(spec.RecentDays - 1)));
            completedFrom = completedFrom is { } explicitFrom && explicitFrom > recent ? explicitFrom : recent;
        }

        var cutoffs = new List<ZoneCutoff>();
        if (spec.View == TaskView.Overdue)
        {
            foreach (var zoneId in await session.Queries.ListOpenDueZoneIdsAsync(context.Space.Id, token))
            {
                // An unknown stored zone falls back to the profile zone rather than hiding the task.
                var local = TimeZoneInfo.ConvertTime(context.Now, FindZone(zoneId) ?? context.Zone);
                cutoffs.Add(new ZoneCutoff(zoneId, DateOnly.FromDateTime(local.DateTime), new TimeOnly(local.Hour, local.Minute, local.Second)));
            }
        }

        var openOnly = spec.View switch
        {
            TaskView.RecentlyCompleted => false,
            TaskView.Today or TaskView.Upcoming or TaskView.Overdue or TaskView.Waiting => true,
            _ => !spec.IncludeCompleted,
        };
        var isMyDay = spec.View == TaskView.MyDay;

        return new TaskQueryCriteria
        {
            SpaceId = context.Space.Id,
            ProfileId = context.Profile.Id,
            View = spec.View,
            ContainerId = string.IsNullOrWhiteSpace(spec.ContainerId) ? null : spec.ContainerId,
            TagId = string.IsNullOrWhiteSpace(spec.TagId) ? null : spec.TagId,
            Text = string.IsNullOrWhiteSpace(spec.Text) ? null : spec.Text.Trim(),
            Statuses = spec.View == TaskView.Waiting ? [TaskItemStatus.WaitingOnSomeone] : [.. spec.Statuses.Distinct()],
            Priorities = [.. spec.Priorities.Distinct()],
            Category = spec.Category,
            OpenOnly = openOnly,
            DoneOnly = spec.View == TaskView.RecentlyCompleted,
            // Opening one archived list shows its tasks, and a task chosen for My Day stays visible there.
            IncludeArchived = spec.IncludeArchived || spec.View == TaskView.Container || isMyDay,
            PlannedFrom = spec.Planned?.From,
            PlannedTo = spec.Planned?.To,
            DueFrom = spec.Due?.From,
            DueTo = spec.Due?.To,
            CompletedFromUtc = completedFrom,
            CompletedBeforeUtc = completedBefore,
            PlannedOrDueOn = spec.View == TaskView.Today ? today : null,
            UpcomingAfter = spec.View == TaskView.Upcoming ? today : null,
            UpcomingThrough = spec.View == TaskView.Upcoming ? today.AddDays(spec.UpcomingDays) : null,
            OverdueBefore = spec.View == TaskView.Overdue ? today : null,
            OverdueZoneCutoffs = cutoffs,
            DayPlanOn = isMyDay ? spec.Day ?? today : null,
            Limit = Math.Clamp(spec.Limit, 1, MaxLimit),
            Cursor = string.IsNullOrWhiteSpace(spec.Cursor) ? null : spec.Cursor,
        };
    }

    private static void RequireOrdered(LocalDateRange? range, string field)
    {
        if (range is { From: { } from, To: { } to } && from > to)
        {
            throw AppException.Validation(field, "The start date must be on or before the end date.");
        }
    }

    internal static TimeZoneInfo? FindZone(string zoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    internal static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// The UTC instant a local date begins (half-open period boundaries, PLAN §6). A midnight
    /// skipped by a DST gap resolves forward to the first valid local time; an ambiguous
    /// midnight resolves to the earlier instant.
    /// </summary>
    public static DateTimeOffset StartOfLocalDayUtc(TimeZoneInfo zone, DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        for (var step = 0; step < 48 && zone.IsInvalidTime(local); step++)
        {
            local = local.AddMinutes(30);
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
