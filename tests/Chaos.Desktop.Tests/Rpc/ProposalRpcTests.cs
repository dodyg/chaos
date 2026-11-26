using Chaos.Application.Agents;
using Chaos.Application.Capture;
using Chaos.Application.Pad;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// The pad's Accept and Dismiss over RPC (PLAN3 12.6): an agent's suggestions are listed with its name, only Accept makes a
/// task (parsing the quick-add text as the bar does, and linking the note), and failures come back as data.
/// </summary>
public sealed class ProposalRpcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private sealed class GuidIds : Chaos.Application.Abstractions.IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task Suggestions_AreListedWithTheAgent_AcceptParsesQuickAddAndLinksTheNote_AndDismissMakesNoTask()
    {
        var ids = new GuidIds();
        var clock = new FixedTimeProvider(Now);
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var store = storage.Store;
        var inbox = await store.QueryAsync(async (session, ct) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(ct);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile!.Id, ct);
            return (await session.Containers.GetInboxAsync(space!.Id, ct))!.Id;
        });
        var agents = new AgentCommands(store, ids, clock);
        var bot = await agents.CreateAsync(new(Op(), "Build bot"));
        await agents.SetAccessAsync(new(Op(), bot.Id, inbox, Allowed: true));
        var pad = await new PadCommands(store, ids, clock).CreateAsync(new(Op(), "Call the venue\nOrder the cake"));
        var commands = new ProposalCommands(store, ids, clock);
        _ = await commands.ProposeAsync(new(bot.Id, pad.Item.Id,
        [
            new("Call the venue", "Ask about Friday", "Call the venue tomorrow !high"),
            new("Order the cake", null, null),
        ]));
        var rpc = new ProposalRpcService(commands, new CaptureCommands(store, ids, clock), null);

        var listed = await rpc.ListAsync(new(null), CancellationToken.None);
        await Assert.That(listed.Ok).IsTrue();
        await Assert.That(listed.Items.Select(item => item.AgentName).Distinct().Single()).IsEqualTo("Build bot");
        await Assert.That((await rpc.ListAsync(new(pad.Item.Id), CancellationToken.None)).Items.Count).IsEqualTo(2);
        await Assert.That((await rpc.ListAsync(new("another-note"), CancellationToken.None)).Items).IsEmpty();
        await Assert.That(await store.QueryAsync(async (session, ct) => (await session.Tasks.ListByContainerAsync(inbox, ct)).Count)).IsEqualTo(0);

        // Accept: the quick-add text is parsed (a date and a priority), the notes come from the agent, the note is linked.
        var venue = listed.Items.Single(item => item.Title == "Call the venue");
        var accepted = await rpc.AcceptAsync(new(Op(), venue.Id, venue.Version, null), CancellationToken.None);
        await Assert.That(accepted.Ok).IsTrue();
        await Assert.That(accepted.Task!.Title).IsEqualTo("Call the venue");
        await Assert.That(accepted.Task.Notes).IsEqualTo("Ask about Friday");
        await Assert.That(accepted.Task.PlannedDate).IsEqualTo("2026-11-27");
        await Assert.That(accepted.Task.Priority).IsEqualTo("High");
        await Assert.That((await store.QueryAsync((session, ct) => session.Pad.ListTaskLinksAsync(pad.Item.Id, ct))).Single().Quote).IsEqualTo("Call the venue");

        // The same click again is refused as data, not thrown.
        var again = await rpc.AcceptAsync(new(Op(), venue.Id, "2", null), CancellationToken.None);
        await Assert.That(again.Ok).IsFalse();
        await Assert.That(again.Error).IsNotNull();

        // Dismiss: no task, and the suggestion is gone from the list.
        var cake = listed.Items.Single(item => item.Title == "Order the cake");
        var dismissed = await rpc.DismissAsync(new(Op(), cake.Id, cake.Version), CancellationToken.None);
        await Assert.That(dismissed.Ok).IsTrue();
        await Assert.That((await rpc.ListAsync(new(null), CancellationToken.None)).Items).IsEmpty();
        await Assert.That(await store.QueryAsync(async (session, ct) => (await session.Tasks.ListByContainerAsync(inbox, ct)).Count)).IsEqualTo(1);
    }
}
