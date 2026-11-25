using Chaos.Application.Abstractions;
using Chaos.Application.Agents;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Spaces;
using Chaos.Infrastructure.Sync;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record LocalAgentDto(string Id, string Name, string? PausedAtUtc, string Version);
public sealed record LocalAgentAccessDto(string ContainerId, string ContainerName, bool Allowed);
public sealed record LocalAgentTokenDto(string Id, string Name, string CreatedAtUtc, string? LastUsedAtUtc);
public sealed record AgentViewDto(LocalAgentDto Agent, IReadOnlyList<LocalAgentAccessDto> Lists, IReadOnlyList<LocalAgentTokenDto> Tokens);
public sealed record AgentListResponse(bool Ok, IReadOnlyList<AgentViewDto> Agents, TaskErrorResponse? Error);
public sealed record AgentCreateRequest(string OperationId, string Name);
public sealed record AgentRenameRequest(string OperationId, string AgentId, string ExpectedVersion, string Name);
public sealed record AgentSetPausedRequest(string OperationId, string AgentId, string ExpectedVersion, bool Paused);
public sealed record AgentRemoveRequest(string OperationId, string AgentId, string ExpectedVersion);
public sealed record AgentMutationResponse(bool Ok, LocalAgentDto? Agent, TaskErrorResponse? Error);
public sealed record AgentTokenRequest(string OperationId, string AgentId, string ExpectedVersion, string Name);
/// <summary><paramref name="Secret"/> is the token to copy into the MCP client; it is shown this once only.</summary>
public sealed record AgentTokenResponse(bool Ok, LocalAgentTokenDto? Token, string? Secret, TaskErrorResponse? Error);
public sealed record AgentRevokeTokenRequest(string OperationId, string AgentId, string TokenId);
public sealed record AgentSetAccessRequest(string OperationId, string AgentId, string ContainerId, bool Allowed);
public sealed record AgentUndoRequest(string ActorId, int? MaxOperations);
public sealed record AgentUndoResponse(bool Ok, int Undone, string? StoppedAt, string? Conflict, TaskErrorResponse? Error);
public sealed record ServerAgentMembershipDto(string SpaceId, IReadOnlyList<string>? AllowedContainerIds);
public sealed record ServerAgentDto(string Id, string Name, string? PausedAtUtc, IReadOnlyList<ServerAgentMembershipDto> Memberships);
public sealed record ServerAgentListResponse(bool Ok, IReadOnlyList<ServerAgentDto> Agents, TaskErrorResponse? Error);
/// <summary>A server agent change: rename (<paramref name="Name"/>), pause or resume (<paramref name="Paused"/>), or remove (neither).</summary>
public sealed record ServerAgentRequest(string AgentId, string? Name, bool? Paused);
public sealed record ServerAgentResponse(bool Ok, ServerAgentDto? Agent, TaskErrorResponse? Error);
public sealed record SpaceAgentDto(ServerAgentDto Agent, string OwnerName, bool Member);
public sealed record SpaceAgentsResponse(bool Ok, IReadOnlyList<SpaceAgentDto> Agents, TaskErrorResponse? Error);
public sealed record SpaceAgentsRequest(string SpaceId);
public sealed record AddAgentRequest(string SpaceId, string AgentId, IReadOnlyList<string>? AllowedContainerIds);
public sealed record PauseAgentInSpaceRequest(string SpaceId, string AgentId, bool Paused);
public sealed record RemoveAgentFromSpaceRequest(string SpaceId, string AgentId);
public sealed record SpaceAgentChangeResponse(bool Ok, SpaceAgentDto? Agent, TaskErrorResponse? Error);
public sealed record RevertAgentRequest(string AgentId, string SpaceId, int Hours);
public sealed record RevertAgentResponse(bool Ok, int RestoredGroups, int Trashed, IReadOnlyList<string> Skipped, TaskErrorResponse? Error);

