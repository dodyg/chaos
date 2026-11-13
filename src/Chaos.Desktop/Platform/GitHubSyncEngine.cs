using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;

namespace Chaos.Desktop.Platform;

public sealed record GitHubSyncActivity(bool Running, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc, GitHubSyncResult? Result, GitHubErrorKind? GitHubError, AppException? AppError, string? ConnectionId = null);

/// <summary>Serializes explicit full passes and per-task action syncs, and drains before data replacement. No startup authentication.</summary>
public sealed class GitHubSyncEngine(IGitHubSyncRunner runner, TimeProvider clock, TimeSpan debounce = default) : IDisposable
{
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _replacementGate = new(1, 1);
    private CancellationTokenSource? _active;
    private Task _work = Task.CompletedTask;
    private int _pauseDepth;
    private bool _disposed;
    private string? _activeTaskId;
    private readonly HashSet<string> _pendingTasks = new(StringComparer.Ordinal);
    private readonly Queue<string> _taskQueue = new();
    private readonly Dictionary<string, GitHubSyncActivity> _taskActivity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ITimer Timer, long Generation)> _debounced = new(StringComparer.Ordinal);
    private long _generation;
    private (string? RepositoryId, string? ConnectionId)? _pendingFull;
    private GitHubSyncActivity _activity = new(false, null, null, null, null, null);
    private GitHubSyncActivity _fullActivity = new(false, null, null, null, null, null);
    public GitHubSyncActivity Status { get { lock (_stateGate) return _fullActivity; } }
    public event Action? WorkCompleted;

    public GitHubSyncActivity? TaskStatus(string taskId)
    {
        lock (_stateGate)
        {
            if (_activeTaskId == taskId || _pendingTasks.Contains(taskId) || _debounced.ContainsKey(taskId)) return new(true, null, null, null, null, null);
            return _taskActivity.GetValueOrDefault(taskId);
        }
    }

    // Called only after a committed local action. Pausing/replacing data must not turn a
    // successful local edit into an RPC failure; the persisted dirty flag remains authoritative.
    public void RequestTaskSync(string taskId, bool immediate = false)
    {
        lock (_stateGate)
        {
            if (_disposed || _pauseDepth != 0) return;
            if (_debounced.Remove(taskId, out var previous)) previous.Timer.Dispose();
            if (!immediate && debounce > TimeSpan.Zero)
            {
                var generation = ++_generation;
                var timer = clock.CreateTimer(_ => FlushTask(taskId, generation), null, debounce, Timeout.InfiniteTimeSpan);
                _debounced.Add(taskId, (timer, generation));
                return;
            }
            QueueTask(taskId);
        }
    }

    private void FlushTask(string taskId, long generation)
    {
        lock (_stateGate)
        {
            if (_disposed || _pauseDepth != 0 || !_debounced.TryGetValue(taskId, out var pending) || pending.Generation != generation) return;
            _debounced.Remove(taskId);
            pending.Timer.Dispose();
            QueueTask(taskId);
        }
    }

    private void QueueTask(string taskId)
    {
            if (_activity.Running) { if (_pendingTasks.Add(taskId)) _taskQueue.Enqueue(taskId); return; }
            Start(null, null, taskId);
    }

    private void ClearDebounced()
    {
        foreach (var pending in _debounced.Values) pending.Timer.Dispose();
        _debounced.Clear();
    }

    private void Start(string? repositoryId, string? connectionId, string? taskId)
    {
        var started = clock.GetUtcNow();
        _activeTaskId = taskId;
        _activity = new(true, started, null, null, null, null, connectionId);
        if (taskId is null) _fullActivity = _activity;
        _active = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var active = _active;
        _work = Task.Run(() => Execute(repositoryId, connectionId, started, active, taskId));
    }

    public GitHubSyncActivity RequestSync(string? repositoryId, string? connectionId = null)
    {
        lock (_stateGate)
        {
            if (_disposed) throw new AppException(AppErrorKind.Cancelled, "GitHub sync is shutting down.");
            if (_pauseDepth != 0) throw AppException.VersionConflict("GitHub sync is paused while local data is replaced.");
            if (_activity.Running)
            {
                if (_activeTaskId is not null)
                {
                    if (_pendingFull is { } pending && pending.ConnectionId != connectionId) throw AppException.VersionConflict("A previous GitHub connection is still finishing. Try again.");
                    _pendingFull = (_pendingFull is { } queued && queued.RepositoryId != repositoryId ? null : repositoryId, connectionId);
                    _fullActivity = new(true, _activity.StartedAtUtc, null, null, null, null, connectionId);
                    return _fullActivity;
                }
                if (_activity.ConnectionId != connectionId) throw AppException.VersionConflict("A previous GitHub connection is still finishing. Try again.");
                return _activity;
            }
            Start(repositoryId, connectionId, null);
            return _activity;
        }
    }

    private async Task Execute(string? repositoryId, string? connectionId, DateTimeOffset started, CancellationTokenSource active, string? taskId)
    {
        while (true)
        {
            GitHubSyncResult? result = null; GitHubErrorKind? githubError = null; AppException? appError = null;
            try { result = taskId is null ? await runner.RunAsync(repositoryId, active.Token, connectionId) : await runner.RunTaskAsync(taskId, active.Token); }
            catch (GitHubApiException error) { githubError = error.Kind; }
            catch (AppException error) { appError = error; }
            catch (OperationCanceledException) { appError = new(AppErrorKind.Cancelled, "GitHub sync was cancelled. Committed changes are kept; the next pass can continue."); }
            catch (Exception) { appError = new(AppErrorKind.StorageUnavailable, "GitHub sync could not finish. Some changes may already have arrived; retry to continue."); }
            WorkCompleted?.Invoke();
            lock (_stateGate)
            {
                _activity = new(false, started, clock.GetUtcNow(), result, githubError, appError, connectionId);
                if (taskId is null) _fullActivity = _activity;
                if (taskId is not null)
                {
                    _taskActivity[taskId] = _activity;
                    if (_taskActivity.Count > 256) _taskActivity.Remove(_taskActivity.Keys.First(id => id != taskId));
                }
                if (!active.IsCancellationRequested && _pendingFull is { } full)
                {
                    repositoryId = full.RepositoryId; connectionId = full.ConnectionId; taskId = null; _pendingFull = null;
                }
                else if (!active.IsCancellationRequested && _pendingTasks.Count > 0)
                {
                    taskId = _taskQueue.Dequeue(); _pendingTasks.Remove(taskId); repositoryId = null; connectionId = null;
                }
                else { _activeTaskId = null; _active = null; active.Dispose(); return; }
                started = clock.GetUtcNow();
                _activeTaskId = taskId;
                _activity = new(true, started, null, null, null, null, connectionId);
                if (taskId is null) _fullActivity = _activity;
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
            ClearDebounced();
            _pendingTasks.Clear(); _taskQueue.Clear(); _pendingFull = null;
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
                lock (_stateGate) { _activity = new(false, null, null, null, null, null); _fullActivity = _activity; _taskActivity.Clear(); }
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
            ClearDebounced();
            _pendingTasks.Clear(); _taskQueue.Clear(); _pendingFull = null;
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
