using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class GitHubRpcTests
{
    private static string Op() => Guid.NewGuid().ToString();
    [Test]
    public async Task ConnectionAndRepository_RpcRoundTrip_UsesStringIdsAndLocalStatus()
    {
        var clock = new FixedTimeProvider(new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var api = new FakeGitHub { Account = new(3000000000, "person") };
        api.PutRepository(new(4000000000, "R", "repo", "person/repo", new(3000000000, "person"), "https://github.com/person/repo", true, true));
        var rpc = new GitHubRpcService(new(storage.Store, api, new SequentialIdGenerator(), clock), storage.Store);
        var local = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(api.Requests).IsEqualTo(0);
        await Assert.That(local.Connection).IsNull();
        await Assert.That((await rpc.ConnectAsync(new(Op()), CancellationToken.None)).Ok).IsTrue();
        var status = await rpc.StatusAsync(new(), CancellationToken.None);
        var connection = status.Connection!;
        var list = await rpc.ListRepositoriesAsync(new(null), CancellationToken.None);
        var request = new GitHubAddRequest(Op(), connection.Id, connection.Version, "person", "repo", list.Repositories.Single().RepositoryId, status.Targets.First().Id, null, new([], "any", "any", null, false, 0), false);
        await Assert.That((await rpc.AddRepositoryAsync(request, CancellationToken.None)).Ok).IsTrue();
        status = await rpc.StatusAsync(new(), CancellationToken.None);
        var json = JsonSerializer.Serialize(status);
        await Assert.That(json.Contains("\"AccountId\":\"3000000000\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.Contains("\"RepositoryId\":\"4000000000\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That((await rpc.AddRepositoryAsync(request with { OperationId = Op(), ExpectedVersion = "bad" }, CancellationToken.None)).Error!.Kind).IsEqualTo("validation");
        await Assert.That((await rpc.DisconnectAsync(new(Op(), connection.Id, connection.Version, false), CancellationToken.None)).Ok).IsTrue();
        await Assert.That((await rpc.StatusAsync(new(), CancellationToken.None)).Connection).IsNull();
    }

    [Test]
    [Arguments(GitHubErrorKind.CliUnavailable)]
    [Arguments(GitHubErrorKind.Unauthorized)]
    [Arguments(GitHubErrorKind.AccountMismatch)]
    public async Task SetupFailure_ReturnsActionableErrorAsData(GitHubErrorKind kind)
    {
        var clock = new FixedTimeProvider(new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var api = new FakeGitHub { NextFailure = kind };
        var rpc = new GitHubRpcService(new(storage.Store, api, new SequentialIdGenerator(), clock), storage.Store);
        var result = await rpc.ConnectAsync(new(Op()), CancellationToken.None);
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Error!.Kind).IsEqualTo("github" + kind);
        await Assert.That(result.Error.Message.Length > 30).IsTrue();
        await Assert.That((await rpc.StatusAsync(new(), CancellationToken.None)).Connection).IsNull();
    }
}
