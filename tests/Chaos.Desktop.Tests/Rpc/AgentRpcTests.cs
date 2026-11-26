using Chaos.Application.Abstractions;
using Chaos.Application.Agents;
using Chaos.Application.History;
using Chaos.Application.Reminders;
using Chaos.Application.Spaces;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>Local agents and Personal-space claims over RPC (PLAN3 Q17): a token is shown once, access is ticked per list, Ready for agent and a claim round-trip.</summary>
public sealed class AgentRpcTests
{
    private static async Task<(ChaosStorageHandle Storage, SequentialIdGenerator Ids, FixedTimeProvider Clock, string Inbox, string TaskId)> PersonalTaskAsync(string root, FixedTimeProvider clock, SequentialIdGenerator ids)
    {
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var profile = (await storage.Store.QueryAsync((session, ct) => session.Profiles.GetPrimaryAsync(ct)))!;
        var inbox = (await storage.Store.QueryAsync(async (session, ct) => await session.Containers.GetInboxAsync((await session.Spaces.GetPersonalByOwnerAsync(profile.Id, ct))!.Id, ct)))!.Id;
        var task = (await new TaskCommands(storage.Store, ids, clock).CaptureAsync(new(Guid.NewGuid().ToString(), inbox, "Deploy the bot", null))).Task;
        return (storage, ids, clock, inbox, task.Id);
    }

    private static AgentRpcService Agents(ChaosStorageHandle storage, SequentialIdGenerator ids, FixedTimeProvider clock) =>
        new(storage.Store, new AgentCommands(storage.Store, ids, clock), new UndoCommands(storage.Store, ids, clock), new ServiceAgentCommands(null!), null, Chaos.Desktop.Platform.McpClientConfig.ForThisApp(Path.GetTempPath()));

    [Test]
    public async Task LocalAgents_CreateTickATokenAndUndo_RoundTrip()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        var (storage, ids2, clock2, inbox, _) = await PersonalTaskAsync(root, clock, ids);
        using (storage)
        {
            var rpc = Agents(storage, ids2, clock2);
            var empty = await rpc.ListAsync(new(), CancellationToken.None);
            await Assert.That(empty.Ok).IsTrue();
            await Assert.That(empty.Agents).IsEmpty();

            var created = await rpc.CreateAsync(new(Guid.NewGuid().ToString(), "Build bot"), CancellationToken.None);
            await Assert.That(created.Ok).IsTrue();
            await Assert.That(created.Agent!.Name).IsEqualTo("Build bot");
            var invalid = await rpc.CreateAsync(new(Guid.NewGuid().ToString(), " "), CancellationToken.None);
            await Assert.That(invalid.Ok).IsFalse();

            var listed = await rpc.ListAsync(new(), CancellationToken.None);
            var view = listed.Agents.Single();
            await Assert.That(view.Lists.Single(list => list.ContainerId == inbox).Allowed).IsFalse(); // off until ticked
            var access = await rpc.SetAccessAsync(new(Guid.NewGuid().ToString(), view.Agent.Id, inbox, true), CancellationToken.None);
            await Assert.That(access.Ok).IsTrue();
            await Assert.That((await rpc.ListAsync(new(), CancellationToken.None)).Agents.Single().Lists.Single(list => list.ContainerId == inbox).Allowed).IsTrue();

            var token = await rpc.CreateTokenAsync(new(Guid.NewGuid().ToString(), view.Agent.Id, view.Agent.Version, "Claude Desktop"), CancellationToken.None);
            await Assert.That(token.Ok).IsTrue();
            await Assert.That(token.Secret!).IsNotNullOrEmpty();
            await Assert.That((await rpc.ListAsync(new(), CancellationToken.None)).Agents.Single().Tokens.Single().Name).IsEqualTo("Claude Desktop");
            var revoked = await rpc.RevokeTokenAsync(new(Guid.NewGuid().ToString(), view.Agent.Id, token.Token!.Id), CancellationToken.None);
            await Assert.That(revoked.Ok).IsTrue();
            await Assert.That((await rpc.ListAsync(new(), CancellationToken.None)).Agents.Single().Tokens).IsEmpty();

            var paused = await rpc.SetPausedAsync(new(Guid.NewGuid().ToString(), view.Agent.Id, access.Agent!.Version, true), CancellationToken.None);
            await Assert.That(paused.Agent!.PausedAtUtc).IsNotNull();
            var removed = await rpc.RemoveAsync(new(Guid.NewGuid().ToString(), view.Agent.Id, paused.Agent.Version), CancellationToken.None);
            await Assert.That(removed.Ok).IsTrue();
            await Assert.That((await rpc.ListAsync(new(), CancellationToken.None)).Agents).IsEmpty();
        }
    }

    [Test]
    public async Task ReadyForAgentAndAClaim_RoundTripOnAPersonalTask()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        var (storage, ids2, clock2, inbox, taskId) = await PersonalTaskAsync(root, clock, ids);
        using (storage)
        {
            var agents = Agents(storage, ids2, clock2);
            var bot = (await agents.CreateAsync(new(Guid.NewGuid().ToString(), "Build bot"), CancellationToken.None)).Agent!;
            await agents.SetAccessAsync(new(Guid.NewGuid().ToString(), bot.Id, inbox, true), CancellationToken.None);
            var claims = new ClaimRpcService(storage.Store, new ClaimCommands(storage.Store, ids2, clock2), new SharedClaimCommands(null!), null);
            var task = (await storage.Store.QueryAsync((session, ct) => session.Tasks.GetAsync(taskId, ct)))!;
            var ready = await claims.SetReadyAsync(new(Guid.NewGuid().ToString(), taskId, task.Version.ToString(), true, bot.Id), CancellationToken.None);
            await Assert.That(ready.Ok).IsTrue();
            await Assert.That(ready.Task!.AgentReady).IsTrue();
            await Assert.That(ready.Task.AgentAssigneeId).IsEqualTo(bot.Id);

            var claimed = await claims.ClaimAsync(new(Guid.NewGuid().ToString(), taskId, ready.Task.Version), CancellationToken.None);
            await Assert.That(claimed.Ok).IsTrue();
            await Assert.That(claimed.Task!.ClaimedById!).StartsWith("person:");
            await Assert.That(claimed.Task.Status).IsEqualTo("InProgress");
            await Assert.That(claimed.Task.ClaimExpiresAtUtc).IsEqualTo(StorageFormats.Instant(clock2.GetUtcNow().AddMinutes(120)));
            var released = await claims.ReleaseAsync(new(Guid.NewGuid().ToString(), taskId, claimed.Task.Version), CancellationToken.None);
            await Assert.That(released.Task!.ClaimedById).IsNull();
            await Assert.That(released.Task.Status).IsEqualTo("NotStarted");
        }
    }
}
