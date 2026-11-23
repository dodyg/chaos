using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Spaces;

/// <summary>A shared space the service accepted, and this member's role in it (1 owner, 2 editor, 3 viewer).</summary>
public sealed record SharedSpaceResult(string SpaceId, string Name, int Role);

/// <summary>A member as the service lists them (§8.3). Kind is 1 person, 2 agent; role 1 owner, 2 editor, 3 viewer. No emails.</summary>
public sealed record ServiceMember(string MemberId, string Name, int Role, int Kind, string JoinedAt, string? RemovedAt);

/// <summary>An invite as the service lists it (§8.5). The link is never part of it: it is shown once, at creation.</summary>
public sealed record ServiceInvite(string Id, string? Email, int Role, string CreatedAt, string? ExpiresAt, int? MaxUses, int Uses, string? RevokedAt);

/// <summary>A freshly created invite. <see cref="Link"/> is null for an email invite, whose token only the email carries.</summary>
public sealed record CreatedInvite(ServiceInvite Invite, string? Link);

/// <summary>What a move into a shared space did on the service.</summary>
public sealed record SpaceMoveResult(string SpaceId, string ContainerId, int EntityCount);

/// <summary>
/// Create, join, leave and move-in for shared spaces (PLAN3 Q4 step 4). Each command calls the service first, outside
/// any transaction, and only then records the result through <see cref="ISpaceStreams"/>. Every one of them needs a
/// connection: offline, it says so and changes nothing.
/// </summary>
public sealed class SpaceCommands(IApplicationStore store, IChaosServiceClient service, ISpaceStreams streams, IIdGenerator ids)
{
    public const int MaxNameLength = 100;

    public async Task<SharedSpaceResult> CreateAsync(string name, string? colorKey = null, string? icon = null, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw AppException.Validation("name", $"Give the space a name of 1 to {MaxNameLength} characters.");
        }

