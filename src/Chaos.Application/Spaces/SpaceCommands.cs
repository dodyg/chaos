using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Spaces;

/// <summary>A shared space the service accepted, and this member's role in it (1 owner, 2 editor, 3 viewer).</summary>
public sealed record SharedSpaceResult(string SpaceId, string Name, int Role);

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
