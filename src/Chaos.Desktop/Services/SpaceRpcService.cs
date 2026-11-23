using Chaos.Application.Abstractions;
using Chaos.Application.Spaces;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record SpaceResponse(string SpaceId, string Name, string Kind, int? Role, int MemberCount);
public sealed record SpaceListRequest();
public sealed record SpaceListResponse(bool Ok, IReadOnlyList<SpaceResponse> Spaces, TaskErrorResponse? Error);
public sealed record SpaceCreateRequest(string Name);
public sealed record SpaceJoinRequest(string Link);
public sealed record SpaceIdRequest(string SpaceId);
public sealed record SpaceOperationResponse(bool Ok, SpaceResponse? Space, TaskErrorResponse? Error);
public sealed record SpaceMoveRequest(string ContainerId, string SpaceId);
public sealed record SpaceMoveResponse(bool Ok, string? SpaceId, int EntityCount, TaskErrorResponse? Error);
public sealed record SpaceShareListRequest(string ContainerId, string Name);
public sealed record SpaceDeleteRequest(string SpaceId, string TypedName);
public sealed record SpaceRoleRequest(string SpaceId, string MemberId, int Role);
public sealed record SpaceMemberRequest(string SpaceId, string MemberId);
public sealed record SpaceMyNameRequest(string SpaceId, string DisplayName);
public sealed record SpaceMemberResponse(string MemberId, string Name, int Role, int Kind, string JoinedAt, bool IsMe);
public sealed record SpaceMembersResponse(bool Ok, IReadOnlyList<SpaceMemberResponse> Members, TaskErrorResponse? Error);
public sealed record SpaceInviteRequest(string SpaceId, string? Email, int Role, int? ExpiresInDays, int? MaxUses);
public sealed record SpaceInviteResponseItem(string Id, string? Email, int Role, string CreatedAt, string? ExpiresAt, int? MaxUses, int Uses, bool Revoked);
public sealed record SpaceInviteResponse(bool Ok, SpaceInviteResponseItem? Invite, string? Link, TaskErrorResponse? Error);
public sealed record SpaceInvitesResponse(bool Ok, IReadOnlyList<SpaceInviteResponseItem> Invites, TaskErrorResponse? Error);
public sealed record SpaceRevokeInviteRequest(string SpaceId, string InviteId);
public sealed record SpaceDoneResponse(bool Ok, TaskErrorResponse? Error);
public sealed record SpaceStateDto(string Id, string Name, string BaseStatus, string Version);
public sealed record SpaceStatesResponse(bool Ok, IReadOnlyList<SpaceStateDto> States, TaskErrorResponse? Error);
public sealed record CreateSpaceStateRequest(string OperationId, string SpaceId, string Name, string BaseStatus);
public sealed record DeleteSpaceStateRequest(string OperationId, string StateId, string ExpectedVersion);
public sealed record SpaceTagPolicyRequest(string SpaceId, bool OwnerOnly);
public sealed record SpaceTagPolicyResponse(bool Ok, bool OwnerOnly, TaskErrorResponse? Error);

/// <summary>
/// Shared spaces for the window (PLAN3 Q5). Every method that changes membership needs the service and says so offline;
/// the sidebar list and the people shown are what this device already holds.
/// </summary>
[NeoRpcService("spaces", Version = 1)]
public sealed class SpaceRpcService(SpaceQueries queries, SpaceCommands commands, SpaceStateCommands states)
{
    [NeoRpcMethod("tagPolicy")]
    public ValueTask<SpaceTagPolicyResponse> TagPolicyAsync(SpaceIdRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.tagPolicy", async () => new SpaceTagPolicyResponse(true, await states.GetTagsOwnerOnlyAsync(request.SpaceId, ct), null),
            error => new SpaceTagPolicyResponse(false, false, error));

