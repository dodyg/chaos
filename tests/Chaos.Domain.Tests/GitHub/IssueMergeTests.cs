using Chaos.Domain.GitHub;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class IssueMergeTests
{
    private static IssueProjection Original() => new("base title", "base body", new(true, null),
        new HashSet<long> { 1, 2 }, new HashSet<long> { 10 });

    [Test]
    public async Task Issue_IndependentEditsAndConflict_ReturnsPushAndLosingValue()
    {
        var original = Original();
        var local = original with { Title = "local title", Body = "local body" };
        var remote = original with { Title = "remote title", State = new(false, "completed") };
        var result = SyncMerge.Issue(original, local, remote);
        await Assert.That(result.Value.Title).IsEqualTo("remote title");
        await Assert.That(result.Value.Body).IsEqualTo("local body");
        await Assert.That(result.Value.State).IsEqualTo(remote.State);
        await Assert.That(result.Push.Title).IsNull();
        await Assert.That(result.Push.Body).IsEqualTo("local body");
        await Assert.That(result.Push.State).IsNull();
        await Assert.That(result.Conflicts.Title).IsEqualTo("local title");
        await Assert.That(result.Conflicts.Body).IsNull();
    }

    [Test]
    public async Task Issue_ConcurrentSetEdits_ReturnsOnlyRequiredRemoteDeltas()
    {
        var original = Original();
        var local = original with { LabelIds = new HashSet<long> { 2, 3 }, AssigneeIds = new HashSet<long>() };
        var remote = original with { LabelIds = new HashSet<long> { 1, 2, 4 }, AssigneeIds = new HashSet<long> { 10, 11 } };
        var result = SyncMerge.Issue(original, local, remote);
        await Assert.That(result.Value.LabelIds.SetEquals(new long[] { 2, 3, 4 })).IsTrue();
        await Assert.That(result.Push.AddLabels.SetEquals(new long[] { 3 })).IsTrue();
        await Assert.That(result.Push.RemoveLabels.SetEquals(new long[] { 1 })).IsTrue();
        await Assert.That(result.Value.AssigneeIds.SetEquals(new long[] { 11 })).IsTrue();
        await Assert.That(result.Push.AddAssignees.Count).IsEqualTo(0);
        await Assert.That(result.Push.RemoveAssignees.SetEquals(new long[] { 10 })).IsTrue();
        await Assert.That(original.LabelIds.SetEquals(new long[] { 1, 2 })).IsTrue();
    }

    [Test]
    public async Task Issue_EmptyBodyAndStateConflict_PreservesExactLosingValues()
    {
        var original = Original();
        var local = original with { Body = "", State = new(false, "completed") };
        var remote = original with { Body = "remote body", State = new(false, "not_planned") };
        var result = SyncMerge.Issue(original, local, remote);
        await Assert.That(result.Conflicts.Body).IsEqualTo("");
        await Assert.That(result.Conflicts.State).IsEqualTo((GitHubIssueState?)local.State);
        await Assert.That(result.Value.EquivalentTo(remote)).IsTrue();
    }
}
