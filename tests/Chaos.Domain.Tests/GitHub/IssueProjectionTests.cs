using Chaos.Domain.GitHub;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class IssueProjectionTests
{
    [Test]
    public async Task EquivalentTo_SeparateReorderedSets_ComparesTheirContents()
    {
        var left = new IssueProjection("title", "body", new(true, null), new HashSet<long> { 1, 2 }, new HashSet<long> { 3 });
        var right = left with { LabelIds = new HashSet<long> { 2, 1 }, AssigneeIds = new HashSet<long> { 3 } };
        await Assert.That(left.EquivalentTo(right)).IsTrue();
        await Assert.That(left == right).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        await Assert.That(left.EquivalentTo(right with { LabelIds = new HashSet<long> { 1 } })).IsFalse();
        await Assert.That(left.EquivalentTo(right with { Body = "changed" })).IsFalse();
    }
}
