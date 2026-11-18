using System.Net;
using System.Text.Json;

namespace Chaos.Server.Tests;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class WebAppHostTests(PostgresServer postgres)
{
    [Test]
    public async Task App_ServesInstallableShellWithSameOriginPolicy_AndPrivateApiRemainsUncached()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var browser = server.NewBrowser();
        using var redirect = await browser.GetAsync("/app");
        await Assert.That(redirect.Headers.Location?.OriginalString).EndsWith("/app/");
        using var shell = await browser.GetAsync("/app/");
        await Assert.That(shell.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(shell.Headers.GetValues("Content-Security-Policy").Single())
            .Contains("script-src 'self'").And.Contains("worker-src 'self'").And.Contains("frame-ancestors 'none'");
        await Assert.That(shell.Headers.CacheControl?.NoStore).IsTrue();
        var html = await shell.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("/app/manifest.webmanifest");

        using var manifest = await browser.GetAsync("/app/manifest.webmanifest");
        await Assert.That(manifest.Content.Headers.ContentType?.MediaType).IsEqualTo("application/manifest+json");
        using var json = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("display").GetString()).IsEqualTo("standalone");
        await Assert.That(json.RootElement.GetProperty("start_url").GetString()).IsEqualTo("/app/");
        foreach (var icon in json.RootElement.GetProperty("icons").EnumerateArray())
        {
            using var image = await browser.GetAsync("/app/" + icon.GetProperty("src").GetString());
            await Assert.That(image.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(image.Content.Headers.ContentType?.MediaType).IsEqualTo("image/png");
        }

        using var worker = await browser.GetAsync("/app/sw.js");
        await Assert.That(worker.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(worker.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(await worker.Content.ReadAsStringAsync()).Contains("/app/index.html").And.DoesNotContain("/v1/web");

        using var signedIn = await WebFlow.SignInAsync(server, browser, "host@example.com");
        using var today = await browser.GetAsync("/v1/web/today");
        await Assert.That(today.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(today.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task WebApi_RejectsAStaleDevice_AndLimitsRequestsPerDevice()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["Limits:RequestsPerMinutePerDevice"] = "1" });
        using var browser = server.NewBrowser();
        using var otherBrowser = server.NewBrowser();
        using var signedIn = await WebFlow.SignInAsync(server, browser, "limited@example.com");
        using var otherSignedIn = await WebFlow.SignInAsync(server, otherBrowser, "other-limited@example.com");
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/v1/web/today");
        stale.Headers.Add("Chaos-Web-Device", Guid.NewGuid().ToString("D"));
        using var staleResponse = await browser.SendAsync(stale);
        await Assert.That(staleResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        using var rateLimited = await browser.GetAsync("/v1/web/lists");
        await Assert.That(rateLimited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        using var allowed = await otherBrowser.GetAsync("/v1/web/lists");
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
