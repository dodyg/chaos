using Chaos.Sync;

namespace Chaos.Sync.Tests;

public sealed class AgentCountersTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Counters_RefusePastTheLimit_AndStartOverWhenTheWindowEnds()
    {
        var counters = AgentCounters.Empty;
        for (var index = 0; index < AgentLimits.ClaimsPerHour; index++)
        {
            var (next, allowed, retry) = counters.Use(Start.AddMinutes(index), adds: false);
            await Assert.That(allowed).IsTrue();
            counters = next;
        }
        var (unchanged, refused, retryAfter) = counters.Use(Start.AddMinutes(45), adds: false);
        await Assert.That(refused).IsFalse();
        await Assert.That(retryAfter).IsEqualTo(15 * 60);
        await Assert.That(unchanged).IsEqualTo(counters);
        var (restarted, allowedAgain, _) = unchanged.Use(Start.AddHours(1), adds: false);
        await Assert.That(allowedAgain).IsTrue();
        await Assert.That(restarted.Claims).IsEqualTo(1);
        await Assert.That(restarted.Adds).IsEqualTo(0);
    }

    [Test]
    public async Task AddsAndClaims_ShareOneWindow()
    {
        var (first, _, _) = AgentCounters.Empty.Use(Start, adds: true);
        var (second, _, _) = first.Use(Start.AddMinutes(1), adds: false);
        await Assert.That(second.Adds).IsEqualTo(1);
        await Assert.That(second.Claims).IsEqualTo(1);
        await Assert.That(second.WindowStart).IsEqualTo(Start);
    }

    [Test]
    public async Task TheToolContract_NamesTheClaimToolsAndTheirCodes()
    {
        await Assert.That(AgentToolNames.ClaimTask).IsEqualTo("claim_task");
        await Assert.That(AgentToolNames.ReleaseTask).IsEqualTo("release_task");
        await Assert.That(AgentToolNames.Heartbeat).IsEqualTo("heartbeat");
        foreach (var code in new[] { AgentToolStatus.Claimed, AgentToolStatus.AlreadyClaimed, AgentToolStatus.NotClaimant, AgentToolStatus.Paused,
                     AgentToolStatus.RateLimited, AgentToolStatus.Forbidden, AgentToolStatus.NotFound })
            await Assert.That(code).DoesNotContain(' ');
    }
}
