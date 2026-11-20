using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

public sealed class SyncStreamPullerTests
{
    private sealed class Service(Func<string, ServiceResponse> answer) : IChaosServiceClient
    {
        public List<string> Paths { get; } = [];
        public bool IsConfigured => true;
        public Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? json, CancellationToken ct) { Paths.Add(path); return Task.FromResult(answer(path)); }
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

    private static async Task AddStreamAsync(DataHost host, Guid stream, string state)
    {
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO SyncStreams (StreamId,Kind,SpaceId,Role,Cursor,State) VALUES ($id,'space',$id,2,0,$state);";
            command.Parameters.AddWithValue("$id", stream.ToString("D"));
            command.Parameters.AddWithValue("$state", state);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
    }

    [Test]
    public async Task PullAsync_JoiningStream_SnapshotsThenPullsFromSnapshotCursor()
    {
        using var host = await DataHost.StartAsync();
        var stream = Guid.NewGuid();
        await AddStreamAsync(host, stream, "joining");
        var service = new Service(path => path.Contains("/snapshot?")
            ? new(200, "{\"cursor\":5,\"changes\":[],\"next\":null,\"more\":false}")
            : new(200, "{\"snapshotRequired\":false,\"next\":7,\"changes\":[],\"more\":false}"));
        var result = await new SyncStreamPuller(host.Store, new SyncApplier(host.Store, host.Clock, host.Ids), service).PullAsync(stream);
        await Assert.That(result.Snapshot).IsTrue();
        await Assert.That(await TextAsync(host, $"SELECT State FROM SyncStreams WHERE StreamId='{stream:D}';")).IsEqualTo("active");
        await Assert.That(await CountAsync(host, $"SELECT Cursor FROM SyncStreams WHERE StreamId='{stream:D}';")).IsEqualTo(7L);
        await Assert.That(service.Paths.Last()).Contains("after=5");
        await Assert.That(service.Paths[0]).Contains("kind=profile");
        await Assert.That(service.Paths[1]).Contains("kind=space");
        await Assert.That(service.Paths[2]).Contains("kind=space_member");
    }

    [Test]
    public async Task PullAsync_MoveToInaccessibleStream_EvictsTheEntityAndAdvances()
    {
        using var host = await DataHost.StartAsync();
        var stream = Guid.NewGuid();
        await AddStreamAsync(host, stream, "active");
        var task = await CaptureTaskAsync(host, "Leaves the device");
        var service = new Service(_ => new(200, $$"""{"snapshotRequired":false,"next":1,"changes":[{"seq":1,"changeId":"{{Guid.NewGuid():D}}","kind":"task","key":"{{task.Id}}","op":"moved","to":"{{Guid.NewGuid():D}}"}],"more":false}"""));
        var puller = new SyncStreamPuller(host.Store, new SyncApplier(host.Store, host.Clock, host.Ids), service);
        await puller.PullAsync(stream);
        await Assert.That(await CountAsync(host, $"SELECT count(*) FROM Tasks WHERE Id='{task.Id}';")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, $"SELECT Cursor FROM SyncStreams WHERE StreamId='{stream:D}';")).IsEqualTo(1L);
    }
}
