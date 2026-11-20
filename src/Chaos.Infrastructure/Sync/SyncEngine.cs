using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>Timings of the sync loop (PLAN2 P13 item 2). Tests shorten them.</summary>
public sealed class SyncEngineOptions
{
    /// <summary>A local change waits this long for more, so a burst of edits is one push.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>With nothing to wake it, the loop pulls this often.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Remote changes applied per transaction; local writes interleave between them (PLAN2 §7.4).</summary>
    public int ApplyBatch { get; init; } = 200;

    /// <summary>A change parked this many pull cycles becomes an attention item (PLAN2 §7.8).</summary>
    public int ParkedAttentionCycles { get; init; } = 10;

    /// <summary>A wake from the service's event stream pulls after this long, so a burst of pushes is one pull (PLAN2 7.12: 300 ms).</summary>
    public TimeSpan PullDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The service sends a keep-alive every 25 s; a stream silent for this long is dead and is reopened.</summary>
    public TimeSpan EventSilenceLimit { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>While sync is off the event listener looks again this often to see whether it was turned on.</summary>
    public TimeSpan EventIdleCheck { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>With no membership event, the readable stream set is asked for again this often (PLAN3 §7.7).</summary>
    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A change refused with <c>parent-unknown</c> this many cycles in a row becomes an attention item (PLAN3 §7.9).</summary>
    public int ParentUnknownAttentionCycles { get; init; } = 10;

    /// <summary>Jitter for the backoff, a fraction between 0 and 1. Tests fix it.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}

/// <summary>What the engine is doing right now, for the status indicator (PLAN2 P15 item 3).</summary>
public sealed record SyncRuntime(bool Syncing, ServiceFailure? Failure, bool UpdateNeeded, bool Streaming, DateTimeOffset? LastSuccessAt = null, string? FailureDetail = null);

/// <summary>What one sync cycle did.</summary>
public sealed record SyncCycleResult(bool Ran, long Pushed, long Pulled, bool SnapshotUsed, string? Problem);

/// <summary>
/// The client sync loop (PLAN2 P13 item 2, PLAN3 Q4). It runs only while <c>SyncState.Enabled = 1</c>: push the
/// outbox in batches, oldest first, and delete what the service acknowledged (the service routes each change to its
/// stream); refresh the readable streams when membership changed, evict the ones that went away, then pull every
/// readable stream, the account stream first, through <see cref="SyncStreamPuller"/>; then wait for a local
/// change (debounced), a wake from the service's event stream (P15), or the poll interval. A network
/// error backs off from 2 s to 5 min with jitter and tries again at once when something wakes it. The
/// loop never blocks the UI and never holds the writer while waiting on the network: every database
/// step is a short store call between the HTTP calls.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const int PushByteBudget = SyncProtocol.MaxPushBytes - 64 * 1024;

    private readonly SqliteApplicationStore _store;
    private readonly SyncApplier _applier;
    private readonly SyncAccountState _account;
    private readonly SyncStreamDiscovery _discovery;
    private readonly SyncStreamPuller _puller;
    private readonly SyncEviction _eviction;
    private readonly Dictionary<string, int> _parentUnknown = new(StringComparer.OrdinalIgnoreCase);
    private bool _discoveryDue = true;
    private long _membershipVersionSeen = -1;
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
    private readonly IChaosServiceClient _service;
    private readonly TimeProvider _clock;
    private readonly SyncEngineOptions _options;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly HashSet<string> _refused = [];
    private readonly Lock _wakeGate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _pushBlocked;
    private bool _localWake;
    private Task? _events;
    private SyncRuntime _runtime = new(false, null, false, false);

    public SyncEngine(SqliteApplicationStore store, SyncApplier applier, IChaosServiceClient service, TimeProvider clock, SyncEngineOptions? options = null)
    {
        _store = store;
        _applier = applier;
        _account = new SyncAccountState(store);
        _service = service;
        _clock = clock;
        _options = options ?? new SyncEngineOptions();
        _discovery = new SyncStreamDiscovery(service, new SyncStreamRegistry(store));
        _puller = new SyncStreamPuller(store, applier, service, _options.ApplyBatch);
        _eviction = new SyncEviction(store, clock);
        if (_store.SyncFlush is { } flush)
        {
            flush.OutboxWritten += Wake;
        }
    }

    /// <summary>Raised after every cycle with what it did; Settings and tests listen.</summary>
    public event Action<SyncCycleResult>? CycleFinished;

    /// <summary>Starts the background loop. Calling it twice does nothing.</summary>
    public void Start()
    {
        lock (_wakeGate)
        {
            if (_loop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_stop.Token));
            _events = Task.Run(() => RunEventsAsync(_stop.Token));
        }
    }

    /// <summary>The engine's live state. Never touches the database or the network.</summary>
    public SyncRuntime Runtime
    {
        get
        {
            lock (_wakeGate)
            {
                return _runtime;
            }
        }
    }

    /// <summary>Raised whenever <see cref="Runtime"/> changes.</summary>
    public event Action<SyncRuntime>? RuntimeChanged;

    private void SetRuntime(Func<SyncRuntime, SyncRuntime> change)
    {
        SyncRuntime next;
        lock (_wakeGate)
        {
            next = change(_runtime);
            if (next == _runtime)
            {
                return;
            }

            _runtime = next;
        }

        RuntimeChanged?.Invoke(next);
    }

    /// <summary>A local change happened (or the person asked to sync now): run a cycle after the debounce.</summary>
    public void Wake()
    {
        lock (_wakeGate)
        {
            _localWake = true;
            _wake.TrySetResult();
        }
    }

    /// <summary>The service says the sequence advanced (the event stream): pull after the short debounce.</summary>
    public void RequestPull()
    {
        lock (_wakeGate)
        {
            _wake.TrySetResult();
        }
    }

    /// <summary>The service says this account's readable streams or roles changed: discover again, then pull.</summary>
    public void RequestDiscovery()
    {
        lock (_wakeGate)
        {
            _discoveryDue = true;
            _wake.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (_store.SyncFlush is { } flush)
        {
            flush.OutboxWritten -= Wake;
        }

        Task? loop;
        lock (_wakeGate)
        {
            loop = _loop;
            _stop?.Cancel();
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5));
            _events?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException error)
        {
            // The loop ends by cancellation.
            System.Diagnostics.Trace.TraceInformation("Sync background work stopped during disposal: {0}", error.GetType().Name);
        }

        // _cycleGate is not disposed: a SyncNowAsync from outside may still be waiting on it, and a SemaphoreSlim holds no handle to free.
        _stop?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            SyncCycleResult result;
            try
            {
                result = await SyncNowAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Nothing may end this loop: report the problem and back off like any other failed cycle.
                result = ReportUnexpected(error);
            }

            TimeSpan wait;
            if (result.Problem is not null && result.Ran)
            {
                failures++;
                wait = Backoff(failures);
            }
            else
            {
                failures = 0;
                wait = _options.PollInterval;
            }

            try
            {
                await WaitForWakeAsync(wait, afterFailure: failures > 0, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>2 s, 4 s, 8 s … capped at 5 min, with jitter so a fleet does not retry in step.</summary>
    private TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(_options.MaxBackoff.TotalSeconds, _options.MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 20)));
        var jittered = seconds * (0.5 + 0.5 * Math.Clamp(_options.Jitter(), 0, 1));
        return TimeSpan.FromSeconds(Math.Max(jittered, _options.MinBackoff.TotalSeconds / 2));
    }

