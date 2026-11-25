using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Application.Spaces;

/// <summary>A server agent as the service lists it (PLAN3 12.3): owned by the account, with the spaces it is a member of.</summary>
public sealed record ServiceAgent(string Id, string Name, string CreatedAtUtc, string? PausedAtUtc, IReadOnlyList<ServiceAgentMembership> Memberships);
/// <summary>Null <paramref name="AllowedContainerIds"/> means every list of the space.</summary>
public sealed record ServiceAgentMembership(string SpaceId, string JoinedAtUtc, IReadOnlyList<string>? AllowedContainerIds);
/// <summary>An agent the caller could add to a space, and whether it is in it.</summary>
public sealed record ServiceSpaceAgent(ServiceAgent Agent, string OwnerName, bool Member);
/// <summary>A shared task's claim as the service answers (12.4).</summary>
public sealed record ServiceClaim(string TaskId, string StreamId, string? ClaimedById, string? ClaimedAtUtc, string? ClaimExpiresAtUtc, string Status);
public sealed record ServiceRevert(int RestoredGroups, int Trashed, IReadOnlyList<string> Skipped);

/// <summary>
/// Server agents through <c>/v2/agents</c> and Members → Add agent (PLAN3 12.3). Each call needs the connection; a
/// refusal comes back as the service's own words.
/// </summary>
public sealed class ServiceAgentCommands(IChaosServiceClient service)
{
    public async Task<IReadOnlyList<ServiceAgent>> ListAsync(CancellationToken ct) =>
        ServiceCalls.ReadList(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Get, "v2/agents", null, ct), "list your agents"), "list your agents", ReadAgent);

    public async Task<ServiceAgent> CreateAsync(string name, CancellationToken ct) =>
        ReadAgent(ServiceCalls.Read(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Post, "v2/agents", JsonSerializer.Serialize(new { name }), ct),
            "add an agent"), "add an agent", root => root));

    public async Task<ServiceAgent> RenameAsync(string agentId, string name, CancellationToken ct) =>
        ReadAgent(ServiceCalls.Read(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Patch, $"v2/agents/{ServiceCalls.Id(agentId):D}",
            JsonSerializer.Serialize(new { name }), ct), "rename an agent"), "rename an agent", root => root));

    public async Task<ServiceAgent> SetPausedAsync(string agentId, bool paused, CancellationToken ct) =>
        ReadAgent(ServiceCalls.Read(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Patch, $"v2/agents/{ServiceCalls.Id(agentId):D}",
            JsonSerializer.Serialize(new { paused }), ct), "pause or resume an agent"), "pause or resume an agent", root => root));

    public async Task RemoveAsync(string agentId, CancellationToken ct) =>
        ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Delete, $"v2/agents/{ServiceCalls.Id(agentId):D}", null, ct), "remove an agent");

    public async Task<ServiceRevert> RevertAsync(string agentId, string spaceId, int hours, CancellationToken ct) =>
        ServiceCalls.Read(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Post, $"v2/agents/{ServiceCalls.Id(agentId):D}/revert",
            JsonSerializer.Serialize(new { spaceId = ServiceCalls.Id(spaceId), hours }), ct), "revert an agent"), "revert an agent",
            root => new ServiceRevert(root.GetProperty("restoredGroups").GetInt32(), root.GetProperty("trashed").GetInt32(),
                [.. root.GetProperty("skipped").EnumerateArray().Select(item => item.GetString() ?? string.Empty)]));

    /// <summary>The agents the caller could add to the space (their own, and those of live members) and which are in it.</summary>
    public async Task<IReadOnlyList<ServiceSpaceAgent>> SpaceAgentsAsync(string spaceId, CancellationToken ct) =>
        ServiceCalls.ReadList(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Get, $"v2/spaces/{ServiceCalls.Id(spaceId):D}/agents", null, ct),
            "see the agents of this space"), "see the agents of this space",
            root => new ServiceSpaceAgent(ReadAgent(root.GetProperty("agent")), root.GetProperty("ownerName").GetString() ?? "A member", root.GetProperty("member").GetBoolean()));

    public async Task<ServiceMember> AddToSpaceAsync(string spaceId, string agentId, IReadOnlyList<string>? allowedContainerIds, CancellationToken ct) =>
        ServiceCalls.ReadMember(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Post, $"v2/spaces/{ServiceCalls.Id(spaceId):D}/agents",
            JsonSerializer.Serialize(new { agentId = ServiceCalls.Id(agentId), allowedContainerIds = allowedContainerIds?.Select(id => ServiceCalls.Id(id).ToString("D")).ToArray() }), ct),
            "add an agent to this space"));

    public async Task<ServiceAgent> PauseInSpaceAsync(string spaceId, string agentId, bool paused, CancellationToken ct) =>
        ReadAgent(ServiceCalls.Read(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Post,
            $"v2/spaces/{ServiceCalls.Id(spaceId):D}/agents/{ServiceCalls.Id(agentId):D}/pause", JsonSerializer.Serialize(new { paused }), ct),
            "pause or resume an agent"), "pause or resume an agent", root => root));

    public async Task RemoveFromSpaceAsync(string spaceId, string agentId, CancellationToken ct) =>
        ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Delete, $"v2/spaces/{ServiceCalls.Id(spaceId):D}/members/{ServiceCalls.Id(agentId):D}", null, ct),
            "remove an agent from this space");

    private static ServiceAgent ReadAgent(JsonElement e) => new(e.GetProperty("id").GetGuid().ToString("D"), e.GetProperty("name").GetString() ?? string.Empty,
        e.GetProperty("createdAt").GetString() ?? string.Empty, ServiceCalls.Text(e, "pausedAt"),
        [.. e.GetProperty("memberships").EnumerateArray().Select(m => new ServiceAgentMembership(m.GetProperty("spaceId").GetGuid().ToString("D"),
            m.GetProperty("joinedAt").GetString() ?? string.Empty,
            m.TryGetProperty("allowedContainerIds", out var lists) && lists.ValueKind == JsonValueKind.Array
                ? [.. lists.EnumerateArray().Select(id => id.GetGuid().ToString("D"))] : null))]);
}

