using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

public sealed class SyncStreamDiscoveryTests
{
    private sealed class Service(ServiceResponse response) : IChaosServiceClient
    {
        public string? Path { get; private set; }
        public bool IsConfigured => true;
        public Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? json, CancellationToken ct) { Path = path; return Task.FromResult(response); }
        public Task<ServiceSession> GetSessionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> SignInAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> ReauthenticateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Bound, AccountCanonicalIds Canonical)> BindAccountAsync(Guid profileId, Guid personalSpaceId, Guid inboxId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAccountAsync(string confirmation, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamLinesAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task RefreshAsync_ValidList_StoresJoiningStream()
    {
        using var host = await DataHost.StartAsync();
        var account = Guid.NewGuid();
        var space = Guid.NewGuid();
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE SyncState SET AccountId=$account WHERE Id=1;";
            command.Parameters.AddWithValue("$account", account.ToString("D"));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var service = new Service(new(200, $$"""{"membershipVersion":7,"streams":[{"id":"{{account:D}}","kind":1,"spaceId":null,"role":null},{"id":"{{space:D}}","kind":2,"spaceId":"{{space:D}}","role":2}]}"""));
        var discovery = new SyncStreamDiscovery(service, new SyncStreamRegistry(host.Store));
        await Assert.That(await discovery.RefreshAsync(account)).IsEqualTo(7L);
        await Assert.That(service.Path).IsEqualTo("v2/streams");
        await Assert.That(await TextAsync(host, $"SELECT State FROM SyncStreams WHERE StreamId='{space:D}';")).IsEqualTo("joining");
    }

    [Test]
    public async Task RefreshAsync_MalformedList_DoesNotEvictExistingStreams()
    {
        using var host = await DataHost.StartAsync();
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO SyncStreams (StreamId,Kind,SpaceId,Role,Cursor,State) VALUES ('existing','space','existing',1,4,'active');";
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var discovery = new SyncStreamDiscovery(new Service(new(200, "{\"membershipVersion\":0,\"streams\":[{}]}")), new SyncStreamRegistry(host.Store));
        await Assert.That(async () => await discovery.RefreshAsync(Guid.NewGuid())).Throws<ChaosServiceException>();
        await Assert.That(await TextAsync(host, "SELECT State FROM SyncStreams WHERE StreamId='existing';")).IsEqualTo("active");
    }
}