    private async Task WaitForWakeAsync(TimeSpan wait, bool afterFailure, CancellationToken ct)
    {
        // A wake that arrived while the last cycle was running is still pending here, so it is not lost.
        Task woken;
        lock (_wakeGate)
        {
            woken = _wake.Task;
        }

        var timeout = Task.Delay(wait, _clock, ct);
        var first = await Task.WhenAny(woken, timeout);
        ct.ThrowIfCancellationRequested();
        if (first == woken)
        {
            bool local;
            lock (_wakeGate)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                local = _localWake;
                _localWake = false;
            }

            // Debounce: more edits within the window ride along with this cycle. After an error the
            // wake means something changed (connectivity came back), so it goes at once.
            if (!afterFailure)
            {
                await Task.Delay(local ? _options.Debounce : _options.PullDebounce, _clock, ct);
            }
        }
    }

    /// <summary>One push-then-pull cycle. Never throws for a service or network problem: it reports it in the result.</summary>
    public async Task<SyncCycleResult> SyncNowAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct);
        try
        {
            // An eviction a crash interrupted is finished before anything else, whether or not sync is still on (§7.7 step 4).
            await _eviction.EvictPendingAsync(ct);
            var state = await ReadStateAsync(ct);
            if (!state.Enabled)
            {
                return Finish(new SyncCycleResult(false, 0, 0, false, null));
            }

            long pushed = 0, pulled = 0;
            var snapshot = false;
            SetRuntime(r => r with { Syncing = true });
            try
            {
                if (!_pushBlocked)
                {
                    pushed = await PushAsync(ct);
                }

                (pulled, snapshot) = await PullStreamsAsync(state.AccountId, ct);
                await RaiseParkedAttentionAsync(ct);

                // A device that just joined first took what the account has (above); only now does the rest of its data go up
                // (PLAN2 §7.13), so its older values never overwrite the account's newer ones. The flag survives a restart.
                if (await _account.BackfillPendingAsync(ct))
                {
                    await _account.BackfillAsync(ct);
                    pushed += await PushAsync(ct);
                }

                await UploadCalendarCachesAsync(ct);
                await RecordAsync(error: null, pushed > 0, pulled > 0 || snapshot, ct);
                var finished = _clock.GetUtcNow();
                SetRuntime(r => r with { Failure = null, FailureDetail = null, LastSuccessAt = finished });
                return Finish(new SyncCycleResult(true, pushed, pulled, snapshot, null));
            }
            catch (ChaosServiceException error) when (error.Kind is ServiceFailure.DeviceRevoked or ServiceFailure.AccountDeleted)
            {
                // The device was removed or the account deleted: offline-only from here on, with all local data kept (PLAN2 §8.6, P14 item 3).
                await _account.DisableAsync(ct);
                SetRuntime(r => r with { Failure = null });
                return Finish(new SyncCycleResult(true, pushed, pulled, snapshot, error.Message));
            }
            catch (ChaosServiceException error)
            {
                var problem = error.Kind switch
                {
                    ServiceFailure.SignInAgain => "Sign in again to keep syncing.",
                    ServiceFailure.Offline => "Offline. Changes will sync when the service is reachable.",
                    _ => error.Message,
                };
                await RecordAsync(problem, false, false, ct);
                SetRuntime(r => r with { Failure = error.Kind, FailureDetail = problem });

                // Signed out or refused is not something a retry fixes; only connectivity is.
                return Finish(new SyncCycleResult(true, pushed, pulled, snapshot, problem));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A malformed response or a local failure is a problem to show and retry, not a reason to stop syncing.
                return ReportUnexpected(error, pushed, pulled, snapshot);
            }
        }
        finally
        {
            SetRuntime(r => r with { Syncing = false, UpdateNeeded = _pushBlocked });
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// Holds the service's event stream open while sync is on and turns each seq event into a pull (PLAN2 P15 item 2).
    /// It reconnects with the same backoff as the sync loop; polling every 60 s still covers a stream that never connects.
    /// </summary>
    private async Task RunEventsAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!(await ReadStateAsync(ct)).Enabled)
                {
                    await Task.Delay(_options.EventIdleCheck, _clock, ct);
                    continue;
                }

                using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct);
                silence.CancelAfter(_options.EventSilenceLimit);
                await foreach (var line in _service.StreamLinesAsync("v2/sync/events", silence.Token))
                {
                    silence.CancelAfter(_options.EventSilenceLimit);
                    SetRuntime(r => r with { Streaming = true });
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        failures = 0;
                        if (MembershipVersion(line) is { } version && version != Interlocked.Read(ref _membershipVersionSeen))
                        {
                            RequestDiscovery();
                        }
                        else
                        {
                            RequestPull();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException error)
            {
                // Silent too long: reopen.
                System.Diagnostics.Trace.TraceInformation("Sync event stream timed out and will reconnect: {0}", error.GetType().Name);
            }
            catch (ChaosServiceException error) when (error.Kind is ServiceFailure.DeviceRevoked or ServiceFailure.AccountDeleted)
            {
                await _account.DisableAsync(ct);
            }
            catch (ChaosServiceException error)
            {
                // Offline, signed out or refused: back off and try again.
                System.Diagnostics.Trace.TraceInformation("Sync event stream will retry after service failure ({0}).", error.Kind);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The stream must keep reconnecting whatever went wrong with it.
                System.Diagnostics.Trace.TraceWarning("Sync event stream failed: {0}", error.GetType().Name);
            }

            SetRuntime(r => r with { Streaming = false });
            failures++;
            try
            {
                await Task.Delay(Backoff(failures), _clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>The version of a <c>{"membership":N}</c> event, or null for any other event (§7.8).</summary>
    private static long? MembershipVersion(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line.AsMemory("data:".Length));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("membership", out var membership)
                && membership.TryGetInt64(out var version) ? version : null;
        }
        catch (JsonException error)
        {
            System.Diagnostics.Trace.TraceInformation("Sync event was not JSON: {0}", error.GetType().Name);
            return null;
        }
    }

    private SyncCycleResult ReportUnexpected(Exception error, long pushed = 0, long pulled = 0, bool snapshot = false)
    {
        System.Diagnostics.Trace.TraceWarning("Sync cycle failed unexpectedly: {0}", error);
        var problem = "Sync hit an unexpected problem and will try again.";
        SetRuntime(r => r with { Failure = ServiceFailure.Other, FailureDetail = problem });
        return Finish(new SyncCycleResult(true, pushed, pulled, snapshot, problem));
    }

    private SyncCycleResult Finish(SyncCycleResult result)
    {
        CycleFinished?.Invoke(result);
        return result;
    }

    // ---- State ---------------------------------------------------------------------------------------

    private sealed record State(bool Enabled, Guid? AccountId);

    private Task<State> ReadStateAsync(CancellationToken ct) =>
        _store.QueryAsync((session, _) =>
        {
            var connection = session.Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Enabled, AccountId FROM SyncState WHERE Id = 1;";
            using var reader = command.ExecuteReader();
            return Task.FromResult(reader.Read()
                ? new State(reader.GetInt64(0) == 1, !reader.IsDBNull(1) && Guid.TryParse(reader.GetString(1), out var account) ? account : null)
                : new State(false, null));
        }, ct);

    private Task RecordAsync(string? error, bool pushed, bool pulled, CancellationToken ct) =>
        _store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            using var command = connection.CreateCommand();
            var now = StorageFormats.Instant(_clock.GetUtcNow());
            command.CommandText = $"""
                UPDATE SyncState SET LastError = $error
                    {(pushed ? ", LastPushUtc = $now" : string.Empty)}
                    {(pulled ? ", LastPullUtc = $now" : string.Empty)}
                WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);

    private async Task UploadCalendarCachesAsync(CancellationToken ct)
    {
        var caches = await _store.QueryAsync((session, _) => Task.FromResult(((SqliteCalendarStore)session.Calendars).ReadCacheSnapshots()), ct);
        foreach (var cache in caches)
        {
            var json = JsonSerializer.Serialize(cache);
            var response = await _service.CallAsync(HttpMethod.Post, "v1/sync/calendar-cache", json, ct);
            if (!response.Ok)
            {
                var detail = response.Status == 404
                    ? "The online service does not support calendar sync yet. Update or restart the server."
                    : ProblemText(response);
                throw new ChaosServiceException(ServiceFailure.Refused, detail);
            }
        }
    }

    // ---- Push ----------------------------------------------------------------------------------------

    private sealed record OutboxRow(long Seq, string ChangeId, string Kind, string EntityKey, string Payload);

    private async Task<long> PushAsync(CancellationToken ct)
    {
        long total = 0;
        // Only what was queued when this cycle began: edits made meanwhile wait for the next cycle, so a busy writer cannot keep a push going forever.
        var through = await _store.QueryAsync((session, _) => Task.FromResult(SyncSql.Scalar(session.Connection(), "SELECT COALESCE(MAX(Seq), 0) FROM SyncOutbox;")), ct);
        while (true)
        {
            var batch = await ReadOutboxBatchAsync(through, ct);
            if (batch.Count == 0)
            {
                return total;
            }

            var body = new StringBuilder("{\"changes\":[");
            for (var index = 0; index < batch.Count; index++)
            {
                body.Append(index == 0 ? string.Empty : ",").Append(batch[index].Payload);
            }

            body.Append("]}");

            var response = await _service.CallAsync(HttpMethod.Post, "v2/sync/push", body.ToString(), ct);
            if (!response.Ok)
            {
                throw new ChaosServiceException(ServiceFailure.Refused, ProblemText(response));
            }

            var (accepted, rejected) = ReadPushAnswer(response.Body);
            var byId = batch.ToDictionary(row => row.ChangeId, StringComparer.OrdinalIgnoreCase);
            var settled = new List<string>(accepted);
            var retryLater = 0;
            foreach (var changeId in accepted)
            {
                _parentUnknown.Remove(changeId);
            }

            foreach (var (changeId, reason) in rejected)
            {
                byId.TryGetValue(changeId, out var row);
                switch (reason)
                {
                    case SyncRejection.Purged:
                        settled.Add(changeId); // The entity was purged elsewhere; the edit has nothing left to apply to.
                        continue;
                    case SyncRejection.Forbidden or SyncRejection.ServerOnly:
                        // The UI let through something this member may not change: drop it and say so (§7.9).
                        settled.Add(changeId);
                        _parentUnknown.Remove(changeId);
                        await RaiseRejectionAsync(row, reason, ct);
                        continue;
                    case SyncRejection.NotMember:
                        // The space went away: discovery evicts it, and with it every unsent change that would route there.
                        lock (_wakeGate)
                        {
                            _discoveryDue = true;
                        }

                        _refused.Add(changeId);
                        retryLater++;
                        continue;
                    case SyncRejection.ParentUnknown:
                        // Outbox order puts parents first; the parent is pushed or pulled by a later cycle.
                        var cycles = _parentUnknown[changeId] = _parentUnknown.GetValueOrDefault(changeId) + 1;
                        if (cycles >= _options.ParentUnknownAttentionCycles)
                        {
                            await RaiseAttentionAsync("sync.parent-unknown", row?.Kind, row?.EntityKey,
                                "Some changes are waiting for an item that has not reached the online service.", ct);
                        }

                        _refused.Add(changeId);
                        retryLater++;
                        continue;
                    case SyncRejection.Moved:
                        // The item moved to another stream; this cycle's pull of the account stream catches up, then the next one retries.
                        _refused.Add(changeId);
                        retryLater++;
                        continue;
                    case SyncRejection.RateLimited:
                        await DeleteOutboxAsync(settled, ct);
                        throw new ChaosServiceException(ServiceFailure.Refused, "The online service asked Chaos to slow down. Sync will try again shortly.");
                    case SyncRejection.SchemaTooOld:
                        _pushBlocked = true;
                        break;
                }

                _refused.Add(changeId);
                await RaiseRejectionAsync(row, reason, ct);
            }

            await DeleteOutboxAsync(settled, ct);
            total += accepted.Count;
            if (_pushBlocked)
            {
                return total;
            }

            if (accepted.Count == 0 && (rejected.Count == 0 || retryLater == rejected.Count))
            {
                return total; // Nothing was settled; leave it for the next cycle rather than spin.
            }
        }
    }

    private Task<List<OutboxRow>> ReadOutboxBatchAsync(long through, CancellationToken ct) =>
        _store.QueryAsync((session, _) =>
        {
            var connection = session.Connection();
            var rows = new List<OutboxRow>();
            var bytes = 0;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Seq, ChangeId, Kind, EntityKey, PayloadJson FROM SyncOutbox WHERE Seq <= $through ORDER BY Seq;";
            command.Parameters.AddWithValue("$through", through);
            using var reader = command.ExecuteReader();
            while (reader.Read() && rows.Count < SyncProtocol.MaxPushChanges)
            {
                var changeId = reader.GetString(1);
                if (_refused.Contains(changeId))
                {
                    continue;
                }

                var payload = reader.GetString(4);
                var size = Encoding.UTF8.GetByteCount(payload) + 1;
                if (rows.Count > 0 && bytes + size > PushByteBudget)
                {
                    break;
                }

                bytes += size;
                rows.Add(new OutboxRow(reader.GetInt64(0), changeId, reader.GetString(2), reader.GetString(3), payload));
            }

            return Task.FromResult(rows);
        }, ct);

    private Task DeleteOutboxAsync(IReadOnlyList<string> changeIds, CancellationToken ct)
    {
        if (changeIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            using var command = connection.CreateCommand();
            var names = new List<string>();
            for (var index = 0; index < changeIds.Count; index++)
            {
                names.Add($"$id{index}");
                command.Parameters.AddWithValue($"$id{index}", changeIds[index]);
            }

            command.CommandText = $"DELETE FROM SyncOutbox WHERE ChangeId COLLATE NOCASE IN ({string.Join(", ", names)});";
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
    }

    private static (List<string> Accepted, List<(string ChangeId, string Reason)> Rejected) ReadPushAnswer(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        // Protocol 2 names the stream each change went to; the client needs only the id.
        var accepted = root.GetProperty("accepted").EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetProperty("changeId").GetString()!).ToList();
        var rejected = root.GetProperty("rejected").EnumerateArray()
            .Select(item => (item.GetProperty("changeId").GetString()!, item.GetProperty("reason").GetString() ?? SyncRejection.Invalid))
            .ToList();
        return (accepted, rejected);
    }

    private Task RaiseRejectionAsync(OutboxRow? row, string reason, CancellationToken ct)
    {
        var message = reason switch
        {
            SyncRejection.SchemaTooOld => "Update Chaos to keep syncing.",
            SyncRejection.Quota => "Your online storage is full, so some changes were not uploaded.",
            SyncRejection.TooLarge => "A change was too large for the online service and was not uploaded.",
            SyncRejection.NotOwner => "The online service refused a change that is not in your account.",
            SyncRejection.UnknownKind => "The online service does not know a kind of change this version of Chaos made. Update Chaos.",
            SyncRejection.Forbidden or SyncRejection.ServerOnly => "You can't change this in a shared space, so the change was not uploaded.",
            _ => "The online service rejected a change.",
        };
        return RaiseAttentionAsync("sync.rejected", row?.Kind, row?.EntityKey, message, ct);
    }

    // ---- Streams and pull ---------------------------------------------------------------------------

    /// <summary>
    /// Asks for the readable streams when membership changed or the interval passed, evicts what went away, then pulls every
    /// readable stream until it is quiet, the account stream first (§7.5). A stream the service no longer lets this device
    /// read makes the cycle discover again at once, which evicts it.
    /// </summary>
    private async Task<(long Pulled, bool Snapshot)> PullStreamsAsync(Guid? accountId, CancellationToken ct)
    {
        if (accountId is not { } account)
        {
            throw new ChaosServiceException(ServiceFailure.Other, "This computer is not linked to an online account yet.");
        }

        long pulled = 0;
        var snapshot = false;
        for (var round = 0; round < 2; round++)
        {
            await DiscoverIfDueAsync(account, ct);
            var lost = false;
            foreach (var stream in await ReadableStreamsAsync(ct))
            {
                try
                {
                    var (count, snapshotted) = await _puller.PullAsync(stream, ct);
                    pulled += count;
                    snapshot |= snapshotted;
                }
                catch (SyncStreamUnreadableException)
                {
                    lost = true;
                }
            }

            if (!lost)
            {
                break;
            }

            lock (_wakeGate)
            {
                _discoveryDue = true;
            }
        }

        return (pulled, snapshot);
    }

    private async Task DiscoverIfDueAsync(Guid accountId, CancellationToken ct)
    {
        bool due;
        lock (_wakeGate)
        {
            due = _discoveryDue || _clock.GetUtcNow() - _lastDiscovery >= _options.DiscoveryInterval;
            _discoveryDue = false;
        }

        if (!due && await _store.QueryAsync((session, _) => Task.FromResult(
                SyncSql.Scalar(session.Connection(), "SELECT count(*) FROM SyncStreams WHERE Kind = 'account';") == 1), ct))
        {
            return;
        }

        try
        {
            var version = await _discovery.RefreshAsync(accountId, ct);
            Interlocked.Exchange(ref _membershipVersionSeen, version);
            _lastDiscovery = _clock.GetUtcNow();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (_wakeGate)
            {
                _discoveryDue = true;
            }

            throw;
        }

        if ((await _eviction.EvictPendingAsync(ct)).Count > 0)
        {
            // The eviction removed the unsent changes that would have routed there; nothing refused needs remembering.
            _refused.Clear();
        }
    }

    private Task<List<Guid>> ReadableStreamsAsync(CancellationToken ct) =>
        _store.QueryAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT StreamId FROM SyncStreams WHERE State IN ('joining', 'active') ORDER BY Kind = 'account' DESC, StreamId;";
            using var reader = command.ExecuteReader();
            var streams = new List<Guid>();
            while (reader.Read())
            {
                streams.Add(Guid.Parse(reader.GetString(0)));
            }

            return Task.FromResult(streams);
        }, ct);

    // ---- Attention -----------------------------------------------------------------------------------

    private Task RaiseParkedAttentionAsync(CancellationToken ct) =>
        _store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            var parked = new List<(string ChangeId, string Kind, string Key)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT ChangeId, Kind, EntityKey FROM SyncInboxParked WHERE PullCycles >= $cycles;";
                select.Parameters.AddWithValue("$cycles", _options.ParkedAttentionCycles);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    parked.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            foreach (var (_, kind, key) in parked)
            {
                if (!OpenAttentionExists(connection, "sync.parked", key))
                {
                    InsertAttention(connection, "sync.parked", kind, key, "Some changes could not be applied.");
                }
            }

            return Task.FromResult(0);
        }, ct);

    private Task RaiseAttentionAsync(string category, string? kind, string? key, string message, CancellationToken ct) =>
        _store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            if (!OpenAttentionExists(connection, category, key ?? message))
            {
                InsertAttention(connection, category, kind, key ?? message, message);
            }

            return Task.FromResult(0);
        }, ct);

    private static bool OpenAttentionExists(SqliteConnection connection, string category, string entityKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SyncAttention WHERE Category = $category AND EntityKey = $key AND ResolvedAtUtc IS NULL LIMIT 1;";
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$key", entityKey);
        return command.ExecuteScalar() is not null;
    }

    private void InsertAttention(SqliteConnection connection, string category, string? kind, string key, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncAttention (Id, Category, Kind, EntityKey, Message, DetailJson, CreatedAtUtc)
            VALUES ($id, $category, $kind, $key, $message, NULL, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(_clock.GetUtcNow()));
        command.ExecuteNonQuery();
    }

    private static string ProblemText(ServiceResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 and <= 200 } text)
            {
                return text;
            }
        }
        catch (JsonException error)
        {
            // Not a problem document: fall through to the status.
            System.Diagnostics.Trace.TraceInformation("Online service error response was not a problem document: {0}", error.GetType().Name);
        }

        return $"The online service answered with an error ({response.Status}).";
    }
}
