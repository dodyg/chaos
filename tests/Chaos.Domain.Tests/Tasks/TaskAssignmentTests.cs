using Chaos.Domain.Tasks;

namespace Chaos.Domain.Tests.Tasks;

public sealed class TaskAssignmentTests
{
    [Test]
    public async Task EachCompletion_UsesLatestLiveCompletionRegardlessOfOrder()
    {
        var first = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        var last = first.AddHours(1);
        AssignmentCompletion[] rows = [new(first, null), new(last, null), new(null, last)];
        await Assert.That(TaskAssignment.EachCompletion(rows)).IsEqualTo(last);
        await Assert.That(TaskAssignment.EachCompletion(rows.Reverse())).IsEqualTo(last);
    }

    [Test]
    public async Task EachCompletion_RequiresAtLeastOneLiveAssigneeAndAllMustComplete()
    {
        var time = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        await Assert.That(TaskAssignment.EachCompletion([])).IsNull();
        await Assert.That(TaskAssignment.EachCompletion([new(time, time)])).IsNull();
        await Assert.That(TaskAssignment.EachCompletion([new(time, null), new(null, null)])).IsNull();
    }
}
