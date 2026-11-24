using System.Net.NetworkInformation;
using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;

namespace Chaos.Desktop.Platform;

/// <summary>
/// Schedules serialized full passes and persisted per-issue retries, for every connection this device runs (the
/// Personal space's and each shared space's), each on its own cadence.
/// </summary>
public sealed class GitHubSyncScheduler(GitHubSyncEngine engine, Func<CancellationToken, Task<IReadOnlyList<GitHubSchedule>>> schedule, TimeProvider clock) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Dictionary<string, (DateTimeOffset NextFull, int Failures)> _cadence = new(StringComparer.Ordinal);
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
        var attemptedLinks = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var failures = 0;
        try
        {
            while (true)
            {
                try
                {
                    var states = (await schedule(_stop.Token)).Where(state => state.Status is { IsRunner: true } status
                        && status.Connection is { State: 0, DeletedAtUtc: null } && status.Repositories.Any(r => r.State == 0 && r.DeletedAtUtc is null)).ToArray();
                    var now = clock.GetUtcNow();
                    if (states.Length == 0)
                    {
                        attemptedLinks.Clear();
                        _cadence.Clear();
                        await WaitForChangeAsync(TimeSpan.FromSeconds(30));
                        continue;
                    }
                    foreach (var id in attemptedLinks.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray()) attemptedLinks.Remove(id);
                    var forced = Interlocked.Exchange(ref _force, 0) != 0;
                    if (forced) attemptedLinks.Clear();
                    // A connection seen for the first time waits ten seconds, as the first pass after start always did; a wake
                    // (a local action, a new repository, connectivity back) makes every full pass due now.
                    foreach (var state in states)
                    {
                        var id = state.Status.Connection!.Id;
                        if (_cadence.TryGetValue(id, out var cadence))
                        {
                            if (forced) _cadence[id] = (now, cadence.Failures);
                        }
                        else _cadence[id] = (forced ? now : now.AddSeconds(10), 0);
                    }
                    // The connection whose next work is due first goes first: its full pass, a due link retry, or a backoff.
                    var plan = states.Select(state =>
                    {
                        var connection = state.Status.Connection!;
                        var nextFull = _cadence[connection.Id].NextFull;
                        var retry = state.LinkRetries.Where(l => l.TaskId is not null && l.NextAttemptUtc is not null)
                            .Select(l => (Link: l, Due: attemptedLinks.TryGetValue(l.Id, out var attempted) && attempted > l.NextAttemptUtc!.Value ? attempted : l.NextAttemptUtc!.Value))
                            .OrderBy(x => x.Due).ThenBy(x => x.Link.Id, StringComparer.Ordinal).FirstOrDefault();
                        var due = retry.Link is not null && retry.Due < nextFull ? retry.Due : nextFull;
                        if (state.ConnectionRetryUtc is { } backoff && backoff > due) due = backoff;
                        if (connection.RateLimitResetUtc is { } reset && reset > due) due = reset;
                        return (State: state, Retry: retry, Due: due);
                    }).OrderBy(entry => entry.Due).ThenBy(entry => entry.State.Status.Connection!.Id, StringComparer.Ordinal).First();
                    if (plan.Due > now)
                    {
                        await WaitForChangeAsync(plan.Due - now);
                        continue;
                    }
                    var current = plan.State.Status.Connection!;
                    if (plan.Retry.Link is not null && plan.Retry.Due <= now && plan.Retry.Due < _cadence[current.Id].NextFull)
                    {
                        // If a concurrent change prevents a retry from updating its row, avoid a tight loop.
                        attemptedLinks[plan.Retry.Link.Id] = now.AddSeconds(10);
                        engine.RequestTaskSync(plan.Retry.Link.TaskId!, immediate: true);
                        await engine.WaitForIdleAsync(_stop.Token);
                        continue;
                    }
                    try { engine.RequestSync(null, current.Id); }
                    catch (AppException)
                    {
                        // Replacement or another connection's pass can temporarily prevent this one.
                        _cadence[current.Id] = (clock.GetUtcNow().AddSeconds(10), 0);
                        continue;
                    }
                    await engine.WaitForIdleAsync(_stop.Token);
                    var activity = engine.Status;
                    failures = 0;
                    var connectionFailures = _cadence.TryGetValue(current.Id, out var prior) ? prior.Failures : 0;
                    if (activity.ConnectionId != current.Id || activity.AppError is not null || activity.GitHubError is not null)
                    {
                        connectionFailures = Math.Min(connectionFailures + 1, 6);
                        _cadence[current.Id] = (clock.GetUtcNow().AddMinutes(Math.Min(30, 1 << (connectionFailures - 1))), connectionFailures);
                    }
                    else
                    {
                        _cadence[current.Id] = (clock.GetUtcNow().AddSeconds(activity.Result?.MorePages == true ? 10 : 300), 0);
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

    private async Task WaitForChangeAsync(TimeSpan atMost)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var timer = Task.Delay(atMost, clock, wait.Token);
        var signal = _changed.WaitAsync(wait.Token);
        await await Task.WhenAny(timer, signal);
        await wait.CancelAsync();
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
