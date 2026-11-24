using System.Net;
using System.Net.Http.Json;
using Chaos.Server.Api;
using Chaos.Server.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chaos.Server.Tests;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class WebSessionTests(PostgresServer postgres)
{
    private const string Email = "web@example.com";

    [Test]
    public async Task Events_WebSession_StreamsAccountChangesAndStopsAfterRevocation()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var browser = server.NewBrowser();
        using var anonymous = await browser.GetAsync($"/v1/web/events?device={Guid.NewGuid():D}");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var signedIn = await WebFlow.SignInAsync(server, browser, Email);
        var session = await WebFlow.SessionAsync(browser);
        using var mismatch = await browser.GetAsync($"/v1/web/events?device={Guid.NewGuid():D}");
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await browser.GetAsync($"/v1/web/events?device={session.DeviceId:D}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("data: 0");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo(string.Empty);

        await using var db = ServerDatabase.Open(server.ConnectionString);
        var accountId = await db.Accounts.Where(a => a.PrimaryEmail == Email).Select(a => a.Id).SingleAsync(timeout.Token);
        var broadcaster = server.Factory.Services.GetRequiredService<SyncBroadcaster>();
        broadcaster.Publish(Guid.NewGuid(), 100);
        broadcaster.PublishStream(accountId, accountId, 1);
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("data: 1");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo(string.Empty);

        await db.Devices.Where(d => d.Id == session.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow), timeout.Token);
        broadcaster.PublishStream(accountId, accountId, 2);
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsNull();
        using var revoked = await browser.GetAsync($"/v1/web/events?device={session.DeviceId:D}");
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task SignIn_RegistersCookieDevice_AndDesktopCanRevokeIt()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var browser = server.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        using var anonymous = await browser.GetAsync("/v1/web/session");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(anonymous.Headers.Location).IsNull();

        using var signedIn = await WebFlow.SignInAsync(server, browser, Email);
        var cookie = signedIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("chaos.web=", StringComparison.Ordinal));
        await Assert.That(cookie).Contains("httponly").And.Contains("secure").And.Contains("samesite=lax").And.Contains("path=/v1/web");
        await Assert.That(signedIn.Headers.Location?.OriginalString).IsEqualTo("/app");
        var session = await WebFlow.SessionAsync(browser);
        var offlineMarker = signedIn.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("chaos.web-offline=", StringComparison.Ordinal));
        await Assert.That(offlineMarker).StartsWith($"chaos.web-offline={session.DeviceId:D};").And.Contains("secure").And.Contains("path=/app");

        var desktop = new DesktopFlow(server);
        var tokens = await desktop.SignInAsync(Email);
        using var desktopClient = server.NewBrowser();
        using var account = await desktopClient.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", tokens.AccessToken));
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var details = (await account.Content.ReadFromJsonAsync<AccountDto>())!;
        await Assert.That(details.Devices.Single(device => device.Id == session.DeviceId).Platform).IsEqualTo("web");
        using var revoked = await desktopClient.SendAsync(DesktopFlow.Api(HttpMethod.Delete, $"/v1/devices/{session.DeviceId}", tokens.AccessToken));
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var afterRevocation = await browser.GetAsync("/v1/web/session");
        await Assert.That(afterRevocation.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var bearerOnly = await desktopClient.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/web/session", tokens.AccessToken));
        await Assert.That(bearerOnly.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task SignOut_RequiresAntiforgery_AndInvalidatesTheSession()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var browser = server.NewBrowser();
        using var signedIn = await WebFlow.SignInAsync(server, browser, Email);
        var session = await WebFlow.SessionAsync(browser);

        using var missingToken = await browser.PostAsync("/v1/web/signout", null);
        await Assert.That(missingToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var signedOut = await WebFlow.PostAsync(browser, session, "/v1/web/signout");
        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var afterSignOut = await browser.GetAsync("/v1/web/session");
        await Assert.That(afterSignOut.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}

internal sealed record WebSessionInfo(Guid DeviceId, string AntiforgeryHeader, string AntiforgeryToken);

internal static class WebFlow
{
    public static async Task<HttpResponseMessage> SignInAsync(TestServer server, HttpClient browser, string email)
    {
        var page = await browser.GetStringAsync("/signin?ReturnUrl=%2Fapp");
        var returnUrl = DesktopFlow.Hidden(page, "returnUrl")!;
        using var codePage = await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", email), ("returnUrl", returnUrl)));
        var codeHtml = await codePage.Content.ReadAsStringAsync();
        return await browser.PostAsync("/signin/verify", DesktopFlow.Form(codeHtml, ("email", email),
            ("code", server.Mail.LastCode(email)), ("returnUrl", returnUrl)));
    }

    public static async Task<WebSessionInfo> SessionAsync(HttpClient browser) =>
        (await browser.GetFromJsonAsync<WebSessionInfo>("/v1/web/session"))!;

    public static async Task<HttpResponseMessage> PostAsync(HttpClient browser, WebSessionInfo session, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(session.AntiforgeryHeader, session.AntiforgeryToken);
        request.Content = body is null ? null : JsonContent.Create(body);
        return await browser.SendAsync(request);
    }
}
