using Chaos.Application.Activity;
using Chaos.Infrastructure.Sync;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>An attention item for the Sync page. <c>Actions</c> are names the page offers: Sync now, Dismiss.</summary>
public sealed record SyncAttentionDto(string Id, string Category, string Message, string Suggestion, string CreatedUtc, IReadOnlyList<string> Actions);

public sealed record SyncStatusRequest();

/// <summary>
/// <c>Kind</c> is Hidden, Synced, Syncing, Offline, NeedsAttention, SignedOut or UpdateNeeded. <c>LastSyncedUtc</c> is null before the first sync.
/// Hidden means this computer never used an account, so the sidebar shows nothing.
/// </summary>
public sealed record SyncStatusResponse(bool Ok, string Kind, string? LastSyncedUtc, double Pending, string? Detail, IReadOnlyList<SyncAttentionDto> Attention);

public sealed record SyncNowRequest();
public sealed record SyncDismissRequest(string Id);

/// <summary>The sync indicator and Settings → Account → Sync (PLAN2 P15 item 3). Reads the local database only; never waits on the network.</summary>
[NeoRpcService("syncStatus", Version = 1)]
public sealed class SyncStatusRpcService(SyncStatusReader reader, SyncEngine engine)
{
    [NeoRpcMethod("status")]
    public async ValueTask<SyncStatusResponse> StatusAsync(SyncStatusRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("syncStatus.status");
        return Map(await reader.GetAsync(ct));
    }

    /// <summary>Asks the engine to run a cycle now and returns at once; the next status says how it went.</summary>
    [NeoRpcMethod("syncNow")]
    public async ValueTask<SyncStatusResponse> SyncNowAsync(SyncNowRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("syncStatus.syncNow");
        engine.Wake();
        return Map(await reader.GetAsync(ct));
    }

    [NeoRpcMethod("dismiss")]
    public async ValueTask<SyncStatusResponse> DismissAsync(SyncDismissRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("syncStatus.dismiss");
        await reader.ResolveAsync(request.Id, ct);
        return Map(await reader.GetAsync(ct));
    }

    private static SyncStatusResponse Map(SyncStatus status) => new(true, status.Kind.ToString(),
        status.LastSyncedAt?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture), status.PendingChanges, status.Detail,
        [.. status.Attention.Select(item => new SyncAttentionDto(item.Id, item.Category, item.Message, item.Suggestion,
            item.CreatedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture), item.Actions))]);
}
