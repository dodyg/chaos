using Chaos.Application.Activity;
using Chaos.Infrastructure.Sync;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record PresenceReportRequest(string SpaceId, string Kind, string EntityId, bool Editing);
public sealed record PresenceLeaveRequest(string SpaceId, string Kind, string EntityId);
public sealed record PresenceWhoRequest(string SpaceId, string Kind, string EntityId);
public sealed record PresenceMemberResponse(string MemberId, string DisplayName, bool Editing);
public sealed record PresenceResponse(bool Ok, IReadOnlyList<PresenceMemberResponse> Members);

/// <summary>Who else has a shared note, page or task open (PLAN3 §9.9). Nothing is sent or shown for the Personal space.</summary>
[NeoRpcService("presence", Version = 1)]
public sealed class PresenceRpcService(PresenceClient presence)
{
    [NeoRpcMethod("report")]
    public async ValueTask<PresenceResponse> ReportAsync(PresenceReportRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("presence.report");
        await presence.ReportAsync(request.SpaceId, request.Kind, request.EntityId, request.Editing, ct);
        return await WhoAsync(new PresenceWhoRequest(request.SpaceId, request.Kind, request.EntityId), ct);
    }

    [NeoRpcMethod("leave")]
    public async ValueTask<PresenceResponse> LeaveAsync(PresenceLeaveRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("presence.leave");
        await presence.LeaveAsync(request.SpaceId, request.Kind, request.EntityId, ct);
        return new PresenceResponse(true, []);
    }

    [NeoRpcMethod("who")]
    public async ValueTask<PresenceResponse> WhoAsync(PresenceWhoRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("presence.who");
        var members = await presence.WhoAsync(request.SpaceId, request.Kind, request.EntityId, ct);
        return new PresenceResponse(true, [.. members.Select(member => new PresenceMemberResponse(member.MemberId, member.DisplayName, member.Editing))]);
    }
}
