using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.People;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Validation;

namespace Chaos.Application.Tasks;

/// <summary>A member a rotating series will hand the next occurrence to.</summary>
public sealed record RotationHost(string MemberId, string DisplayName);

/// <summary>Reads, validates and applies a series' rotation (PLAN3 9.10).</summary>
internal static class RotationSupport
{
    public static string? Write(Rotation? rotation) =>
        rotation is null ? null : JsonSerializer.Serialize(rotation, StoredJsonFormats.Options);

    public static Rotation? Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Rotation>(json, StoredJsonFormats.Options);

    /// <summary>Members who can be given work: current human owners and editors (the Q7 assignment rule).</summary>
    public static Func<string, bool> Live(IReadOnlyList<SpacePerson> people)
    {
        var live = people.Where(p => !p.Former && p.Role is 1 or 2 && p.ActorId.StartsWith("person:", StringComparison.Ordinal))
            .Select(p => p.ActorId["person:".Length..]).ToHashSet(StringComparer.Ordinal);
        return live.Contains;
    }

    /// <summary>Rejects a rotation outside a shared space or naming anyone who cannot be assigned work.</summary>
    public static async Task ValidateAsync(IDataSession session, string spaceId, Rotation rotation, CancellationToken ct)
    {
        try { rotation.Validate(); }
        catch (DomainValidationException error) { throw AppException.Validation("rotation", error.Message); }
        var space = await session.Spaces.GetAsync(spaceId, ct);
        if (space?.Kind != Chaos.Domain.Tasks.SpaceKind.Shared)
            throw AppException.Validation("rotation", "Rotation is available in shared spaces.");
        var live = Live(await session.People.ListAsync(spaceId, ct));
        if (rotation.Members.Any(member => !live(member)))
            throw AppException.Validation("rotation", "Choose current owners or editors in this space.");
    }

    /// <summary>Makes <paramref name="host"/> the only live assignee of the task.</summary>
    public static async Task AssignOnlyAsync(IDataSession session, string taskId, string? host, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await session.Tasks.ListAssigneesAsync(taskId, ct);
        foreach (var row in rows.Where(row => row.DeletedAtUtc is null && row.MemberId != host))
            await session.Tasks.SaveAssigneeAsync(row with { DeletedAtUtc = now }, ct);
        if (host is null) return;
        var existing = rows.FirstOrDefault(row => row.MemberId == host);
        if (existing is { DeletedAtUtc: null }) return;
        await session.Tasks.SaveAssigneeAsync(new TaskAssigneeRecord(taskId, host, now, null, null), ct);
    }
}
