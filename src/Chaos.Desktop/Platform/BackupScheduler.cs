using System.Diagnostics;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.DataProtection;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Makes the daily backup in the background (PLAN §14: at least daily after changes). It
/// checks shortly after start and then every <see cref="Interval"/>; a backup is made only when
/// the newest one is a day old and something changed since. Failures are kept by the backup
/// manager for Settings and retried on the next check; nothing here blocks the UI.
/// </summary>
public sealed class BackupScheduler(DataProtectionService protection, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private ITimer? _timer;
    private int _running;

    public void Start()
    {
        _timer ??= clock.CreateTimer(_ => _ = RunAsync(), null, FirstCheck, Interval);
    }

    private async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            await protection.RunScheduledBackupIfDueAsync(CancellationToken.None);
        }
        catch (AppException error)
        {
            Trace.TraceWarning("Scheduled backup failed: {0}", error.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
