using Chaos.Sync;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>
/// One deterministic duplicate rule of the catalog (PLAN2 §7.6). The applier finds the colliding
/// row and decides the winner; the rule settles the loser on both sides of the collision.
/// </summary>
internal interface IUniqueRule
{
    /// <summary>
    /// Runs the rule's effect on the loser (local or incoming) and returns the merge to write;
    /// a rule may reshape the winner's groups (the daily page's appended text).
    /// </summary>
    MergeResult ResolveLoser(UniqueConflict conflict, MergeResult merge);

    /// <summary>
    /// Writes an incoming loser in the shape the rule chose, so both devices hold the same rows;
    /// its clocks are stored so the change does not win again.
    /// </summary>
    Task WriteIncomingLoserAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct);

    /// <summary>The rule for a catalog rule kind. Adopt is never resolved here: it waits for device join.</summary>
    static IUniqueRule For(SyncUniqueRuleKind kind) => kind switch
    {
        SyncUniqueRuleKind.RenameLoser => RenameLoserRule.Instance,
        SyncUniqueRuleKind.MergeTag => MergeTagRule.Instance,
        SyncUniqueRuleKind.AppendText => AppendTextRule.Instance,
        SyncUniqueRuleKind.KeepLowestRetireOther => KeepLowestRetireOtherRule.Instance,
        SyncUniqueRuleKind.RepointChildren => RepointChildrenRule.Instance,
        _ => throw new InvalidOperationException($"The unique rule '{kind}' is not resolved during an ordinary apply."),
    };
}
