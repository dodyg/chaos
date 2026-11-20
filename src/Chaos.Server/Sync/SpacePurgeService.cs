using Chaos.Server.Data;
using Chaos.Server.Blobs;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

/// <summary>Hard-deletes streams whose 30-day restore window has ended.</summary>
public sealed class SpacePurgeService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SpacePurgeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await PurgeDueAsync(scope.ServiceProvider.GetRequiredService<ChaosDbContext>(), clock.GetUtcNow(),
                        stoppingToken, scope.ServiceProvider.GetRequiredService<BlobService>());
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Purging expired spaces failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("Space purge service stopped.");
        }
    }

    public static async Task<int> PurgeDueAsync(ChaosDbContext db, DateTimeOffset now, CancellationToken ct,
        BlobService? blobs = null)
    {
        var due = await db.SyncStreams.Where(s => s.Kind == SyncStream.SpaceKind && s.DeletedAt != null
            && s.PurgeAfter != null && s.PurgeAfter <= now).Select(s => s.Id).ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }
        // Read before the delete: the bindings go with their stream.
        var bound = await db.AttachmentBlobBindings.AsNoTracking()
            .Where(b => due.Contains(b.StreamId))
            .Select(b => new { b.CustodianAccountId, b.Sha256 })
            .Distinct()
            .ToListAsync(ct);
        var count = await db.SyncStreams.Where(s => due.Contains(s.Id)).ExecuteDeleteAsync(ct);
        if (blobs is not null && bound.Count > 0)
        {
            await blobs.PurgeUnreferencedAsync([.. bound.Select(b => (b.CustodianAccountId, b.Sha256))], ct);
        }

        return count;
    }
}
