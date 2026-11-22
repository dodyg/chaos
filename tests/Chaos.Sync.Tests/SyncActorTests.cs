using System.Text;
using System.Text.Json;
using Chaos.Sync;

namespace Chaos.Sync.Tests;

public sealed class SyncActorTests
{
    [Test]
    public async Task ChangeActor_RoundTripsWithoutChangingOlderPayloads()
    {
        var actor = Guid.NewGuid();
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
            SyncJson.WriteChange(writer, "change", "task", "key", SyncOp.Purge,
                new Dictionary<string, SyncGroupState>(), 1, actor);
        var parsed = SyncJson.ParseChange(Encoding.UTF8.GetString(bytes.ToArray()));
        await Assert.That(parsed.ActorId).IsEqualTo(actor.ToString("D"));
        var old = SyncJson.ParseChange("{\"changeId\":\"c\",\"kind\":\"task\",\"key\":\"k\",\"op\":\"purge\",\"groups\":{}}");
        await Assert.That(old.ActorId).IsNull();
    }
}
