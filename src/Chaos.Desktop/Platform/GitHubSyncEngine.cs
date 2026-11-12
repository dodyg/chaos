using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;

namespace Chaos.Desktop.Platform;

public sealed record GitHubSyncActivity(bool Running, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc, GitHubSyncResult? Result, GitHubErrorKind? GitHubError, AppException? AppError, string? ConnectionId = null);

/// <summary>Manual-only G5 engine: coalesces requests, exposes progress, and drains work before restore/reset or shutdown. No startup authentication or timer.</summary>
public sealed class GitHubSyncEngine(IGitHubSyncRunner runner, TimeProvider clock) : IDisposable
{
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _replacementGate = new(1, 1);
    private CancellationTokenSource? _active;
    private Task _work = Task.CompletedTask;
    private int _pauseDepth;
    private bool _disposed;
    private GitHubSyncActivity _activity = new(false, null, null, null, null, null);
    public GitHubSyncActivity Status { get { lock (_stateGate) return _activity; } }

    public GitHubSyncActivity RequestSync(string? repositoryId, string? connectionId = null)
    {
        lock (_stateGate)
        {
            if (_disposed) throw new AppException(AppErrorKind.Cancelled, "GitHub sync is shutting down.");
            if (_pauseDepth != 0) throw AppException.VersionConflict("GitHub sync is paused while local data is replaced.");
            if (_activity.Running)
            {
                if (_activity.ConnectionId != connectionId) throw AppException.VersionConflict("A previous GitHub connection is still finishing. Try again.");
                return _activity;
            }
            var started = clock.GetUtcNow();
            _activity = new(true, started, null, null, null, null, connectionId);
            _active = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var active = _active;
            _work = Task.Run(() => Execute(repositoryId, connectionId, started, active));
            return _activity;
        }
    }

    private async Task Execute(string? repositoryId, string? connectionId, DateTimeOffset started, CancellationTokenSource active)
    {
        GitHubSyncResult? result = null; GitHubErrorKind? githubError = null; AppException? appError = null;
        try { result = await runner.RunAsync(repositoryId, active.Token, connectionId); }
        catch (GitHubApiException error) { githubError = error.Kind; }
        catch (AppException error) { appError = error; }
        catch (OperationCanceledException) { appError = new(AppErrorKind.Cancelled, "GitHub sync was cancelled. Committed changes are kept; the next pass can continue."); }
        catch (Exception) { appError = new(AppErrorKind.StorageUnavailable, "GitHub sync could not finish. Some changes may already have arrived; retry to continue."); }
        finally
        {
            lock (_stateGate)
            {
                _activity = new(false, started, clock.GetUtcNow(), result, githubError, appError, connectionId);
                _active = null;
                active.Dispose();
            }
        }
    }

    public Task WaitForIdleAsync(CancellationToken ct = default) { lock (_stateGate) return _work.WaitAsync(ct); }

    public async Task RunPausedAsync(Func<Task> work, CancellationToken ct)
    {
        Task active;
        lock (_stateGate)
        {
            if (_disposed) throw new AppException(AppErrorKind.Cancelled, "GitHub sync is shutting down.");
            _pauseDepth++;
            _active?.Cancel();
            active = _work;
        }
        try
        {
            await active.WaitAsync(ct);
            await _replacementGate.WaitAsync(ct);
            try
            {
                await work();
                lock (_stateGate) _activity = new(false, null, null, null, null, null);
            }
            finally { _replacementGate.Release(); }
        }
        finally { lock (_stateGate) _pauseDepth--; }
    }

    public void Dispose()
    {
        Task active;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            active = _work;
        }
        active.GetAwaiter().GetResult();
        _replacementGate.Wait();
        _replacementGate.Release();
        _replacementGate.Dispose();
        _stop.Dispose();
    }
}
