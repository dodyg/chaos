using Chaos.Application.Activity;
using Chaos.Application.Calendar;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Runs a time block pass when one is due (PLAN3 §11): a task changed since the last pass, or five minutes passed. The
/// tick is every 30 seconds and the due check is one indexed read; with time blocking off, or on a device that is not
/// the runner, a tick makes no request. The same gate serves the <em>Sync now</em> button and keeps a pass from
/// writing while restore or reset replaces the database.
/// </summary>
public sealed class TimeBlockScheduler(TimeBlockCommands commands, TimeProvider clock, IJobRuns? jobRuns = null) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private int _ticking;

    public void Start() => _timer ??= clock.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(25), Interval);

    /// <summary>One due check, and a pass when one is due. Returns null when nothing ran.</summary>
    public async Task<TimeBlockPassResult?> TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return null;
        }

        try
        {
            return await RunAsync(onlyDue: true, CancellationToken.None);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("Time block pass could not run: {0}", error.GetType().Name);
            return null;
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>The button's pass: always runs when this device is the runner.</summary>
    public Task<TimeBlockPassResult?> RunNowAsync(CancellationToken ct) => RunAsync(onlyDue: false, ct);

    private async Task<TimeBlockPassResult?> RunAsync(bool onlyDue, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            TimeBlockPassResult? result = null;
            await JobPass.RunAsync(jobRuns, ActivitySources.Schedule, ActivityActions.TimeBlockSync, async token =>
            {
                if (!onlyDue || await commands.IsDueAsync(token))
                {
                    var pass = await commands.SyncAsync(token);
                    if (pass.Ran)
                    {
                        result = pass;
                    }
                }
            }, ct);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs <paramref name="work"/> while no pass is writing (restore and reset replace the database).</summary>
    public async Task RunPausedAsync(Func<Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }
}
