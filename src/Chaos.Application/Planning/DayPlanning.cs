using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;

namespace Chaos.Application.Planning;

/// <summary>
/// Day plan writes shared by My Day and My Week (PLAN §10 "Day initialization"): creating a
/// day once, seeding it from the confirmed week in the same transaction, and placing tasks.
/// </summary>
internal static class DayPlanning
{
    /// <summary>
    /// Creates the plan for one date once. When a confirmed week covers the date, its
    /// assignments for that date seed the new day (origin Week) and the revision is recorded;
    /// an existing day is never reseeded, so a task the person removed stays removed.
    /// </summary>
    public static async Task<DayPlanRecord> EnsurePlanAsync(IDataSession session, IIdGenerator ids, string profileId, DateOnly date, DateTimeOffset now, CancellationToken token)
    {
        if (await session.DayPlans.FindAsync(profileId, date, token) is { } existing)
        {
            return existing;
        }

        var week = await WeekResolution.ConfirmedContainingAsync(session, profileId, date, token);
        var created = await session.DayPlans.TryAddAsync(new DayPlanRecord
        {
            Id = StorageFormats.Id(ids.NewId()),
            ProfileId = profileId,
            LocalDate = date,
            SeededWeekPlanId = week?.Id,
            SeededWeekRevision = week?.ConfirmedRevision,
            InitializedAtUtc = now,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }, token);
        var plan = await session.DayPlans.FindAsync(profileId, date, token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "My Day could not be prepared. Try again.");

        if (created && week is not null)
        {
            foreach (var taskId in await AssignedOpenTaskIdsAsync(session, week.Id, date, token))
            {
                await PlaceAsync(session, ids, plan, taskId, DayPlanItemOrigin.Week, now, token);
            }
        }

        return plan;
    }

    /// <summary>Open, nondeleted tasks a confirmed week assigns to <paramref name="date"/>, in day order.</summary>
    public static async Task<IReadOnlyList<string>> AssignedOpenTaskIdsAsync(IDataSession session, string weekPlanId, DateOnly date, CancellationToken token)
    {
        var taskIds = new List<string>();
        foreach (var assignment in await session.Weeks.ListAssignmentsAsync(weekPlanId, token))
        {
            if (assignment.LocalDate == date && await session.Tasks.GetAsync(assignment.TaskId, token) is { IsTerminal: false })
            {
                taskIds.Add(assignment.TaskId);
            }
        }

        return taskIds;
    }

    /// <returns>True when the task was added or brought back; false when it was already on the day.</returns>
    public static async Task<bool> PlaceAsync(IDataSession session, IIdGenerator ids, DayPlanRecord plan, string taskId, DayPlanItemOrigin origin, DateTimeOffset now, CancellationToken token)
    {
        var existing = await session.DayPlans.FindItemAsync(plan.Id, taskId, token);
        if (existing is { RemovedAtUtc: null })
        {
            return false;
        }

        var active = await session.DayPlans.ListItemsAsync(plan.Id, includeRemoved: false, token);
        var position = active.Count == 0 ? 0 : active.Max(item => item.Position) + RankOrdering.Gap;
        if (existing is not null)
        {
            await UpdateItemAsync(session, existing with
            {
                RemovedAtUtc = null,
                Origin = origin,
                AddedAtUtc = now,
                Position = position,
                UpdatedAtUtc = now,
                Version = existing.Version + 1,
            }, existing.Version, token);
            return true;
        }

        await session.DayPlans.AddItemAsync(new DayPlanItemRecord
        {
            Id = StorageFormats.Id(ids.NewId()),
            DayPlanId = plan.Id,
            TaskId = taskId,
            Position = position,
            Origin = origin,
            AddedAtUtc = now,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }, token);
        return true;
    }

    /// <summary>Takes a task off a day, keeping its row so it is never silently put back.</summary>
    public static async Task<bool> RemoveAsync(IDataSession session, DayPlanRecord plan, string taskId, DateTimeOffset now, CancellationToken token)
    {
        var item = await session.DayPlans.FindItemAsync(plan.Id, taskId, token);
        if (item is null || item.RemovedAtUtc is not null)
        {
            return false;
        }

        await UpdateItemAsync(session, item with { RemovedAtUtc = now, UpdatedAtUtc = now, Version = item.Version + 1 }, item.Version, token);
        return true;
    }

    public static async Task UpdateItemAsync(IDataSession session, DayPlanItemRecord item, long expectedVersion, CancellationToken token)
    {
        if (await session.DayPlans.UpdateItemAsync(item, expectedVersion, token) is null)
        {
            throw AppException.VersionConflict("My Day changed in the meantime. Refresh and try again.");
        }
    }
}
