using System.Collections.Concurrent;
using System.Text.Json;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

public sealed record PresenceKey(Guid SpaceId, string Kind, string EntityId);

/// <summary>
/// Who has a shared pad note, page or task open (PLAN3 §9.9). In memory only, never stored: a client reports every 20 seconds
/// and leaves on close, and a report not renewed within 45 seconds expires. A change goes as <c>{"presence":{…}}</c> on the
/// event stream of every member of that space who is connected to this process. A Personal entity never reaches here.
/// </summary>
public sealed class PresenceService(TimeProvider clock, SyncBroadcaster broadcaster)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);
    public static readonly string[] Kinds = ["pad", "page", "task"];
    public static readonly string[] States = ["viewing", "editing"];

    private sealed record Entry(string State, DateTimeOffset ExpiresAt);

    private sealed class Room
    {
        public readonly Dictionary<Guid, Entry> People = [];
        public Guid[] Audience = [];
    }

    private readonly ConcurrentDictionary<PresenceKey, Room> _rooms = new();

    /// <summary>Records that <paramref name="account"/> has the entity open; <paramref name="audience"/> are the space's members to tell.</summary>
    public void Report(PresenceKey key, Guid account, string state, IReadOnlyList<Guid> audience)
    {
        var room = _rooms.GetOrAdd(key, _ => new Room());
        bool changed;
        lock (room)
        {
            room.Audience = [.. audience];
            changed = !room.People.TryGetValue(account, out var existing) || existing.State != state;
            room.People[account] = new Entry(state, clock.GetUtcNow() + Lifetime);
        }

        if (changed) Publish(key, room);
    }

    public void Leave(PresenceKey key, Guid account, IReadOnlyList<Guid> audience)
    {
        if (!_rooms.TryGetValue(key, out var room)) return;
        bool changed;
        lock (room)
        {
            room.Audience = [.. audience];
            changed = room.People.Remove(account);
        }

        if (changed) Publish(key, room);
        Drop(key, room);
    }

    /// <summary>Removes reports not renewed in time and tells the room. Called by the sweep and by tests.</summary>
    public void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, room) in _rooms)
        {
            bool changed;
            lock (room)
            {
                changed = room.People.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList().Count(person => room.People.Remove(person)) > 0;
            }

            if (changed) Publish(key, room);
            Drop(key, room);
        }
    }

    public IReadOnlyList<Guid> Members(PresenceKey key)
    {
        if (!_rooms.TryGetValue(key, out var room)) return [];
        lock (room) return [.. room.People.Keys.Order()];
    }

    /// <summary>The event line for one entity's current presence (also sent to a stream that has just connected).</summary>
    public static string Payload(PresenceKey key, IReadOnlyDictionary<Guid, string> people) =>
        JsonSerializer.Serialize(new
        {
            presence = new
            {
                spaceId = key.SpaceId,
                entityKind = key.Kind,
                entityId = key.EntityId,
                members = people.Keys.Order().ToArray(),
                editing = people.Where(pair => pair.Value == "editing").Select(pair => pair.Key).Order().ToArray(),
            },
        });

    private void Publish(PresenceKey key, Room room)
    {
        Guid[] audience;
        string payload;
        lock (room)
        {
            audience = room.Audience;
            payload = Payload(key, room.People.ToDictionary(pair => pair.Key, pair => pair.Value.State));
        }

        foreach (var account in audience) broadcaster.PublishPresence(account, payload);
    }

    private void Drop(PresenceKey key, Room room)
    {
        lock (room)
        {
            if (room.People.Count == 0) _rooms.TryRemove(new KeyValuePair<PresenceKey, Room>(key, room));
        }
    }

    /// <summary>The accounts of a space's people, to tell about a change.</summary>
    public static async Task<IReadOnlyList<Guid>> AudienceAsync(ChaosDbContext db, Guid spaceId, CancellationToken ct) =>
        await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null)
            .Where(m => m.AccountId != null).Select(m => m.AccountId!.Value).ToListAsync(ct);
}

/// <summary>Expires presence reports that were not renewed, so a closed window or a lost connection clears within the lifetime.</summary>
public sealed class PresenceSweepService(PresenceService presence, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) presence.Sweep();
    }
}
