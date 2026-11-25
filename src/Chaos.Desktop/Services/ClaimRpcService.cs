using Chaos.Application.Abstractions;
using Chaos.Application.Agents;
using Chaos.Application.Contracts;
using Chaos.Application.Spaces;
using Chaos.Infrastructure.Sync;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record ClaimRequest(string OperationId, string TaskId, string ExpectedVersion);
public sealed record SetReadyRequest(string OperationId, string TaskId, string ExpectedVersion, bool Ready, string? AgentId);
/// <summary>A claim change. A Personal task comes back as it now stands; a shared one is claimed by the service, which answers with a status name, and the task arrives with sync.</summary>
public sealed record ClaimResponse(bool Ok, TaskResponse? Task, string? ServiceStatus, bool Replayed, TaskErrorResponse? Error);

/// <summary>
/// Claim, release, take over and heartbeat (PLAN3 12.4), and Ready for agent (12.8). A Personal task is changed here, under
/// the device's serialized writer; a shared task goes to the service, the compare-and-set there, and the device wakes sync so
/// the change shows at once.
/// </summary>
[NeoRpcService("claims", Version = 1)]
public sealed class ClaimRpcService(IApplicationStore store, ClaimCommands claims, SharedClaimCommands shared, SyncEngine? sync)
{
    [NeoRpcMethod("claim")]
    public ValueTask<ClaimResponse> ClaimAsync(ClaimRequest request, CancellationToken ct) =>
        Rpc.Run<ClaimResponse>("claims.claim", () => Change("claims.claim", request, (command, token) => claims.ClaimAsync(command, token), ct), Failed);

    [NeoRpcMethod("release")]
    public ValueTask<ClaimResponse> ReleaseAsync(ClaimRequest request, CancellationToken ct) =>
        Rpc.Run<ClaimResponse>("claims.release", () => Change("claims.release", request, (command, token) => claims.ReleaseAsync(command, token), ct), Failed);

    [NeoRpcMethod("takeover")]
    public ValueTask<ClaimResponse> TakeOverAsync(ClaimRequest request, CancellationToken ct) =>
        Rpc.Run<ClaimResponse>("claims.takeover", () => Change("claims.takeover", request, (command, token) => claims.TakeOverAsync(command, token), ct), Failed);

    [NeoRpcMethod("heartbeat")]
    public ValueTask<ClaimResponse> HeartbeatAsync(ClaimRequest request, CancellationToken ct) =>
        Rpc.Run<ClaimResponse>("claims.heartbeat", () => Change("claims.heartbeat", request, (command, token) => claims.HeartbeatAsync(command, token), ct), Failed);

    [NeoRpcMethod("setReady")]
    public ValueTask<ClaimResponse> SetReadyAsync(SetReadyRequest request, CancellationToken ct) =>
        Rpc.Run<ClaimResponse>("claims.setReady", () => Change("claims.setReady", new ClaimRequest(request.OperationId, request.TaskId, request.ExpectedVersion),
            (command, token) => claims.SetReadyAsync(new SetReadyCommand(command.OperationId, command.TaskId, command.ExpectedVersion, request.Ready, request.AgentId), token), ct), Failed);

    private static ClaimResponse Failed(TaskErrorResponse error) => new(false, null, null, false, error);

    /// <summary>Ready for agent is an ordinary edit of the task's agent group wherever the task lives (the service checks the role on sync); a claim on a shared task is the service's compare-and-set.</summary>
    private async ValueTask<ClaimResponse> Change(string action, ClaimRequest request, Func<ClaimCommand, CancellationToken, Task<ClaimResult>> local, CancellationToken ct)
    {
        var command = new ClaimCommand(request.OperationId, request.TaskId, ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"));
        if (action == "claims.setReady" || await IsPersonalAsync(request.TaskId, ct))
        {
            var result = await local(command, ct);
            return new ClaimResponse(true, TaskRpcService.ToResponse(result.Task), null, result.Replayed, null);
        }

        var status = action switch
        {
            "claims.release" => (await shared.ReleaseAsync(request.TaskId, ct)).Status,
            "claims.takeover" => (await shared.TakeOverAsync(request.TaskId, ct)).Status,
            "claims.heartbeat" => (await shared.HeartbeatAsync(request.TaskId, ct)).Status,
            _ => (await shared.ClaimAsync(request.TaskId, ct)).Status,
        };
        sync?.Wake();
        return new ClaimResponse(true, null, status, false, null);
    }

    private Task<bool> IsPersonalAsync(string taskId, CancellationToken ct) =>
        store.QueryAsync(async (session, token) =>
        {
            var task = await session.Tasks.GetAsync(taskId, token);
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var personalSpace = profile is null ? null : await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            return task is not null && personalSpace is not null && task.SpaceId == personalSpace.Id;
        }, ct);
}
