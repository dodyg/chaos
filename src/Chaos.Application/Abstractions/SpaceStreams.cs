namespace Chaos.Application.Abstractions;

/// <summary>
/// The device's side of shared-space membership (PLAN3 §8.2, §7.7). Space commands call the service first and only then
/// record the outcome here, so no network call ever runs inside a local transaction.
/// </summary>
public interface ISpaceStreams
{
    /// <summary>Records that this device may now read the space; the next sync snapshots it before it shows in the sidebar.</summary>
    Task JoinAsync(Guid spaceId, int role, CancellationToken ct);

    /// <summary>Marks the space for eviction; the next sync removes its entities from this device.</summary>
    Task LeaveAsync(Guid spaceId, CancellationToken ct);

    /// <summary>Runs one sync cycle now (push, discovery, eviction, pull) and returns its problem, if any.</summary>
    Task<string?> SyncAsync(CancellationToken ct);
}