        var spaceId = ids.NewId();
        var body = JsonSerializer.Serialize(new { spaceId, name = trimmed, colorKey, icon });
        var space = ReadSpace(await CallAsync(HttpMethod.Post, "v2/spaces", body, "Connect to create a shared space.", ct));
        await streams.JoinAsync(Guid.Parse(space.SpaceId), space.Role, ct);
        await streams.SyncAsync(ct);
        return space;
    }

    /// <summary>Accepts an invite from a pasted link (<c>https://…/invite/&lt;token&gt;</c>) or a bare token.</summary>
    public async Task<SharedSpaceResult> JoinWithLinkAsync(string link, CancellationToken ct = default)
    {
        var token = TokenFrom(link);
        var space = ReadSpace(await CallAsync(HttpMethod.Post, "v2/invites/accept", JsonSerializer.Serialize(new { token }),
            "Connect to join a shared space.", ct));
        await streams.JoinAsync(Guid.Parse(space.SpaceId), space.Role, ct);
        await streams.SyncAsync(ct);
        return space;
    }

    public async Task LeaveAsync(string spaceId, CancellationToken ct = default)
    {
        var id = SpaceId(spaceId);
        await CallAsync(HttpMethod.Post, $"v2/spaces/{id:D}/leave", null, "Connect to leave a shared space.", ct);
        await streams.LeaveAsync(id, ct);
        await streams.SyncAsync(ct);
    }

    /// <summary>
    /// Moves a Personal list, with everything in it, into a shared space (§7.6). Unsent edits go up first so the service
    /// moves the list as this device has it; the <c>moved</c> changes and the space's copy then come back by sync.
    /// </summary>
    public async Task<SpaceMoveResult> MoveAsync(string containerId, string spaceId, CancellationToken ct = default)
    {
        var target = SpaceId(spaceId);
        if (!Guid.TryParseExact(containerId, "D", out var container))
        {
            throw AppException.Validation("containerId", "Choose a valid list.");
        }

        await store.QueryAsync(async (session, token) =>
        {
            var list = await session.Containers.GetAsync(container.ToString("D"), token)
                ?? throw new AppException(AppErrorKind.NotFound, "That list does not exist.");
            if (list.IsInbox)
            {
                throw AppException.Validation("containerId", "The Inbox stays personal.");
            }

            var from = await session.Spaces.GetAsync(list.SpaceId, token);
            if (from?.Kind != SpaceKind.Personal)
            {
                throw AppException.Validation("containerId", "Only a Personal list can be moved into a shared space.");
            }

            if (await session.Spaces.GetAsync(target.ToString("D"), token) is not { Kind: SpaceKind.Shared })
            {
                throw new AppException(AppErrorKind.NotFound, "That shared space is not on this computer.");
            }

            return 0;
        }, ct);

        if (await streams.SyncAsync(ct) is { } problem)
        {
            throw new AppException(AppErrorKind.OperationConflict, $"The list could not be moved because sync is not up to date. {problem}");
        }

        var body = JsonSerializer.Serialize(new { moveId = ids.NewId(), containerId = container });
        var response = await CallAsync(HttpMethod.Post, $"v2/spaces/{target:D}/moves", body, "Connect to move a list into a shared space.", ct);
        int count;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            count = document.RootElement.GetProperty("entityCount").GetInt32();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ChaosServiceException(ServiceFailure.Other, "The online service returned an invalid move result.", error);
        }

        await streams.SyncAsync(ct);
        return new SpaceMoveResult(target.ToString("D"), container.ToString("D"), count);
    }

    /// <summary>The members as the service has them, with roles. The local directory shows names; this is the manager's view.</summary>
    public async Task<IReadOnlyList<ServiceMember>> MembersAsync(string spaceId, CancellationToken ct = default)
    {
        var id = SpaceId(spaceId);
        var response = await CallAsync(HttpMethod.Get, $"v2/spaces/{id:D}/members", null, "Connect to see the members.", ct);
        return Read(response, "members", root => (IReadOnlyList<ServiceMember>)[.. root.EnumerateArray().Select(ReadMember)]);
    }

    public async Task ChangeRoleAsync(string spaceId, string memberId, int role, CancellationToken ct = default)
    {
        if (role is < 1 or > 3)
        {
            throw AppException.Validation("role", "Choose owner, editor or viewer.");
        }

        await CallAsync(HttpMethod.Patch, $"v2/spaces/{SpaceId(spaceId):D}/members/{EntityId(memberId):D}",
            JsonSerializer.Serialize(new { role }), "Connect to change a role.", ct);
        await streams.SyncAsync(ct);
    }

    public async Task RemoveMemberAsync(string spaceId, string memberId, CancellationToken ct = default)
    {
        await CallAsync(HttpMethod.Delete, $"v2/spaces/{SpaceId(spaceId):D}/members/{EntityId(memberId):D}", null, "Connect to remove a member.", ct);
        await streams.SyncAsync(ct);
    }

    /// <summary>Changes the name other members see for this person in this space only (§8.3).</summary>
    public async Task SetMyNameAsync(string spaceId, string displayName, CancellationToken ct = default)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length is 0 or > 80)
        {
            throw AppException.Validation("displayName", "Enter a name of 1 to 80 characters.");
        }

        await CallAsync(HttpMethod.Patch, $"v2/spaces/{SpaceId(spaceId):D}/members/me", JsonSerializer.Serialize(new { displayName = name }),
            "Connect to change your name in this space.", ct);
        await streams.SyncAsync(ct);
    }

    /// <summary>
    /// The person's GitHub login in this space (PLAN3 10.3): used to match GitHub assignees to them. It is not
    /// verified against GitHub, because the token belongs to the owner's device. Null or blank clears it.
    /// </summary>
    public async Task SetMyGitHubLoginAsync(string spaceId, string? login, CancellationToken ct = default)
    {
        var normalized = GitHub.GitHubLogin.Normalize(login);
        if (normalized is not null && !GitHub.GitHubLogin.IsValid(normalized))
        {
            throw AppException.Validation("gitHubLogin", "Enter a GitHub login: 1–39 letters, digits or hyphens.");
        }
        await CallAsync(HttpMethod.Patch, $"v2/spaces/{SpaceId(spaceId):D}/members/me", JsonSerializer.Serialize(new { gitHubLogin = normalized ?? "" }),
            "Connect to change your GitHub login in this space.", ct);
        await streams.SyncAsync(ct);
    }

    public async Task<IReadOnlyList<ServiceInvite>> InvitesAsync(string spaceId, CancellationToken ct = default)
    {
        var response = await CallAsync(HttpMethod.Get, $"v2/spaces/{SpaceId(spaceId):D}/invites", null, "Connect to see the invitations.", ct);
        return Read(response, "invites", root => (IReadOnlyList<ServiceInvite>)[.. root.EnumerateArray().Select(ReadInvite)]);
    }

    /// <summary>
    /// Creates an email invite (<paramref name="email"/> set) or a link invite. Expiry is in days (null: never); uses null is unlimited.
    /// </summary>
    public async Task<CreatedInvite> InviteAsync(string spaceId, string? email, int role, int? expiresInDays, int? maxUses, CancellationToken ct = default)
    {
        if (role is not (2 or 3))
        {
            throw AppException.Validation("role", "Invite people as an editor or a viewer.");
        }

        var address = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (address is not null && (address.Length > 254 || !address.Contains('@') || address.Any(char.IsWhiteSpace)))
        {
            throw AppException.Validation("email", "Enter a valid email address.");
        }

        if (expiresInDays is <= 0 or > 3650 || maxUses is <= 0 or > 1000)
        {
            throw AppException.Validation("expiresInDays", "Choose a valid expiry and number of uses.");
        }

        DateTimeOffset? expiresAt = expiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : null;
        var body = address is null
            ? JsonSerializer.Serialize(new { role, expiresAt, maxUses })
            : JsonSerializer.Serialize(new { email = address, role });
        var response = await CallAsync(HttpMethod.Post, $"v2/spaces/{SpaceId(spaceId):D}/invites", body, "Connect to invite people.", ct);
        return Read(response, "invite", root => new CreatedInvite(ReadInvite(root.GetProperty("invite")),
            root.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null));
    }

    public async Task RevokeInviteAsync(string spaceId, string inviteId, CancellationToken ct = default) =>
        await CallAsync(HttpMethod.Delete, $"v2/spaces/{SpaceId(spaceId):D}/invites/{EntityId(inviteId):D}", null, "Connect to turn an invitation off.", ct);

    /// <summary>Trashes a shared space for everyone; members' devices evict it and the owner may restore it for 30 days (§8.6). The name must be typed back.</summary>
    public async Task DeleteSpaceAsync(string spaceId, string typedName, CancellationToken ct = default)
    {
        var id = SpaceId(spaceId);
        var name = await store.QueryAsync(async (session, token) =>
            (await session.Spaces.GetAsync(id.ToString("D"), token))?.Name, ct)
            ?? throw new AppException(AppErrorKind.NotFound, "That shared space is not on this computer.");
        if (!string.Equals(name, (typedName ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            throw AppException.Validation("name", "Type the space's name exactly to delete it.");
        }

        await CallAsync(HttpMethod.Delete, $"v2/spaces/{id:D}", null, "Connect to delete a shared space.", ct);
        await streams.SyncAsync(ct);
    }

    public async Task RestoreSpaceAsync(string spaceId, CancellationToken ct = default)
    {
        await CallAsync(HttpMethod.Post, $"v2/spaces/{SpaceId(spaceId):D}/restore", null, "Connect to restore a shared space.", ct);
        await streams.SyncAsync(ct);
    }

    private static T Read<T>(ServiceResponse response, string what, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ChaosServiceException(ServiceFailure.Other, $"The online service returned an invalid {what} answer.", error);
        }
    }

    private static ServiceMember ReadMember(JsonElement e) => new(e.GetProperty("memberId").GetGuid().ToString("D"), e.GetProperty("name").GetString() ?? string.Empty,
        e.GetProperty("role").GetInt32(), e.GetProperty("kind").GetInt32(), e.GetProperty("joinedAt").GetString() ?? string.Empty,
        e.TryGetProperty("removedAt", out var removed) && removed.ValueKind == JsonValueKind.String ? removed.GetString() : null);

    private static ServiceInvite ReadInvite(JsonElement e)
    {
        static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(e.GetProperty("id").GetGuid().ToString("D"), Text(e, "email"), e.GetProperty("role").GetInt32(), Text(e, "createdAt") ?? string.Empty, Text(e, "expiresAt"),
            e.TryGetProperty("maxUses", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetInt32() : null, e.GetProperty("uses").GetInt32(), Text(e, "revokedAt"));
    }

    private static Guid EntityId(string id) =>
        Guid.TryParseExact(id, "D", out var parsed) ? parsed : throw AppException.Validation("id", "Choose a valid member or invitation.");

    private async Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? body, string offline, CancellationToken ct)
    {
        ServiceResponse response;
        try
        {
            response = await service.CallAsync(method, path, body, ct);
        }
        catch (ChaosServiceException error) when (error.Kind == ServiceFailure.Offline)
        {
            throw new AppException(AppErrorKind.OperationConflict, offline);
        }

        if (!response.Ok)
        {
            throw new AppException(response.Status == 404 ? AppErrorKind.NotFound : AppErrorKind.OperationConflict, ProblemText(response));
        }

        return response;
    }

    private static SharedSpaceResult ReadSpace(ServiceResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var role = root.GetProperty("role").GetInt32();
            if (role is < 1 or > 3)
            {
                throw new FormatException("Invalid role.");
            }

            return new SharedSpaceResult(root.GetProperty("id").GetGuid().ToString("D"), root.GetProperty("name").GetString() ?? string.Empty, role);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ChaosServiceException(ServiceFailure.Other, "The online service returned an invalid space.", error);
        }
    }

    internal static string TokenFrom(string link)
    {
        var text = (link ?? string.Empty).Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            text = segments.Length == 2 && segments[0] == "invite" ? Uri.UnescapeDataString(segments[1]) : string.Empty;
        }

        if (text.Length is 0 or > 200 || text.Any(char.IsWhiteSpace))
        {
            throw AppException.Validation("link", "Paste the whole invitation link.");
        }

        return text;
    }

    private static Guid SpaceId(string spaceId) =>
        Guid.TryParseExact(spaceId, "D", out var id) ? id : throw AppException.Validation("spaceId", "Choose a valid space.");

    private static string ProblemText(ServiceResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 and <= 200 } text)
            {
                return text;
            }
        }
        catch (JsonException error)
        {
            // Not a problem document: describe the status instead.
            System.Diagnostics.Trace.TraceInformation("Online service error response was not a problem document: {0}", error.GetType().Name);
        }

        return $"The online service answered with an error ({response.Status}).";
    }
}
