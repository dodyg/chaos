namespace Chaos.Infrastructure.Sync;

/// <summary>
/// The children that move from a <c>RepointChildren</c> loser to its winner (PLAN2 §7.6). A child
/// that would itself collide hits its own rule when the winner row is written — every device makes
/// the same choice, so the outcome converges.
/// </summary>
internal static class SyncRepointSql
{
    /// <summary>Statements moving one loser's children to the winner; <c>$k0</c> is the loser's key column value.</summary>
    public static IReadOnlyList<string> For(string table, string loserKey, string winnerKey) => table switch
    {
        "DayPlans" =>
        [
            // A membership row that would collide (UX_DayPlanItems_PlanTask) stays with the winner:
            // the loser's copy is removed first, deterministically on both devices.
            """
            DELETE FROM DayPlanItems WHERE DayPlanId = $loser AND TaskId IN (SELECT TaskId FROM DayPlanItems WHERE DayPlanId = $winner);
            UPDATE DayPlanItems SET DayPlanId = $winner WHERE DayPlanId = $loser;
            """,
        ],
        "WeekPlans" =>
        [
            """
            DELETE FROM WeekAssignments WHERE WeekPlanId = $loser AND TaskId IN (SELECT TaskId FROM WeekAssignments WHERE WeekPlanId = $winner);
            UPDATE WeekAssignments SET WeekPlanId = $winner WHERE WeekPlanId = $loser;
            UPDATE WeekGoals SET WeekPlanId = $winner WHERE WeekPlanId = $loser;
            """,
        ],
        _ => [],
    };
}
