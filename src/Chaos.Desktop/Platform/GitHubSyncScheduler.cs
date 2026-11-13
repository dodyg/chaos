using System.Net.NetworkInformation;
using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;

namespace Chaos.Desktop.Platform;

/// <summary>Schedules serialized full passes and persisted per-issue retries.</summary>
public sealed class GitHubSyncScheduler(GitHubSyncEngine engine, Func<CancellationToken, Task<GitHubSchedule>> schedule, TimeProvider clock) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private Task? _loop;
    private int _force;

    public void Start()
    {
        if (_loop is not null) return;
        engine.WorkCompleted += Signal;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _loop = Task.Run(RunAsync);
    }

    public void Wake()
    {
        Interlocked.Exchange(ref _force, 1);
        Signal();
    }

    private void Signal()
    {
        try { _changed.Release(); }
        catch (SemaphoreFullException) { System.Diagnostics.Trace.TraceInformation("GitHub scheduler wake already queued."); }
        catch (ObjectDisposedException) { System.Diagnostics.Trace.TraceInformation("GitHub scheduler is already stopped."); }
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Wake();
    }

    private async Task RunAsync()
    {
        var nextFull = clock.GetUtcNow().AddSeconds(10);
        var attemptedLinks = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        string? connectionId = null;
        var failures = 0;
        try
        {
            while (true)
            {
                try
                {
                    var state = await schedule(_stop.Token);
                    var connection = state.Status.Connection;
                    if (connection is not { State: 0, DeletedAtUtc: null } || !state.Status.Repositories.Any(r => r.State == 0 && r.DeletedAtUtc is null))
                    {
                        connectionId = null;
                        attemptedLinks.Clear();
                        await _changed.WaitAsync(_stop.Token);
                        continue;
                    }
                    var now = clock.GetUtcNow();
                    foreach (var id in attemptedLinks.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray()) attemptedLinks.Remove(id);
                    if (connectionId != connection.Id)
                    {
                        if (connectionId is not null) nextFull = now.AddSeconds(10);
                        connectionId = connection.Id;
                        attemptedLinks.Clear();
                    }
                    if (Interlocked.Exchange(ref _force, 0) != 0)
                    {
                        attemptedLinks.Clear();
                        nextFull = now;
                    }
                    var retry = state.LinkRetries.Where(l => l.TaskId is not null && l.NextAttemptUtc is not null)
                        .Select(l => (Link: l, Due: attemptedLinks.TryGetValue(l.Id, out var attempted) && attempted > l.NextAttemptUtc!.Value ? attempted : l.NextAttemptUtc!.Value))
                        .OrderBy(x => x.Due).ThenBy(x => x.Link.Id, StringComparer.Ordinal).FirstOrDefault();
                    var due = retry.Link is not null && retry.Due < nextFull ? retry.Due : nextFull;
                    if (state.ConnectionRetryUtc is { } backoff && backoff > due) due = backoff;
                    if (connection.RateLimitResetUtc is { } reset && reset > due) due = reset;
                    if (due > now)
                    {
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        var timer = Task.Delay(due - now, clock, wait.Token);
                        var signal = _changed.WaitAsync(wait.Token);
                        var completed = await Task.WhenAny(timer, signal);
                        await completed;
                        await wait.CancelAsync();
                        continue;
                    }
                    if (retry.Link is not null && retry.Due <= now && retry.Due < nextFull)
                    {
                        // If a concurrent change prevents a retry from updating its row, avoid a tight loop.
                        attemptedLinks[retry.Link.Id] = now.AddSeconds(10);
                        engine.RequestTaskSync(retry.Link.TaskId!, immediate: true);
                        await engine.WaitForIdleAsync(_stop.Token);
                        continue;
                    }
                    try { engine.RequestSync(null, connection.Id); }
                    catch (AppException)
                    {
                        // Replacement or connection switching can temporarily prevent a pass.
                        nextFull = clock.GetUtcNow().AddSeconds(10);
                        continue;
                    }
                    await engine.WaitForIdleAsync(_stop.Token);
                    var activity = engine.Status;
                    if (activity.ConnectionId != connection.Id || activity.AppError is not null || activity.GitHubError is not null)
                    {
                        failures = Math.Min(failures + 1, 6);
                        nextFull = clock.GetUtcNow().AddMinutes(Math.Min(30, 1 << (failures - 1)));
                    }
                    else
                    {
                        failures = 0;
                        nextFull = clock.GetUtcNow().AddSeconds(activity.Result?.MorePages == true ? 10 : 300);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceWarning("GitHub scheduler could not process local state: {0}", error.GetType().Name);
                    failures = Math.Min(failures + 1, 6);
                    await Task.Delay(TimeSpan.FromMinutes(Math.Min(30, 1 << (failures - 1))), clock, _stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { System.Diagnostics.Trace.TraceInformation("GitHub scheduler stopped."); }
    }

    public void Dispose()
    {
        engine.WorkCompleted -= Signal;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _stop.Cancel();
        _loop?.GetAwaiter().GetResult();
        _changed.Dispose();
        _stop.Dispose();
    }
}
