using System.Net;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Updates;

namespace Chaos.Infrastructure.Tests.Updates;

public sealed class GitHubReleaseFeedTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static GitHubReleaseFeed FeedOf(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)));

    [Test]
    public async Task Latest_ReadsTagAndPage()
    {
        var feed = FeedOf(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.4.2","html_url":"https://github.com/dodyg/chaos/releases/tag/v1.4.2","published_at":"2026-11-01T10:00:00Z"}"""),
        });

        var release = await feed.LatestAsync(CancellationToken.None);

        await Assert.That(release!.Version).IsEqualTo("1.4.2");
        await Assert.That(release.Tag).IsEqualTo("v1.4.2");
    }

    [Test]
    public async Task Latest_IsNullWhenNothingIsPublished()
    {
        var feed = FeedOf(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.That(await feed.LatestAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Latest_ExplainsRateLimiting()
    {
        var feed = FeedOf(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<UpdateCheckException>(async () => await feed.LatestAsync(CancellationToken.None));
    }

    [Test]
    [Arguments("1.0.0", "v1.0.1", true)]
    [Arguments("1.0.0", "v1.0.0", false)]
    [Arguments("1.2.0", "v1.10.0", true)]
    [Arguments("2.0.0", "v1.9.9", false)]
    [Arguments("1.0.0", "nightly", false)]
    [Arguments("1.0.0", "v1.1.0-beta.1", true)]
    public async Task IsNewer_ComparesNumerically(string current, string latest, bool expected)
    {
        await Assert.That(ReleaseVersions.IsNewer(current, latest)).IsEqualTo(expected);
    }
}
