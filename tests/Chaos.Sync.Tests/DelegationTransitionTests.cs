using Chaos.Sync;

namespace Chaos.Sync.Tests;

/// <summary>The full transition table of a delegation (PLAN3 Q13 step 1): every pair, allowed or refused, and by whom.</summary>
public sealed class DelegationTransitionTests
{
    private static readonly DelegationState[] States =
    [
        DelegationState.Pending, DelegationState.Accepted, DelegationState.Question, DelegationState.Declined,
        DelegationState.Done, DelegationState.Closed, DelegationState.Withdrawn,
    ];

    [Test]
    [MethodDataSource(nameof(Pairs))]
    public async Task Actor_EveryPair_MatchesTheTable(DelegationState from, DelegationState to)
    {
        var expected = (from, to) switch
        {
            (DelegationState.Pending, DelegationState.Accepted) => DelegationParty.To,
            (DelegationState.Pending, DelegationState.Question) => DelegationParty.To,
            (DelegationState.Pending, DelegationState.Declined) => DelegationParty.To,
            (DelegationState.Question, DelegationState.Accepted) => DelegationParty.To,
            (DelegationState.Question, DelegationState.Declined) => DelegationParty.To,
            (DelegationState.Accepted, DelegationState.Done) => DelegationParty.To,
            (DelegationState.Pending, DelegationState.Withdrawn) => DelegationParty.From,
            (DelegationState.Question, DelegationState.Withdrawn) => DelegationParty.From,
            (DelegationState.Accepted, DelegationState.Withdrawn) => DelegationParty.From,
            (DelegationState.Done, DelegationState.Closed) => DelegationParty.From,
            (DelegationState.Done, DelegationState.Accepted) => DelegationParty.From,
            _ => DelegationParty.None,
        };
        await Assert.That(DelegationTransitions.Actor(from, to)).IsEqualTo(expected);
    }

    public static IEnumerable<(DelegationState, DelegationState)> Pairs() =>
        [.. from from in States from to in States select (from, to)];

    [Test]
    public async Task IsLive_OnlyPendingAcceptedAndQuestion()
    {
        foreach (var state in States)
        {
            await Assert.That(DelegationTransitions.IsLive(state)).IsEqualTo(state is DelegationState.Pending or DelegationState.Accepted or DelegationState.Question);
        }
        await Assert.That(DelegationTransitions.IsDefined((DelegationState)7)).IsFalse();
        await Assert.That(DelegationTransitions.IsDefined((DelegationState)(-1))).IsFalse();
    }

    [Test]
    public async Task FinalStates_AreFinal()
    {
        foreach (var state in new[] { DelegationState.Declined, DelegationState.Closed, DelegationState.Withdrawn })
        {
            foreach (var to in States)
            {
                await Assert.That(DelegationTransitions.Actor(state, to)).IsEqualTo(DelegationParty.None);
            }
        }
    }
}
