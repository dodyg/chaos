using Chaos.Domain.Recurrence;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Tests.Recurrence;

public sealed class RotationTests
{
    private static readonly Rotation Trio = new(["omar", "sara", "lee"], 1);

    [Test]
    public async Task FirstIndexIsStartAndEachNextIsOneMore()
    {
        await Assert.That(Trio.NextIndex(null)).IsEqualTo(1);
        await Assert.That(Trio.NextIndex(1)).IsEqualTo(2);
        await Assert.That(Trio.NextIndex(5)).IsEqualTo(6);
    }

    [Test]
    [Arguments(0, "omar")]
    [Arguments(1, "sara")]
    [Arguments(2, "lee")]
    [Arguments(3, "omar")]
    [Arguments(7, "sara")]
    public async Task HostIsMemberAtIndexModuloCount(int index, string expected) =>
        await Assert.That(Trio.HostFor(index, _ => true)).IsEqualTo(expected);

    [Test]
    public async Task HostSkipsMembersWhoLeftAndWrapsAround()
    {
        await Assert.That(Trio.HostFor(1, member => member != "sara")).IsEqualTo("lee");
        await Assert.That(Trio.HostFor(2, member => member == "omar")).IsEqualTo("omar");
        await Assert.That(Trio.HostFor(0, _ => false)).IsNull();
    }

    [Test]
    public async Task UpcomingListsTheNextHostsInOrder()
    {
        await Assert.That(Trio.Upcoming(null, 2, _ => true)).IsEquivalentTo(new[] { "sara", "lee" });
        await Assert.That(Trio.Upcoming(1, 2, member => member != "sara")).IsEquivalentTo(new[] { "lee", "omar" });
    }

    [Test]
    public async Task HostIsDeterministicForTheSameInputs()
    {
        var first = Enumerable.Range(0, 20).Select(i => Trio.HostFor(i, member => member != "sara")).ToList();
        var second = Enumerable.Range(0, 20).Select(i => new Rotation(["omar", "sara", "lee"], 1).HostFor(i, member => member != "sara")).ToList();
        await Assert.That(first).IsEquivalentTo(second);
    }

    [Test]
    public async Task ValidateRejectsEmptyDuplicateAndOutOfRangeStart()
    {
        await Assert.That(() => new Rotation([], 0).Validate()).Throws<DomainValidationException>();
        await Assert.That(() => new Rotation(["a", "a"], 0).Validate()).Throws<DomainValidationException>();
        await Assert.That(() => new Rotation(["a", "b"], 2).Validate()).Throws<DomainValidationException>();
        await Assert.That(() => new Rotation(["a", "b"], -1).Validate()).Throws<DomainValidationException>();
        Trio.Validate();
    }
}
