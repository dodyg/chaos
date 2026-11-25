using Chaos.Server.Data;

namespace Chaos.Server.Sync;

/// <summary>
/// Releases expired claims every minute (PLAN3 12.4): a claim that outlived the space's claim time goes back to the queue
/// — Ready for agent and Not started — with a diary note saying whose claim expired. A failed sweep is retried on the
/// next tick; nothing is lost, since the expiry time is on the task.
/// </summary>
public sealed class ClaimExpiryService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ClaimExpiryService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AgentService>().ExpireDueAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Releasing expired claims failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("Claim expiry service stopped.");
        }
    }
}
