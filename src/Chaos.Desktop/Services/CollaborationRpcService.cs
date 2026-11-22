using Chaos.Application.Activity;
using Chaos.Infrastructure.Activity;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record CollaborationListRequest();
public sealed record CollaborationListResponse(bool Ok, IReadOnlyList<CollaborationNotice> Notices, TaskErrorResponse? Error);
public sealed record CollaborationReadRequest(string Id);
public sealed record CollaborationReadResponse(bool Ok, TaskErrorResponse? Error);
public sealed record CollaborationSettingsRequest(string SpaceId);
public sealed record CollaborationSettingsResponse(bool Ok, SpaceNotificationPreference? Settings, TaskErrorResponse? Error);
public sealed record CollaborationSetSettingsRequest(string SpaceId, SpaceNotificationPreference Settings);
public sealed record CollaborationDefaultRequest(string SpaceId);
public sealed record CollaborationDefaultResponse(bool Ok, int? OffsetMinutes, TaskErrorResponse? Error);
public sealed record CollaborationSetDefaultRequest(string SpaceId, int? OffsetMinutes);

[NeoRpcService("collaboration", Version = 1)]
public sealed class CollaborationRpcService(CollaborationInbox inbox, SpaceNotificationPreferences preferences)
{
    [NeoRpcMethod("list")]
    public ValueTask<CollaborationListResponse> ListAsync(CollaborationListRequest request, CancellationToken ct) =>
        Rpc.Run<CollaborationListResponse>("collaboration.list", async () => new(true, await inbox.ListAsync(ct), null),
            error => new(false, [], error));

    [NeoRpcMethod("read")]
    public ValueTask<CollaborationReadResponse> ReadAsync(CollaborationReadRequest request, CancellationToken ct) =>
        Rpc.Run<CollaborationReadResponse>("collaboration.read", async () =>
        {
            using var scope = ActivityScope.Rpc("collaboration.read");
            await inbox.MarkReadAsync(request.Id, ct);
            return new(true, null);
        }, error => new(false, error));

    [NeoRpcMethod("settings")]
    public ValueTask<CollaborationSettingsResponse> SettingsAsync(CollaborationSettingsRequest request, CancellationToken ct) =>
        Rpc.Run<CollaborationSettingsResponse>("collaboration.settings", async () => new(true, await preferences.GetAsync(request.SpaceId, ct), null),
            error => new(false, null, error));

    [NeoRpcMethod("setSettings")]
    public ValueTask<CollaborationReadResponse> SetSettingsAsync(CollaborationSetSettingsRequest request, CancellationToken ct) =>
        Rpc.Run<CollaborationReadResponse>("collaboration.setSettings", async () =>
        {
            using var scope = ActivityScope.Rpc("collaboration.setSettings");
            await preferences.SetAsync(request.SpaceId, request.Settings, ct);
            return new(true, null);
        }, error => new(false, error));

    [NeoRpcMethod("defaultDeadline")]
    public ValueTask<CollaborationDefaultResponse> DefaultDeadlineAsync(CollaborationDefaultRequest request, CancellationToken ct) =>
        Rpc.Run<CollaborationDefaultResponse>("collaboration.defaultDeadline", async () => new(true, await preferences.GetDefaultDeadlineAsync(request.SpaceId, ct), null),
            error => new(false, null, error));

    [NeoRpcMethod("setDefaultDeadline")]
    public ValueTask<CollaborationReadResponse> SetDefaultDeadlineAsync(CollaborationSetDefaultRequest request, CancellationToken ct) =>
        Rpc.Run<CollaborationReadResponse>("collaboration.setDefaultDeadline", async () =>
        {
            using var scope = ActivityScope.Rpc("collaboration.setDefaultDeadline");
            await preferences.SetDefaultDeadlineAsync(request.SpaceId, request.OffsetMinutes, ct);
            return new(true, null);
        }, error => new(false, error));
}
