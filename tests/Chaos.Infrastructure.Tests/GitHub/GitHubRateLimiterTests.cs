using System.Net;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.GitHub;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubRateLimiterTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public async Task Observe_QuotaReserved_PausesNonessentialReadsUntilReset()
    {
        var clock = new Clock();
        var limiter = new GitHubRateLimiter(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("X-RateLimit-Remaining", "99");
        response.Headers.Add("X-RateLimit-Reset", clock.Now.AddMinutes(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        limiter.Observe(response);
        limiter.Check(nonessentialRead: false);
        await Assert.That(() => limiter.Check(nonessentialRead: true)).Throws<GitHubApiException>();
        clock.Now = clock.Now.AddMinutes(1);
        limiter.Check(nonessentialRead: true);
    }

    [Test]
    public async Task Block_NoResetProvided_StopsAllRequestsForOneMinute()
    {
        var clock = new Clock();
        var limiter = new GitHubRateLimiter(clock);
        limiter.Block(null);
        await Assert.That(() => limiter.Check(false)).Throws<GitHubApiException>();
        clock.Now = clock.Now.AddMinutes(1);
        limiter.Check(false);
    }
}
