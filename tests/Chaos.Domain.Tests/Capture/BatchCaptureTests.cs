using Chaos.Domain.Capture;

namespace Chaos.Domain.Tests.Capture;

public sealed class BatchCaptureTests
{
    [Test]
    public async Task Split_IndentedBullets_BecomeSubtasksWithCheckedState()
    {
        var split = BatchCapture.Split("Pack bag\n  - socks\n  - [x] charger\nBook taxi");
        await Assert.That(split.IsBatch).IsTrue();
        await Assert.That(split.Errors.Count).IsEqualTo(0);
        await Assert.That(split.Items.Count).IsEqualTo(2);
        await Assert.That(split.Items[0].Subtasks.Select(line => line.Text).ToArray()).IsEquivalentTo(new[] { "socks", "charger" });
        await Assert.That(split.Items[0].Subtasks[1].Checked).IsTrue();
        await Assert.That(split.Items[1].Task.LineNumber).IsEqualTo(4);
    }

    [Test]
    public async Task Split_TabIndent_CountsAsOneLevel()
    {
        var split = BatchCapture.Split("Trip\r\n\tPassport\r\n\tTickets");
        await Assert.That(split.Errors.Count).IsEqualTo(0);
        await Assert.That(split.Items.Single().Subtasks.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Split_SecondLevel_IsRejectedWithLineNumber()
    {
        var split = BatchCapture.Split("Move\n  Pack\n    Tape");
        await Assert.That(split.Errors.Single().LineNumber).IsEqualTo(3);
    }

    [Test]
    public async Task Split_MixedTabsAndSpaces_IsRejected()
    {
        var split = BatchCapture.Split("Move\n \tPack");
        await Assert.That(split.Errors.Single().Message.Contains("mixes tabs and spaces")).IsTrue();
    }

    [Test]
    public async Task Split_OverFiveHundredLines_IsRejected()
    {
        var split = BatchCapture.Split(string.Join('\n', Enumerable.Range(1, 501).Select(index => $"Task {index}")));
        await Assert.That(split.Items.Count).IsEqualTo(0);
        await Assert.That(split.Errors.Single().LineNumber).IsEqualTo(501);
    }

    [Test]
    public async Task Split_SingleLine_IsNotABatchAndKeepsMarker()
    {
        var split = BatchCapture.Split("- tomorrow call Sam");
        await Assert.That(split.IsBatch).IsFalse();
        await Assert.That(split.Items.Single().Task.Text).IsEqualTo("- tomorrow call Sam");
    }

    [Test]
    public async Task Split_SourceStart_PointsAtTextAfterMarker()
    {
        var text = "Alpha\n  * beta";
        var line = BatchCapture.Split(text).Items[0].Subtasks[0];
        await Assert.That(text[line.SourceStart..]).IsEqualTo("beta");
    }
}
