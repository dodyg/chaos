using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>An unexpected exception from the service layer (not a <see cref="ChaosServiceException"/>) must not end the sync loop.</summary>
public sealed class SyncEngineResilienceTests
{
    /// <summary>Every call fails with an exception type the engine does not special-case, and is counted.</summary>
    private sealed class BrokenService(Func<Exception> error) : IChaosServiceClient
    {
        public int Calls;
        public TaskCompletionSource ThirdCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConfigured => true;
        public Task<ServiceSession> GetSessionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> SignInAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> ReauthenticateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Bound, AccountCanonicalIds Canonical)> BindAccountAsync(Guid profileId, Guid personalSpaceId, Guid inboxId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAccountAsync(string confirmation, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? json, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) >= 3)
            {
                ThirdCall.TrySetResult();
            }

            throw error();
        }

        public async IAsyncEnumerable<string> StreamLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), stopped);
            await stopped.Task;
            ct.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private static SyncEngineOptions Fast() => new()
    {
        Debounce = TimeSpan.FromMilliseconds(5),
        PollInterval = TimeSpan.FromMilliseconds(20),
        MinBackoff = TimeSpan.FromMilliseconds(10),
        MaxBackoff = TimeSpan.FromMilliseconds(20),
        Jitter = () => 0.5,
    };

    [Test]
    public async Task A_cycle_that_hits_an_unexpected_exception_reports_a_problem_instead_of_throwing()
    {
        using var host = await DataHost.StartAsync();
        await EnableSyncAsync(host);
        var service = new BrokenService(() => new System.Text.Json.JsonException("not json"));
        using var engine = new SyncEngine(host.Store, new SyncApplier(host.Store, host.Clock, host.Ids), service, TimeProvider.System, Fast());

        var result = await engine.SyncNowAsync(CancellationToken.None);

        await Assert.That(result.Ran).IsTrue();
        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(service.Calls).IsGreaterThan(0);
    }

    [Test]
    public async Task The_background_loop_keeps_retrying_after_unexpected_exceptions()
    {
        using var host = await DataHost.StartAsync();
        await EnableSyncAsync(host);
        var service = new BrokenService(() => new KeyNotFoundException("missing"));
        using var engine = new SyncEngine(host.Store, new SyncApplier(host.Store, host.Clock, host.Ids), service, TimeProvider.System, Fast());

        engine.Start();
        await service.ThirdCall.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(Volatile.Read(ref service.Calls)).IsGreaterThanOrEqualTo(3);
    }
}
