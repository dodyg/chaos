namespace Chaos.Sync;

/// <summary>
/// The state of one delegation (PLAN3 9.11). The integer is the stored <c>State</c> column. A
/// delegation is <see cref="Pending"/> until the recipient answers; <see cref="Accepted"/>,
/// <see cref="Question"/> and <see cref="Pending"/> are the live states (one live delegation per
/// task); the rest are final, and handing the work to someone else is a new delegation.
/// </summary>
public enum DelegationState
{
    Pending = 0,
    Accepted = 1,
    Question = 2,
    Declined = 3,
    Done = 4,
    Closed = 5,
    Withdrawn = 6,
}

/// <summary>Which party of a delegation may make a transition: the delegator or the recipient.</summary>
public enum DelegationParty
{
    None = 0,
    From = 1,
    To = 2,
}

/// <summary>
/// The transition table of a delegation, as one source of truth for the desktop commands and the
/// service's permission check (the service references no client project, so it lives here beside
/// the routing table, as data). The recipient accepts, asks, declines and reports done; the
/// delegator takes the work back, closes a reviewed result or reopens it.
/// </summary>
public static class DelegationTransitions
{
    /// <summary>The one party that may move a delegation from <paramref name="from"/> to <paramref name="to"/>; <see cref="DelegationParty.None"/> when no such move exists.</summary>
    public static DelegationParty Actor(DelegationState from, DelegationState to) => (from, to) switch
    {
        (DelegationState.Pending, DelegationState.Accepted or DelegationState.Question or DelegationState.Declined) => DelegationParty.To,
        (DelegationState.Question, DelegationState.Accepted or DelegationState.Declined) => DelegationParty.To,
        (DelegationState.Accepted, DelegationState.Done) => DelegationParty.To,
        (DelegationState.Pending or DelegationState.Question or DelegationState.Accepted, DelegationState.Withdrawn) => DelegationParty.From,
        (DelegationState.Done, DelegationState.Closed or DelegationState.Accepted) => DelegationParty.From,
        _ => DelegationParty.None,
    };

    public static bool IsLive(DelegationState state) => state is DelegationState.Pending or DelegationState.Accepted or DelegationState.Question;

    public static bool IsDefined(DelegationState state) => state is >= DelegationState.Pending and <= DelegationState.Withdrawn;

    /// <summary>Nudges per delegation per rolling window (E8); a server configuration value in spirit, a constant here.</summary>
    public const int NudgesPerDelegation = 3;
    public static readonly TimeSpan NudgeWindow = TimeSpan.FromHours(24);
    public const int MaxMessageLength = 4_000;
    public const int MaxDoneDefinitionLength = 4_000;
    public const int MaxNudgeLength = 2_000;
}
