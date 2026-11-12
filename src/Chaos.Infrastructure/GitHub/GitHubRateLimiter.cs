using System.Globalization;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.GitHub;

/// <summary>Called under the client's request gate. Reserves the last 100 requests for essential work.</summary>
public sealed class GitHubRateLimiter(TimeProvider clock)
{
    private DateTimeOffset? _blockedUntil;
    private DateTimeOffset? _reservedUntil;

    public void Check(bool nonessentialRead)
    {
        var now = clock.GetUtcNow();
        var until = _blockedUntil > now ? _blockedUntil : nonessentialRead && _reservedUntil > now ? _reservedUntil : null;
        if (until is not null) throw new GitHubApiException(GitHubErrorKind.RateLimited, until);
    }

    public void Block(DateTimeOffset? until) => _blockedUntil = until > clock.GetUtcNow() ? until : clock.GetUtcNow().AddMinutes(1);

    public void Observe(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) ||
            !long.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var remaining) || remaining < 0) return;
        DateTimeOffset? reset = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out values) && long.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 253402300799)
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (remaining == 0) Block(reset);
        if (remaining < 100 && reset > clock.GetUtcNow()) _reservedUntil = reset;
    }
}
