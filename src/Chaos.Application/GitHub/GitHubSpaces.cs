using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.People;
using Chaos.Domain.Tasks;

namespace Chaos.Application.GitHub;

/// <summary>
/// Which space a GitHub command or pass works on, and who may do what there (PLAN3 §10.1). A null space id is
/// the person's Personal space. Only owners manage a shared space's connection, and only an owner's device runs
/// the engine; members need no setup of their own.
/// </summary>
public static class GitHubSpaces
{
    /// <summary>The message a non-owner gets for any management action (the service refuses the write as well).</summary>
    public static string OwnerOnly(SpaceRecord space) => $"Only owners of {space.Name} manage its GitHub connection.";

    public static string OwnerOnly(string spaceName) => $"Only owners of {spaceName} manage its GitHub connection.";

    public static async Task<(ProfileRecord Profile, SpaceRecord Space)> ResolveAsync(IDataSession s, string? spaceId, CancellationToken ct)
    {
        var profile = await s.Profiles.GetPrimaryAsync(ct) ?? throw AppException.NotFound("Local profile is unavailable.");
        if (string.IsNullOrEmpty(spaceId))
        {
            return (profile, await s.Spaces.GetPersonalByOwnerAsync(profile.Id, ct) ?? throw AppException.NotFound("Personal space is unavailable."));
        }
        if (!Guid.TryParseExact(spaceId, "D", out _))
        {
            throw AppException.Validation("spaceId", "Choose a valid space.");
        }
        var space = await s.Spaces.GetAsync(spaceId, ct) ?? throw AppException.NotFound("That space is unavailable.");
        if (space.Kind == SpaceKind.Shared && !await IsMemberAsync(s, space, profile, ct))
        {
            throw AppException.NotFound("That space is unavailable.");
        }
        return (profile, space);
    }

    /// <summary>The space a connection or repository row belongs to, as this device holds it.</summary>
    public static async Task<SpaceRecord> SpaceOfAsync(IDataSession s, GitHubConnectionRecord connection, CancellationToken ct) =>
        await s.Spaces.GetAsync(connection.SpaceId, ct) ?? throw AppException.VersionConflict("The GitHub subscription is no longer active.");

    public static bool IsOwner(SpaceRecord space, ProfileRecord profile) => space.Kind == SpaceKind.Personal && space.OwnerProfileId == profile.Id;

    public static async Task<bool> IsOwnerAsync(IDataSession s, SpaceRecord space, ProfileRecord profile, CancellationToken ct) =>
        IsOwner(space, profile)
        || space.Kind == SpaceKind.Shared && await MemberAsync(s, space.Id, profile, ct) is { Role: 1 };

    public static async Task<bool> IsMemberAsync(IDataSession s, SpaceRecord space, ProfileRecord profile, CancellationToken ct) =>
        IsOwner(space, profile) || space.Kind == SpaceKind.Shared && await MemberAsync(s, space.Id, profile, ct) is not null;

    /// <summary>
    /// The device may run the engine for a connection when the connection is live and the local person owns its
    /// space: the Personal owner, or a current owner member (PLAN3 10.1).
    /// </summary>
    public static async Task<bool> CanRunAsync(IDataSession s, GitHubConnectionRecord? connection, ProfileRecord profile, CancellationToken ct) =>
        connection is { DeletedAtUtc: null }
        && await s.Spaces.GetAsync(connection.SpaceId, ct) is { } space
        && await IsOwnerAsync(s, space, profile, ct);

    public static async Task RequireOwnerAsync(IDataSession s, SpaceRecord space, ProfileRecord profile, CancellationToken ct)
    {
        if (!await IsOwnerAsync(s, space, profile, ct))
        {
            throw AppException.Validation("spaceId", OwnerOnly(space));
        }
    }

    private static async Task<SpacePerson?> MemberAsync(IDataSession s, string spaceId, ProfileRecord profile, CancellationToken ct)
    {
        var me = $"person:{profile.Id}";
        var member = (await s.People.ListAsync(spaceId, ct)).FirstOrDefault(person => string.Equals(person.ActorId, me, StringComparison.OrdinalIgnoreCase));
        return member is { Former: false } ? member : null;
    }
}
