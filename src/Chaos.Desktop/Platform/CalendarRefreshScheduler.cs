using Chaos.Application.Activity;
using Chaos.Application.Calendar;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Refreshes calendar feeds every 15 minutes while the app runs, and only while at least one feed
/// exists: with none, a tick makes no request at all (PLAN2 §10.2, §10.4). A failure is recorded on
/// the source by the refresher; the timer never stops because of one.
/// </summary>
public sealed class CalendarRefreshScheduler(CalendarRefresher refresher, TimeProvider clock, IJobRuns? jobRuns = null) : IDisposable
{
    private ITimer? _timer;
    private int _running;

    public void Start() => _timer ??= clock.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), CalendarRefresher.GoogleInterval);

    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            await JobPass.RunAsync(jobRuns, ActivitySources.Schedule, ActivityActions.CalendarRefresh, async ct =>
            {
                if (await refresher.HasSourcesAsync(ct))
                {
                    await refresher.RefreshAsync(ct: ct, onlyDue: true); // Google every 5 minutes, a feed every 15
                }
            }, CancellationToken.None);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("Calendar refresh failed: " + error.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
