using Chaos.Domain.GitHub;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class ChecklistReconcilerTests
{
    [Test]
    public async Task Match_ReorderedDuplicatesAndRenames_UsesStableMatches()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var previous = new[] { new ChecklistBase(first, 0, "same"), new ChecklistBase(second, 1, "same"), new ChecklistBase(removed, 2, "gone") };
        var items = IssueBody.ScanChecklist("- [ ] renamed\n- [x] same\n");
        var result = ChecklistReconciler.Match(previous, items);
        await Assert.That(result.Items[0].SubtaskId).IsEqualTo(first);
        await Assert.That(result.Items[1].SubtaskId).IsEqualTo(second);
        await Assert.That(result.RemovedSubtaskIds.Single()).IsEqualTo(removed);
    }
}
