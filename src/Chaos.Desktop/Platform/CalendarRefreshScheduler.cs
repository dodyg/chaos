using Chaos.Application.Calendar;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Refreshes calendar feeds every 15 minutes while the app runs, and only while at least one feed
/// exists: with none, a tick makes no request at all (PLAN2 §10.2, §10.4). A failure is recorded on
/// the source by the refresher; the timer never stops because of one.
/// </summary>
public sealed class CalendarRefreshScheduler(CalendarRefresher refresher, TimeProvider clock) : IDisposable
{
    private ITimer? _timer;
    private int _running;

    public void Start() => _timer ??= clock.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), CalendarRefresher.Interval);

    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (await refresher.HasSourcesAsync())
            {
                await refresher.RefreshAsync();
            }
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
