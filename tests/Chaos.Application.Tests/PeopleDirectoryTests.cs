using Chaos.Application.People;
using Chaos.Sync;

namespace Chaos.Application.Tests;

public sealed class PeopleDirectoryTests
{
    [Test]
    public async Task Resolve_RetainedMember_ReturnsNameAndIdentity()
    {
        const string id = "person:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var person = PeopleDirectory.Resolve(id, id, [new(id, "Sara Ali")]);
        await Assert.That(person.DisplayName).IsEqualTo("Sara Ali");
        await Assert.That(person.Initials).IsEqualTo("SA");
        await Assert.That(person.Kind).IsEqualTo(ActorKind.Person);
        await Assert.That(person.IsMe).IsTrue();
    }

    [Test]
    public async Task Resolve_MissingOrMalformedActor_ReturnsUnknown()
    {
        var person = PeopleDirectory.Resolve(null, null, []);
        await Assert.That(person.DisplayName).IsEqualTo("Unknown person");
        await Assert.That(person.IsMe).IsFalse();
        await Assert.That(person.Kind).IsNull();
    }
}
