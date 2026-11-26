using Chaos.Application.Abstractions;
using Chaos.Application.Agents;
using Chaos.Application.Contracts;
using Chaos.Application.Pad;
using Chaos.Infrastructure.Tests.DataProtection;

namespace Chaos.Infrastructure.Tests.Agents;

/// <summary>Pad proposals (PLAN3 12.6) against real SQLite: an agent only suggests, and a person's click decides.</summary>
public sealed class ProposalTests
{
    private static async Task<(DataHost Host, string Inbox, string AgentId, string PadId, ProposalCommands Proposals)> StartAsync(bool grantList = true)
    {
        var host = await DataHost.StartAsync();
        var inbox = await host.Store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile!.Id, token);
            return (await session.Containers.GetInboxAsync(space!.Id, token))!.Id;
        });
        var agents = new AgentCommands(host.Store, host.Ids, host.Clock);
        var agent = await agents.CreateAsync(new(DataHost.Op(), "Build bot"));
        if (grantList) _ = await agents.SetAccessAsync(new(DataHost.Op(), agent.Id, inbox, Allowed: true));
        var pad = await new PadCommands(host.Store, host.Ids, host.Clock).CreateAsync(new(DataHost.Op(), "Plan the launch\nBook the venue"));
        return (host, inbox, agent.Id, pad.Item.Id, new ProposalCommands(host.Store, host.Ids, host.Clock));
    }

    private static async Task<int> TaskCountAsync(DataHost host) => (int)await host.CountAsync("SELECT COUNT(*) FROM Tasks WHERE DeletedAtUtc IS NULL;");

    [Test]
    public async Task Proposing_CreatesOnlyProposals_AndNothingBecomesATaskWithoutAClick()
    {
        var (host, _, agentId, padId, proposals) = await StartAsync();
        var before = await TaskCountAsync(host);
        var made = await proposals.ProposeAsync(new(agentId, padId, [new("Book the venue", "Ask for a quote", "Book the venue tomorrow"), new("Plan the launch", null, null)]));
        await Assert.That(made.Count).IsEqualTo(2);
        await Assert.That(made.All(p => p.State == ProposalState.Pending && p.CreatedTaskId is null)).IsTrue();
        await Assert.That(await TaskCountAsync(host)).IsEqualTo(before);

        var pending = await proposals.ListPendingAsync(padId);
        await Assert.That(pending.Select(item => item.AgentName).Distinct().Single()).IsEqualTo("Build bot");
        await Assert.That(pending.Count).IsEqualTo(2);
        await Assert.That(await TaskCountAsync(host)).IsEqualTo(before); // listing creates nothing either
    }

    [Test]
    public async Task Accepting_MakesTheTaskAsThePerson_LinksTheNote_AndCannotBeRepeated()
    {
        var (host, inbox, agentId, padId, proposals) = await StartAsync();
        var proposal = (await proposals.ProposeAsync(new(agentId, padId, [new("Book the venue", "Ask for a quote", null)]))).Single();
        var accepted = await proposals.AcceptAsync(new(DataHost.Op(), proposal.Id, proposal.Version, inbox));
        await Assert.That(accepted.Task!.Title).IsEqualTo("Book the venue");
        await Assert.That(accepted.Task.Notes).IsEqualTo("Ask for a quote");
        await Assert.That(accepted.Proposal.State).IsEqualTo(ProposalState.Accepted);
        await Assert.That(accepted.Proposal.CreatedTaskId).IsEqualTo(accepted.Task.Id);

        // The task's history names the person, not the agent; the note links to it.
        var history = await host.Store.QueryAsync((session, ct) => session.History.ListByTaskAsync(accepted.Task.Id, ct));
        await Assert.That(history.Any(entry => entry.Actor.StartsWith("agent:", StringComparison.Ordinal))).IsFalse();
        var links = await host.Store.QueryAsync((session, ct) => session.Pad.ListTaskLinksAsync(padId, ct));
        await Assert.That(links.Single().Quote).IsEqualTo("Book the venue");
        await Assert.That(await proposals.ListPendingAsync()).IsEmpty();

        var again = await Assert.That(async () => await proposals.AcceptAsync(new(DataHost.Op(), proposal.Id, accepted.Proposal.Version, inbox))).Throws<AppException>();
        await Assert.That(again).IsNotNull();
        await Assert.That(await TaskCountAsync(host)).IsEqualTo(1);
    }

    [Test]
    public async Task Dismissing_ClosesTheSuggestion_WithoutATask()
    {
        var (host, _, agentId, padId, proposals) = await StartAsync();
        var proposal = (await proposals.ProposeAsync(new(agentId, padId, [new("Plan the launch", null, null)]))).Single();
        var dismissed = await proposals.DismissAsync(new(DataHost.Op(), proposal.Id, proposal.Version));
        await Assert.That(dismissed.Proposal.State).IsEqualTo(ProposalState.Dismissed);
        await Assert.That(dismissed.Task).IsNull();
        await Assert.That(await TaskCountAsync(host)).IsEqualTo(0);
        await Assert.That(await proposals.ListPendingAsync()).IsEmpty();
    }

    [Test]
    public async Task APausedAgent_OneWithoutLists_OrAForeignPadNote_CannotPropose()
    {
        var (host, _, agentId, padId, proposals) = await StartAsync();
        var agents = new AgentCommands(host.Store, host.Ids, host.Clock);
        var current = (await agents.ListAsync()).Single().Agent;
        _ = await agents.SetPausedAsync(new(DataHost.Op(), agentId, current.Version, Paused: true));
        await Assert.That(async () => await proposals.ProposeAsync(new(agentId, padId, [new("x", null, null)]))).Throws<AppException>();

        var (_, _, bareAgent, barePad, bare) = await StartAsync(grantList: false);
        await Assert.That(async () => await bare.ProposeAsync(new(bareAgent, barePad, [new("x", null, null)]))).Throws<AppException>();

        var (_, _, liveAgent, _, live) = await StartAsync();
        await Assert.That(async () => await live.ProposeAsync(new(liveAgent, "no-such-note", [new("x", null, null)]))).Throws<AppException>();
        await Assert.That(async () => await live.ProposeAsync(new(liveAgent, null, []))).Throws<AppException>();
    }
}
