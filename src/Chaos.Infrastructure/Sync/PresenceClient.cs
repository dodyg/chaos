using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>One person who has a shared note, page or task open, as last reported by the service.</summary>
public sealed record PresentMember(string MemberId, string DisplayName, bool Editing);

/// <summary>
/// This device's end of presence (PLAN3 §9.9). It reports "I have this open" and leaves on close, and it keeps what the service
/// pushes about everyone else. Memory only: nothing is stored, a Personal space is never reported and a report not refreshed in
/// 45 seconds is forgotten, the same lifetime the service uses.
/// </summary>
public sealed class PresenceClient(SqliteApplicationStore store, IChaosServiceClient service, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(45);

    private sealed record Seen(IReadOnlyList<string> Members, IReadOnlyList<string> Editing, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string spaceId, string kind, string entityId) => $"{spaceId}|{kind}|{entityId}".ToLowerInvariant();

    /// <summary>Tells the service this entity is open here. Returns false when nothing was sent (Personal space, or not signed in).</summary>
    public Task<bool> ReportAsync(string spaceId, string kind, string entityId, bool editing, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v2/presence", spaceId, kind, entityId, editing, ct);

    public Task<bool> LeaveAsync(string spaceId, string kind, string entityId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"v2/presence?spaceId={Uri.EscapeDataString(spaceId)}&entityKind={Uri.EscapeDataString(kind)}&entityId={Uri.EscapeDataString(entityId)}",
            spaceId, kind, entityId, false, ct);

    private async Task<bool> SendAsync(HttpMethod method, string path, string spaceId, string kind, string entityId, bool editing, CancellationToken ct)
    {
        if (await IsSharedAsync(spaceId, ct) is not true) return false;
        var body = method == HttpMethod.Post
            ? JsonSerializer.Serialize(new { spaceId, entityKind = kind, entityId, state = editing ? "editing" : "viewing" })
            : null;
        try
        {
            var answer = await service.CallAsync(method, path, body, ct);
            return answer.Status is >= 200 and < 300;
        }
        catch (ChaosServiceException error)
        {
            System.Diagnostics.Trace.TraceInformation("Presence was not sent ({0}).", error.Kind);
            return false;
        }
    }

    /// <summary>True for a shared space, false for Personal or an unknown one: only shared work has anyone to tell.</summary>
    private Task<bool?> IsSharedAsync(string spaceId, CancellationToken ct) =>
        store.QueryAsync<bool?>((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT Kind FROM Spaces WHERE Id = $id AND DeletedAtUtc IS NULL;";
            command.Parameters.AddWithValue("$id", spaceId);
            return Task.FromResult(command.ExecuteScalar() is long kind ? kind == 2 : (bool?)null);
        }, ct);

    /// <summary>Takes one event line pushed by the service. Returns true when it was a presence line.</summary>
    public bool Apply(string line)
    {
        var text = line.StartsWith("data:", StringComparison.Ordinal) ? line["data:".Length..] : line;
        if (!text.Contains("\"presence\"", StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("presence", out var presence)) return false;
            static List<string> Ids(JsonElement element) =>
                element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray().Select(item => item.GetString() ?? string.Empty)] : [];
            _seen[Key(presence.GetProperty("spaceId").GetString() ?? "", presence.GetProperty("entityKind").GetString() ?? "", presence.GetProperty("entityId").GetString() ?? "")] =
                new Seen(Ids(presence.GetProperty("members")), Ids(presence.GetProperty("editing")), clock.GetUtcNow());
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceInformation("A presence event could not be read: {0}", error.GetType().Name);
            return false;
        }
    }

    /// <summary>The other people who have the entity open, with names from the space's member list. Never includes this account.</summary>
    public async Task<IReadOnlyList<PresentMember>> WhoAsync(string spaceId, string kind, string entityId, CancellationToken ct)
    {
        if (!_seen.TryGetValue(Key(spaceId, kind, entityId), out var seen) || clock.GetUtcNow() - seen.At > Lifetime) return [];
        List<string> others;
        return await store.QueryAsync<IReadOnlyList<PresentMember>>((session, _) =>
        {
            using (var self = session.Connection().CreateCommand())
            {
                self.CommandText = "SELECT AccountId FROM SyncState WHERE Id = 1;";
                var me = self.ExecuteScalar() as string;
                others = [.. seen.Members.Where(id => !string.Equals(id, me, StringComparison.OrdinalIgnoreCase))];
            }

            var result = new List<PresentMember>();
            foreach (var id in others)
            {
                using var command = session.Connection().CreateCommand();
                command.CommandText = "SELECT DisplayName FROM SpaceMembers WHERE SpaceId = $space AND MemberId = $member COLLATE NOCASE AND RemovedAtUtc IS NULL LIMIT 1;";
                command.Parameters.AddWithValue("$space", spaceId);
                command.Parameters.AddWithValue("$member", id);
                var name = command.ExecuteScalar() as string;
                result.Add(new PresentMember(id, string.IsNullOrWhiteSpace(name) ? "Someone" : name, seen.Editing.Contains(id, StringComparer.OrdinalIgnoreCase)));
            }

            return Task.FromResult<IReadOnlyList<PresentMember>>(result);
        }, ct);
    }
}