    [NeoRpcMethod("setTagPolicy")]
    public ValueTask<SpaceDoneResponse> SetTagPolicyAsync(SpaceTagPolicyRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.setTagPolicy", async () => { await states.SetTagsOwnerOnlyAsync(request.SpaceId, request.OwnerOnly, ct); return new SpaceDoneResponse(true, null); }, Failed);
    [NeoRpcMethod("states")]
    public ValueTask<SpaceStatesResponse> StatesAsync(SpaceIdRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.states", async () => new SpaceStatesResponse(true,
            [.. (await states.ListAsync(request.SpaceId, ct)).Select(state => new SpaceStateDto(state.Id, state.Name, state.BaseStatus.ToString(), state.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))], null),
            error => new SpaceStatesResponse(false, [], error));

    [NeoRpcMethod("createState")]
    public ValueTask<SpaceDoneResponse> CreateStateAsync(CreateSpaceStateRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.createState", async () => { await states.CreateAsync(new(request.OperationId, request.SpaceId, request.Name,
            RpcParse.Enum<TaskItemStatus>(request.BaseStatus, "baseStatus")), ct); return new SpaceDoneResponse(true, null); }, Failed);

    [NeoRpcMethod("deleteState")]
    public ValueTask<SpaceDoneResponse> DeleteStateAsync(DeleteSpaceStateRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.deleteState", async () => { await states.DeleteAsync(new(request.OperationId, request.StateId,
            RpcParse.Version(request.ExpectedVersion)), ct); return new SpaceDoneResponse(true, null); }, Failed);
    [NeoRpcMethod("list")]
    public ValueTask<SpaceListResponse> ListAsync(SpaceListRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.list", async () => new SpaceListResponse(true, [.. (await queries.ListAsync(ct)).Select(Map)], null),
            error => new SpaceListResponse(false, [], error));

    [NeoRpcMethod("create")]
    public ValueTask<SpaceOperationResponse> CreateAsync(SpaceCreateRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.create", async () => await Result(await commands.CreateAsync(request.Name, ct: ct), ct),
            error => new SpaceOperationResponse(false, null, error));

    [NeoRpcMethod("join")]
    public ValueTask<SpaceOperationResponse> JoinAsync(SpaceJoinRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.join", async () => await Result(await commands.JoinWithLinkAsync(request.Link, ct), ct),
            error => new SpaceOperationResponse(false, null, error));

    [NeoRpcMethod("leave")]
    public ValueTask<SpaceDoneResponse> LeaveAsync(SpaceIdRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.leave", () => Ok(commands.LeaveAsync(request.SpaceId, ct)), Failed);

    [NeoRpcMethod("move")]
    public ValueTask<SpaceMoveResponse> MoveAsync(SpaceMoveRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.move", async () =>
        {
            var moved = await commands.MoveAsync(request.ContainerId, request.SpaceId, ct);
            return new SpaceMoveResponse(true, moved.SpaceId, moved.EntityCount, null);
        }, error => new SpaceMoveResponse(false, null, 0, error));

    /// <summary>Share list… (E7): a new shared space named after the list, then the list moves into it.</summary>
    [NeoRpcMethod("shareList")]
    public ValueTask<SpaceMoveResponse> ShareListAsync(SpaceShareListRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.shareList", async () =>
        {
            var space = await commands.CreateAsync(request.Name, ct: ct);
            var moved = await commands.MoveAsync(request.ContainerId, space.SpaceId, ct);
            return new SpaceMoveResponse(true, moved.SpaceId, moved.EntityCount, null);
        }, error => new SpaceMoveResponse(false, null, 0, error));

    [NeoRpcMethod("delete")]
    public ValueTask<SpaceDoneResponse> DeleteAsync(SpaceDeleteRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.delete", () => Ok(commands.DeleteSpaceAsync(request.SpaceId, request.TypedName, ct)), Failed);

    [NeoRpcMethod("restore")]
    public ValueTask<SpaceDoneResponse> RestoreAsync(SpaceIdRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.restore", () => Ok(commands.RestoreSpaceAsync(request.SpaceId, ct)), Failed);

    [NeoRpcMethod("members")]
    public ValueTask<SpaceMembersResponse> MembersAsync(SpaceIdRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.members", async () =>
        {
            var local = await queries.MembersAsync(request.SpaceId, ct);
            var mine = local.FirstOrDefault(person => person.IsMe)?.ActorId;
            var members = await commands.MembersAsync(request.SpaceId, ct);
            return new SpaceMembersResponse(true,
                [.. members.Where(member => member.RemovedAt is null).Select(member => new SpaceMemberResponse(member.MemberId, member.Name, member.Role, member.Kind,
                    member.JoinedAt, mine is not null && mine.EndsWith(member.MemberId, StringComparison.OrdinalIgnoreCase)))], null);
        }, error => new SpaceMembersResponse(false, [], error));

    [NeoRpcMethod("changeRole")]
    public ValueTask<SpaceDoneResponse> ChangeRoleAsync(SpaceRoleRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.changeRole", () => Ok(commands.ChangeRoleAsync(request.SpaceId, request.MemberId, request.Role, ct)), Failed);

    [NeoRpcMethod("removeMember")]
    public ValueTask<SpaceDoneResponse> RemoveMemberAsync(SpaceMemberRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.removeMember", () => Ok(commands.RemoveMemberAsync(request.SpaceId, request.MemberId, ct)), Failed);

    [NeoRpcMethod("setMyName")]
    public ValueTask<SpaceDoneResponse> SetMyNameAsync(SpaceMyNameRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.setMyName", () => Ok(commands.SetMyNameAsync(request.SpaceId, request.DisplayName, ct)), Failed);

    [NeoRpcMethod("invites")]
    public ValueTask<SpaceInvitesResponse> InvitesAsync(SpaceIdRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.invites", async () => new SpaceInvitesResponse(true, [.. (await commands.InvitesAsync(request.SpaceId, ct)).Select(MapInvite)], null),
            error => new SpaceInvitesResponse(false, [], error));

    [NeoRpcMethod("invite")]
    public ValueTask<SpaceInviteResponse> InviteAsync(SpaceInviteRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.invite", async () =>
        {
            var created = await commands.InviteAsync(request.SpaceId, request.Email, request.Role, request.ExpiresInDays, request.MaxUses, ct);
            return new SpaceInviteResponse(true, MapInvite(created.Invite), created.Link, null);
        }, error => new SpaceInviteResponse(false, null, null, error));

    [NeoRpcMethod("revokeInvite")]
    public ValueTask<SpaceDoneResponse> RevokeInviteAsync(SpaceRevokeInviteRequest request, CancellationToken ct) =>
        Rpc.Run("spaces.revokeInvite", () => Ok(commands.RevokeInviteAsync(request.SpaceId, request.InviteId, ct)), Failed);

    private static async ValueTask<SpaceDoneResponse> Ok(Task body)
    {
        await body;
        return new SpaceDoneResponse(true, null);
    }

    private static SpaceDoneResponse Failed(TaskErrorResponse error) => new(false, error);

    private async Task<SpaceOperationResponse> Result(SharedSpaceResult space, CancellationToken ct)
    {
        var summary = (await queries.ListAsync(ct)).FirstOrDefault(entry => string.Equals(entry.SpaceId, space.SpaceId, StringComparison.OrdinalIgnoreCase));
        return new SpaceOperationResponse(true, summary is null ? new SpaceResponse(space.SpaceId, space.Name, "Shared", space.Role, 1) : Map(summary), null);
    }

    private static SpaceResponse Map(SpaceSummary space) => new(space.SpaceId, space.Name, space.Kind.ToString(), space.Role, space.MemberCount);

    private static SpaceInviteResponseItem MapInvite(ServiceInvite invite) =>
        new(invite.Id, invite.Email, invite.Role, invite.CreatedAt, invite.ExpiresAt, invite.MaxUses, invite.Uses, invite.RevokedAt is not null);
}
