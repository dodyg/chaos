using System.Security.Claims;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Sync;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Sync;

public sealed record PauseAgentRequest(bool Paused);

/// <summary>
/// <c>/v2/agents…</c>, Members → Add agent, and the claim endpoints people use from the UI (PLAN3 12.3, 12.4). An agent
/// itself never calls these: it reaches the service through its tools (Q19).
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this RouteGroupBuilder v2)
    {
        v2.MapPost("/agents", async (ClaimsPrincipal user, CreateAgentRequest request, AgentService agents, CancellationToken ct) =>
            Http(await agents.CreateAsync(Account(user), Device(user), request, ct)));
        v2.MapGet("/agents", async (ClaimsPrincipal user, AgentService agents, CancellationToken ct) =>
            Http(await agents.ListAsync(Account(user), ct)));
        v2.MapPatch("/agents/{agentId:guid}", async (ClaimsPrincipal user, Guid agentId, UpdateAgentRequest request, AgentService agents, CancellationToken ct) =>
            Http(await agents.UpdateAsync(Account(user), Device(user), agentId, request, ct)));
        v2.MapDelete("/agents/{agentId:guid}", async (ClaimsPrincipal user, Guid agentId, AgentService agents, CancellationToken ct) =>
            Http(await agents.RemoveAsync(Account(user), Device(user), agentId, ct)));
        v2.MapPost("/agents/{agentId:guid}/revert", async (ClaimsPrincipal user, Guid agentId, RevertAgentRequest request, AgentService agents, CancellationToken ct) =>
            Http(await agents.RevertAsync(Account(user), Device(user), agentId, request, ct)));
        v2.MapGet("/spaces/{spaceId:guid}/agents", async (ClaimsPrincipal user, Guid spaceId, AgentService agents, CancellationToken ct) =>
            Http(await agents.SpaceAgentsAsync(Account(user), spaceId, ct)));
        v2.MapPost("/spaces/{spaceId:guid}/agents", async (ClaimsPrincipal user, Guid spaceId, AddAgentRequest request, AgentService agents, CancellationToken ct) =>
            Http(await agents.AddToSpaceAsync(Account(user), Device(user), spaceId, request, ct)));
        v2.MapPost("/spaces/{spaceId:guid}/agents/{agentId:guid}/pause", async (ClaimsPrincipal user, Guid spaceId, Guid agentId, PauseAgentRequest request,
            AgentService agents, CancellationToken ct) =>
            Http(await agents.SetPausedAsync(Account(user), Device(user), agentId, request.Paused, spaceId, ct)));
        v2.MapPost("/tasks/{taskId:guid}/claim", (ClaimsPrincipal user, Guid taskId, AgentService agents, CancellationToken ct) =>
            Claim(user, taskId, ClaimAction.Claim, agents, ct));
        v2.MapPost("/tasks/{taskId:guid}/release", (ClaimsPrincipal user, Guid taskId, AgentService agents, CancellationToken ct) =>
            Claim(user, taskId, ClaimAction.Release, agents, ct));
        v2.MapPost("/tasks/{taskId:guid}/takeover", (ClaimsPrincipal user, Guid taskId, AgentService agents, CancellationToken ct) =>
            Claim(user, taskId, ClaimAction.TakeOver, agents, ct));
        v2.MapPost("/tasks/{taskId:guid}/heartbeat", (ClaimsPrincipal user, Guid taskId, AgentService agents, CancellationToken ct) =>
            Claim(user, taskId, ClaimAction.Heartbeat, agents, ct));
    }

    private static async Task<IResult> Claim(ClaimsPrincipal user, Guid taskId, ClaimAction action, AgentService agents, CancellationToken ct) =>
        Http(await agents.PersonClaimAsync(SpaceEndpoints.Account(user), taskId.ToString("D"), action, ct));

    private static IResult Http<T>(SpaceOperation<T> operation) => operation.Succeeded
        ? Results.Ok(operation.Value)
        : ApiErrors.Problem(operation.Error!, operation.Detail ?? "The request was refused.", StatusFor(operation.Error!));

    private static Guid Account(ClaimsPrincipal user) => Guid.Parse(user.FindFirst(Claims.Subject)!.Value);
    private static Guid Device(ClaimsPrincipal user) => Guid.Parse(user.FindFirst(DesktopClient.DeviceIdClaim)!.Value);

    private static int StatusFor(string code) => code switch
    {
        SyncRejection.NotMember or SyncRejection.Forbidden or AgentToolStatus.Forbidden or AgentToolStatus.Paused or "agents-disabled"
            or AgentToolStatus.NotClaimant => StatusCodes.Status403Forbidden,
        SyncRejection.RateLimited or AgentToolStatus.RateLimited => StatusCodes.Status429TooManyRequests,
        AgentToolStatus.AlreadyClaimed or SyncRejection.Invalid or "agent-limit" or "agent-already-member" => StatusCodes.Status409Conflict,
        "agent-not-found" or "container-not-found" or AgentToolStatus.NotFound or "task-not-found"
            or "space-unavailable" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest,
    };
}