/// <summary>
/// Settings → Agents (PLAN3 12.3): the person's local agents, their tokens (shown once) and the Personal lists each may
/// work in; undo of an agent's recent changes (12.7); and the account's server agents, which an owner adds to a shared
/// space. Every write of a local agent is a recorded operation of the local person.
/// </summary>
[NeoRpcService("agents", Version = 1)]
public sealed class AgentRpcService(IApplicationStore store, AgentCommands agents, UndoCommands undo, ServiceAgentCommands server, SyncEngine? sync)
{
    [NeoRpcMethod("list")]
    public ValueTask<AgentListResponse> ListAsync(AgentListRequest request, CancellationToken ct) =>
        Rpc.Run<AgentListResponse>("agents.list", async () =>
        {
            var views = await agents.ListAsync(ct);
            var lists = new List<AgentViewDto>();
            foreach (var view in views)
            {
                var granted = view.Access.Select(access => access.ContainerId).ToHashSet(StringComparer.Ordinal);
                var personal = await PersonalListsAsync(granted, ct);
                lists.Add(new AgentViewDto(Map(view.Agent), personal, [.. view.Tokens.Select(Map)]));
            }

            return new(true, lists, null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("create")]
    public ValueTask<AgentMutationResponse> CreateAsync(AgentCreateRequest request, CancellationToken ct) =>
        Rpc.Run<AgentMutationResponse>("agents.create", async () => new(true, Map(await agents.CreateAsync(new(request.OperationId, request.Name), ct)), null),
            error => new(false, null, error));

    [NeoRpcMethod("rename")]
    public ValueTask<AgentMutationResponse> RenameAsync(AgentRenameRequest request, CancellationToken ct) =>
        Rpc.Run<AgentMutationResponse>("agents.rename", async () =>
        {
            var renamed = await agents.RenameAsync(new RenameAgentCommand(request.OperationId, request.AgentId, Number(request), request.Name), ct);
            return new AgentMutationResponse(true, Map(renamed), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("setPaused")]
    public ValueTask<AgentMutationResponse> SetPausedAsync(AgentSetPausedRequest request, CancellationToken ct) =>
        Rpc.Run<AgentMutationResponse>("agents.setPaused", async () =>
        {
            var changed = await agents.SetPausedAsync(new SetAgentPausedCommand(request.OperationId, request.AgentId, Number(request), request.Paused), ct);
            return new AgentMutationResponse(true, Map(changed), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("remove")]
    public ValueTask<AgentMutationResponse> RemoveAsync(AgentRemoveRequest request, CancellationToken ct) =>
        Rpc.Run<AgentMutationResponse>("agents.remove", async () =>
        {
            var removed = await agents.RemoveAsync(new RemoveAgentCommand(request.OperationId, request.AgentId, Number(request)), ct);
            return new AgentMutationResponse(true, Map(removed), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("createToken")]
    public ValueTask<AgentTokenResponse> CreateTokenAsync(AgentTokenRequest request, CancellationToken ct) =>
        Rpc.Run<AgentTokenResponse>("agents.createToken", async () =>
        {
            var created = await agents.CreateTokenAsync(new(request.OperationId, request.AgentId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"),
                request.Name), ct);
            return new(true, Map(created.Token), created.Secret, null);
        },
            error => new(false, null, null, error));

    [NeoRpcMethod("revokeToken")]
    public ValueTask<AgentMutationResponse> RevokeTokenAsync(AgentRevokeTokenRequest request, CancellationToken ct) =>
        Rpc.Run<AgentMutationResponse>("agents.revokeToken", async () =>
        {
            await agents.RevokeTokenAsync(new(request.OperationId, request.AgentId, request.TokenId), ct);
            var agent = (await agents.ListAsync(ct)).FirstOrDefault(view => view.Agent.Id == request.AgentId)?.Agent;
            return new(true, agent is null ? null : Map(agent), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("setAccess")]
    public ValueTask<AgentMutationResponse> SetAccessAsync(AgentSetAccessRequest request, CancellationToken ct) =>
        Rpc.Run<AgentMutationResponse>("agents.setAccess", async () =>
        {
            await agents.SetAccessAsync(new(request.OperationId, request.AgentId, request.ContainerId, request.Allowed), ct);
            var agent = (await agents.ListAsync(ct)).FirstOrDefault(view => view.Agent.Id == request.AgentId)?.Agent;
            return new(true, agent is null ? null : Map(agent), null);
        },
            error => new(false, null, error));

    /// <summary>Undo recent changes by a local agent (12.7), newest first, stopping at the first conflict.</summary>
    [NeoRpcMethod("undo")]
    public ValueTask<AgentUndoResponse> UndoAsync(AgentUndoRequest request, CancellationToken ct) =>
        Rpc.Run<AgentUndoResponse>("agents.undo", async () =>
        {
            var result = await undo.UndoAgentAsync(new(request.ActorId, request.MaxOperations), ct);
            return new(true, result.Undone, result.StoppedAt, result.Conflict, null);
        },
            error => new(false, 0, null, null, error));

    // ---- Server agents (12.3): the account's, added to shared spaces by an owner -------------------------------

    [NeoRpcMethod("serverList")]
    public ValueTask<ServerAgentListResponse> ServerListAsync(AgentListRequest request, CancellationToken ct) =>
        Rpc.Run<ServerAgentListResponse>("agents.serverList", async () => new(true, [.. (await server.ListAsync(ct)).Select(Map)], null),
            error => new(false, [], error));

    [NeoRpcMethod("serverCreate")]
    public ValueTask<ServerAgentResponse> ServerCreateAsync(ServerAgentRequest request, CancellationToken ct) =>
        Rpc.Run<ServerAgentResponse>("agents.serverCreate", async () => new(true, Map(await server.CreateAsync(request.Name ?? string.Empty, ct)), null),
            error => new(false, null, error));

    [NeoRpcMethod("serverUpdate")]
    public ValueTask<ServerAgentResponse> ServerUpdateAsync(ServerAgentRequest request, CancellationToken ct) =>
        Rpc.Run<ServerAgentResponse>("agents.serverUpdate", async () => new(true, Map(request switch
            {
                { Name: { } name } => await server.RenameAsync(request.AgentId, name, ct),
                { Paused: { } paused } => await server.SetPausedAsync(request.AgentId, paused, ct),
                // Neither a name nor a pause state: refuse, rather than read it as "resume".
                _ => throw AppException.Validation("paused", "Say what to change: a new name, or pause or resume."),
            }), null),
            error => new(false, null, error));

    [NeoRpcMethod("serverRemove")]
    public ValueTask<ServerAgentListResponse> ServerRemoveAsync(ServerAgentRequest request, CancellationToken ct) =>
        Rpc.Run<ServerAgentListResponse>("agents.serverRemove", async () =>
        {
            await server.RemoveAsync(request.AgentId, ct);
            sync?.Wake();
            return new(true, [.. (await server.ListAsync(ct)).Select(Map)], null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("spaceAgents")]
    public ValueTask<SpaceAgentsResponse> SpaceAgentsAsync(SpaceAgentsRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceAgentsResponse>("agents.spaceAgents", async () => new(true, [.. (await server.SpaceAgentsAsync(request.SpaceId, ct)).Select(Map)], null),
            error => new(false, [], error));

    [NeoRpcMethod("addToSpace")]
    public ValueTask<SpaceAgentsResponse> AddToSpaceAsync(AddAgentRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceAgentsResponse>("agents.addToSpace", async () =>
        {
            await server.AddToSpaceAsync(request.SpaceId, request.AgentId, request.AllowedContainerIds, ct);
            sync?.Wake();
            return new(true, [.. (await server.SpaceAgentsAsync(request.SpaceId, ct)).Select(Map)], null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("pauseInSpace")]
    public ValueTask<SpaceAgentChangeResponse> PauseInSpaceAsync(PauseAgentInSpaceRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceAgentChangeResponse>("agents.pauseInSpace", async () =>
        {
            var agent = await server.PauseInSpaceAsync(request.SpaceId, request.AgentId, request.Paused, ct);
            sync?.Wake();
            return new(true, Map(new ServiceSpaceAgent(agent, string.Empty, true)), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("removeFromSpace")]
    public ValueTask<SpaceAgentsResponse> RemoveFromSpaceAsync(RemoveAgentFromSpaceRequest request, CancellationToken ct) =>
        Rpc.Run<SpaceAgentsResponse>("agents.removeFromSpace", async () =>
        {
            await server.RemoveFromSpaceAsync(request.SpaceId, request.AgentId, ct);
            sync?.Wake();
            return new(true, [.. (await server.SpaceAgentsAsync(request.SpaceId, ct)).Select(Map)], null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("revert")]
    public ValueTask<RevertAgentResponse> RevertAsync(RevertAgentRequest request, CancellationToken ct) =>
        Rpc.Run<RevertAgentResponse>("agents.revert", async () =>
        {
            var result = await server.RevertAsync(request.AgentId, request.SpaceId, request.Hours, ct);
            sync?.Wake();
            return new(true, result.RestoredGroups, result.Trashed, result.Skipped, null);
        },
            error => new(false, 0, 0, [], error));

    private static long Number(AgentRenameRequest request) => ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion");
    private static long Number(AgentSetPausedRequest request) => ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion");
    private static long Number(AgentRemoveRequest request) => ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion");

    /// <summary>Every live list of the Personal space, marked with what the agent may use.</summary>
    private async Task<IReadOnlyList<LocalAgentAccessDto>> PersonalListsAsync(IReadOnlySet<string> granted, CancellationToken ct) =>
        await store.QueryAsync<IReadOnlyList<LocalAgentAccessDto>>(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var personal = profile is null ? null : await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            if (personal is null)
            {
                return [];
            }

            var lists = new List<LocalAgentAccessDto>();
            foreach (var container in await session.Containers.ListBySpaceAsync(personal.Id, token))
            {
                lists.Add(new LocalAgentAccessDto(container.Id, container.Name, granted.Contains(container.Id)));
            }

            return lists;
        }, ct);

    private static LocalAgentDto Map(LocalAgentRecord agent) => new(agent.Id, agent.Name,
        agent.PausedAtUtc is { } at ? StorageFormats.Instant(at) : null, agent.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static LocalAgentTokenDto Map(LocalAgentTokenRecord token) => new(token.Id, token.Name, StorageFormats.Instant(token.CreatedAtUtc),
        token.LastUsedAtUtc is { } used ? StorageFormats.Instant(used) : null);

    private static ServerAgentDto Map(ServiceAgent agent) => new(agent.Id, agent.Name, agent.PausedAtUtc,
        [.. agent.Memberships.Select(m => new ServerAgentMembershipDto(m.SpaceId, m.AllowedContainerIds))]);

    private static SpaceAgentDto Map(ServiceSpaceAgent agent) => new(Map(agent.Agent), agent.OwnerName, agent.Member);
}

public sealed record AgentListRequest();
