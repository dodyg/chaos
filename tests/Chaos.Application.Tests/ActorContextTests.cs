using Chaos.Application.Abstractions;
using Chaos.Application.Activity;

namespace Chaos.Application.Tests;

public sealed class ActorContextTests
{
    [Test]
    public async Task CurrentActor_NestedScopes_PreservesAndRestoresActor()
    {
        var context = AmbientActorContext.Instance;
        await Assert.That(context.CurrentActor("profile")).IsEqualTo("person:profile");
        using (ActivityScope.Open(ActivitySources.User, "test", actorId: "agent:123"))
        {
            using (ActivityScope.Open(ActivitySources.System, "nested"))
                await Assert.That(context.CurrentActor("profile")).IsEqualTo("agent:123");
            await Assert.That(context.CurrentActor("profile")).IsEqualTo("agent:123");
        }
        await Assert.That(context.CurrentActor("profile")).IsEqualTo("person:profile");
    }
}
