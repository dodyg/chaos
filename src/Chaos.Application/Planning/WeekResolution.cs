using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Planning;

namespace Chaos.Application.Planning;

/// <summary>
/// Which week a local date belongs to (PLAN §10). An existing plan keeps the week-start date it
/// was created with, so changing the first weekday never rewrites historical keys; a date no
/// plan covers falls in the week aligned to the profile's current first weekday.
/// </summary>
internal static class WeekResolution
{
    /// <summary>The non-superseded plan covering <paramref name="date"/>: a confirmed one first, then the latest start.</summary>
    public static async Task<WeekPlanRecord?> PlanContainingAsync(IDataSession session, string profileId, DateOnly date, CancellationToken token) =>
        (await session.Weeks.ListOverlappingAsync(profileId, date, date, token))
            .OrderByDescending(plan => plan.State == WeekPlanState.Confirmed)
            .ThenByDescending(plan => plan.WeekStartDate)
            .FirstOrDefault();

    public static async Task<WeekPlanRecord?> ConfirmedContainingAsync(IDataSession session, string profileId, DateOnly date, CancellationToken token) =>
        await PlanContainingAsync(session, profileId, date, token) is { State: WeekPlanState.Confirmed } plan ? plan : null;

    /// <summary>The start date of the week containing <paramref name="date"/>.</summary>
    public static async Task<DateOnly> StartForAsync(IDataSession session, ProfileRecord profile, DateOnly date, CancellationToken token) =>
        (await PlanContainingAsync(session, profile.Id, date, token))?.WeekStartDate ?? WeekCalendar.StartOf(date, profile.FirstWeekday);
}
