using Chaos.Sync;

namespace Chaos.Sync.Tests;

public sealed class ActorIdTests
{
    [Test]
    public async Task TryParse_PersonAndAgent_RoundTrip()
    {
        foreach (var kind in new[] { ActorKind.Person, ActorKind.Agent })
        {
            var original = new ActorId(kind, Guid.NewGuid());
            await Assert.That(ActorId.TryParse(original.ToString(), out var parsed)).IsTrue();
            await Assert.That(parsed).IsEqualTo(original);
        }
    }

    [Test]
    public async Task TryParse_InvalidValues_AreRejected()
    {
        foreach (var value in new string?[] { null, "local", "github:someone", "person:no", "agent:" })
            await Assert.That(ActorId.TryParse(value, out _)).IsFalse();
    }
}
