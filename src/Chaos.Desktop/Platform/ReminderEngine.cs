using System.Diagnostics;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Reminders;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Runs reminder delivery and OS reconciliation after commit, off the UI thread (PLAN §11):
/// a full resync and reconciliation at startup (which also covers anything missed while the
/// app was closed: missed repeats are coalesced by delivery), then a tick on a timer and
/// whenever the frontend asks for alerts. Ticks never overlap. A failed tick is traced with a
/// correlation ID and retried on the next one; capture never waits on reminders.
/// </summary>
public sealed class ReminderEngine(ReminderCommands reminders, INotificationAdapter adapter, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private bool _firstRun = true;

    /// <summary>The last failure, for diagnostics; null after a successful tick.</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        _timer ??= clock.CreateTimer(_ => _ = RunScheduledAsync(), null, TimeSpan.Zero, Interval);
    }

    /// <summary>Resynchronizes every desired notification (startup, resume, zone change).</summary>
    public async Task ResyncAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await reminders.ResyncAllAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Delivers everything due and brings OS schedules in line.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var delivered in await reminders.DeliverDueAsync(ct))
            {
                var presentation = delivered.Job.OsState == NotificationOsState.Scheduled
                    ? NotificationPresentation.System // the OS already showed it at its time
                    : await adapter.ShowAsync(delivered.Request, ct) switch
                    {
                        NotificationShowOutcome.System => NotificationPresentation.System,
                        NotificationShowOutcome.InApp => NotificationPresentation.InApp,
                        _ => NotificationPresentation.Suppressed,
                    };
                await reminders.MarkPresentedAsync(delivered.Job.Id, presentation, ct);
            }

            await reminders.ReconcileOsAsync(ct);
            LastError = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunScheduledAsync()
    {
        try
        {
            if (_firstRun)
            {
                _firstRun = false;
                await ResyncAsync(CancellationToken.None);
            }

            await TickAsync(CancellationToken.None);
        }
        catch (Exception error) when (error is AppException or IOException or InvalidOperationException)
        {
            var correlation = Guid.NewGuid().ToString("N")[..8];
            LastError = $"Reminders could not be processed ({correlation}); retrying in {Interval.TotalSeconds:0} seconds.";
            Trace.TraceWarning("Reminder engine tick {0} failed: {1}", correlation, error);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();

        // Wait for a tick in progress so storage is not disposed underneath it.
        _gate.Wait();
        _gate.Release();
        _gate.Dispose();
    }
}

/// <summary>
/// The development presenter PLAN §11 allows for an ordinary run: reminders appear as in-app
/// alerts while Chaos is running (the frontend polls <c>reminders.alerts</c>). It cannot
/// schedule with Windows, so nothing is delivered after the app exits; the installed MSIX
/// adapter replaces it once the package 0 notification spike passes on a real desktop.
/// </summary>
public sealed class InAppNotificationAdapter : INotificationAdapter
{
    public NotificationCapabilities Capabilities { get; } = new(
        "In-app (development)",
        SchedulesWhileClosed: false,
        SupportsActions: true,
        NotificationPermission.Granted,
        "Reminders and follow-ups appear inside Chaos while it is open, with Open, Done and Snooze. "
        + "Windows notifications that arrive after Chaos is closed need the installed package, which this build does not have yet. "
        + "Anything due while Chaos was closed is shown once when it starts again.");

    public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct) =>
        Task.FromResult(new NotificationScheduleResult(false, null, "This build cannot schedule Windows notifications."));

    public Task CancelAsync(string osNotificationId, CancellationToken ct) => Task.CompletedTask;

    public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct) =>
        Task.FromResult(NotificationShowOutcome.InApp);
}
