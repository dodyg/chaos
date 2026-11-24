using Chaos.Domain.GitHub;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class IssueProjectionTests
{
    [Test]
    public async Task EquivalentTo_SeparateReorderedSets_ComparesTheirContents()
    {
        var left = new IssueProjection("title", "body", new(true, null), new HashSet<long> { 1, 2 }, IssueProjection.Logins(["lina", "omar"]));
        var right = left with { LabelIds = new HashSet<long> { 2, 1 }, Assignees = IssueProjection.Logins(["Omar", "LINA"]) };
        await Assert.That(left.EquivalentTo(right)).IsTrue();
        await Assert.That(left == right).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        await Assert.That(left.EquivalentTo(right with { Assignees = IssueProjection.Logins(["omar"]) })).IsFalse();
        await Assert.That(left.EquivalentTo(right with { LabelIds = new HashSet<long> { 1 } })).IsFalse();
        await Assert.That(left.EquivalentTo(right with { Body = "changed" })).IsFalse();
    }
}