/// <summary>Claims on shared tasks (PLAN3 12.4): the service is the compare-and-set there; the change arrives with sync.</summary>
public sealed class SharedClaimCommands(IChaosServiceClient service)
{
    public Task<ServiceClaim> ClaimAsync(string taskId, CancellationToken ct) => CallAsync(taskId, "claim", ct);
    public Task<ServiceClaim> ReleaseAsync(string taskId, CancellationToken ct) => CallAsync(taskId, "release", ct);
    public Task<ServiceClaim> TakeOverAsync(string taskId, CancellationToken ct) => CallAsync(taskId, "takeover", ct);
    public Task<ServiceClaim> HeartbeatAsync(string taskId, CancellationToken ct) => CallAsync(taskId, "heartbeat", ct);

    private async Task<ServiceClaim> CallAsync(string taskId, string action, CancellationToken ct) =>
        ServiceCalls.Read(ServiceCalls.Require(await ServiceCalls.SendAsync(service, HttpMethod.Post, $"v2/tasks/{ServiceCalls.Id(taskId):D}/{action}", null, ct), $"{action} the task"),
            $"{action} the task", root => new ServiceClaim(root.GetProperty("taskId").GetGuid().ToString("D"), root.GetProperty("streamId").GetGuid().ToString("D"),
                ServiceCalls.Text(root, "claimedById"), ServiceCalls.Text(root, "claimedAtUtc"), ServiceCalls.Text(root, "claimExpiresAtUtc"),
                root.GetProperty("status").GetString() ?? string.Empty));
}

/// <summary>Reading the service's answers and turning a refusal into an <see cref="AppException"/> with its own words.</summary>
internal static class ServiceCalls
{
    /// <summary>
    /// One call to the service, with its transport failures (offline, no account, signed out) turned into an
    /// <see cref="AppException"/> in plain words, as <c>SpaceCommands</c> does: the RPC layer answers those as a refusal,
    /// so a screen that also shows local data (Settings → Agents) keeps working without the service.
    /// </summary>
    public static async Task<ServiceResponse> SendAsync(IChaosServiceClient service, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        try
        {
            return await service.CallAsync(method, path, body, ct);
        }
        catch (ChaosServiceException error)
        {
            throw new AppException(AppErrorKind.OperationConflict, error.Kind switch
            {
                ServiceFailure.Offline => "Chaos cannot reach the online service. Check your connection and try again.",
                ServiceFailure.SignInAgain => "Sign in again in Settings → Account to use server agents and shared tasks.",
                ServiceFailure.NotConfigured => "This copy of Chaos has no online service, so server agents and shared tasks are not available.",
                _ => error.Message,
            });
        }
    }

    public static Guid Id(string id) =>
        Guid.TryParseExact(id, "D", out var parsed) ? parsed : throw AppException.Validation("id", "Choose a valid ID.");

    public static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static ServiceResponse Require(ServiceResponse response, string what)
    {
        if (response.Status is >= 200 and < 300)
        {
            return response;
        }

        var kind = response.Status == 404 ? AppErrorKind.NotFound : AppErrorKind.OperationConflict;
        throw new AppException(kind, ProblemText(response) ?? $"The online service did not let you {what}.");
    }

    public static T Read<T>(ServiceResponse response, string what, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AppException(AppErrorKind.OperationConflict, $"The online service returned an invalid answer when asked to {what}.");
        }
    }

    public static IReadOnlyList<T> ReadList<T>(ServiceResponse response, string what, Func<JsonElement, T> read) =>
        Read(response, what, root => (IReadOnlyList<T>)[.. root.EnumerateArray().Select(read)]);

    public static ServiceMember ReadMember(ServiceResponse response) => Read(response, "read a member", e => new ServiceMember(
        e.GetProperty("memberId").GetGuid().ToString("D"), e.GetProperty("name").GetString() ?? string.Empty, e.GetProperty("role").GetInt32(),
        e.GetProperty("kind").GetInt32(), e.GetProperty("joinedAt").GetString() ?? string.Empty, Text(e, "removedAt")));

    public static string? ProblemText(ServiceResponse response)
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
            // Not a problem document: the status says enough.
            System.Diagnostics.Trace.TraceInformation("Online service error response was not a problem document: {0}", error.GetType().Name);
        }

        return null;
    }
}
