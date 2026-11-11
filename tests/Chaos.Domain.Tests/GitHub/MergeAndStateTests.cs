using Chaos.Domain.GitHub;
using Chaos.Domain.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class MergeAndStateTests
{
    [Test]
    [Arguments("a", "a", "a", "a", false, false)]
    [Arguments("a", "a", "b", "b", false, false)]
    [Arguments("a", "b", "a", "b", true, false)]
    [Arguments("a", "b", "b", "b", false, false)]
    [Arguments("a", "b", "c", "c", false, true)]
    public async Task Field_ThreeWayCases_ReturnsExpectedResult(string original, string local, string remote, string expected, bool push, bool conflict)
    {
        var result = SyncMerge.Field(original, local, remote);
        await Assert.That(result.Value).IsEqualTo(expected);
        await Assert.That(result.Push).IsEqualTo(push);
        await Assert.That(result.Conflict).IsEqualTo(conflict);
    }

    [Test]
    public async Task Set_ConcurrentAddAndRemove_MergesByElement()
    {
        var result = SyncMerge.Set(new[] { "old", "keep" }, new[] { "keep", "local" }, new[] { "old", "keep", "remote" });
        await Assert.That(result.SetEquals(new[] { "keep", "local", "remote" })).IsTrue();
    }

    [Test]
    public async Task ToTask_ReopenedTerminal_UsesPreviousNonterminal()
    {
        var result = IssueStateMapping.ToTask(new(true, "reopened"), TaskItemStatus.Done, TaskItemStatus.WaitingOnSomeone);
        await Assert.That(result).IsEqualTo(TaskItemStatus.WaitingOnSomeone);
    }

    [Test]
    [Arguments("completed", TaskItemStatus.Done)]
    [Arguments("not_planned", TaskItemStatus.Cancelled)]
    [Arguments("duplicate", TaskItemStatus.Cancelled)]
    public async Task ToTask_ClosedReason_MapsStatus(string reason, TaskItemStatus expected)
    {
        await Assert.That(IssueStateMapping.ToTask(new(false, reason), TaskItemStatus.InProgress)).IsEqualTo(expected);
    }

    [Test]
    public async Task FromTask_DuplicateAlreadyCancelled_PreservesReason()
    {
        await Assert.That(IssueStateMapping.FromTask(TaskItemStatus.Cancelled, new(false, "duplicate")).Reason).IsEqualTo("duplicate");
    }

    [Test]
    public async Task FromTask_ReopenedIssue_SetsReopenedReason()
    {
        var state = IssueStateMapping.FromTask(TaskItemStatus.NeedsReview, new(false, "completed"));
        await Assert.That(state).IsEqualTo(new GitHubIssueState(true, "reopened"));
    }
}
