using Chaos.Sync;

namespace Chaos.Infrastructure.Sync;

/// <summary>Parses one outbox/wire change (PLAN2 §7.12 shape); the format lives in <see cref="SyncJson"/>, shared with the service.</summary>
public static class SyncChangeParser
{
    public static SyncChange Parse(string payload) => SyncJson.ParseChange(payload);
}
