using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests;

/// <summary>PLAN2 P12 items 3, 4 and 6: email code sign-in, PKCE, refresh rotation, account isolation, limits and logs.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class AuthFlowTests(PostgresServer postgres)
{
    private const string Email = "person@example.com";

    private static RegisterDeviceRequest Registration(Guid id, string name = "Laptop") => new(id, name, "windows", "0.2.1", 1);

    [Test]
    public async Task SignIn_EmailCode_ReachesTheAccountAndRegistersTheDevice()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        var tokens = await flow.SignInAsync(Email);
        using var http = server.NewBrowser();

        using var registered = await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, Registration(flow.DeviceId)));
        using var account = await http.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", tokens.AccessToken));

        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = (await account.Content.ReadFromJsonAsync<AccountDto>(JsonSerializerOptions.Web))!;
        await Assert.That(body.Email).IsEqualTo(Email);
        await Assert.That(body.Devices.Count).IsEqualTo(1);
        await Assert.That(body.Devices[0].Id).IsEqualTo(flow.DeviceId);
        await Assert.That(body.Devices[0].Current).IsTrue();
        await Assert.That(body.StorageQuotaBytes).IsEqualTo(5L * 1024 * 1024 * 1024);
    }

    [Test]
    public async Task Api_WithoutToken_IsUnauthorized()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var http = server.NewBrowser();

        using var response = await http.GetAsync("/v1/account");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Authorize_WithoutPkce_IsRefused()
    {
        await using var server = await TestServer.StartAsync(postgres);
        using var http = server.NewBrowser();

        using var response = await http.GetAsync(new DesktopFlow(server).AuthorizeUrl(pkce: false));

        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(location + body).Contains("invalid_request");
        await Assert.That(location).DoesNotContain("code=");
    }

    [Test]
    public async Task Token_WrongCodeVerifier_IsRefused()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var code = await flow.SignInForCodeAsync(browser, Email);

        using var response = await flow.ExchangeCodeAsync(browser, code, verifier: DesktopFlow.Base64Url(new byte[32]));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("invalid_grant");
    }

    [Test]
    public async Task EmailCode_FiveWrongAttempts_LocksTheCode()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var page = await browser.GetStringAsync((await browser.GetAsync(flow.AuthorizeUrl())).Headers.Location!.OriginalString);
        var returnUrl = DesktopFlow.Hidden(page, "returnUrl")!;
        var codePage = await (await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", Email), ("returnUrl", returnUrl)))).Content.ReadAsStringAsync();
        var real = server.Mail.LastCode(Email);
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < EmailCode.MaxAttempts; i++)
        {
            using var attempt = await browser.PostAsync("/signin/verify", DesktopFlow.Form(codePage, ("email", Email), ("code", wrong), ("returnUrl", returnUrl)));
            await Assert.That(attempt.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using var late = await browser.PostAsync("/signin/verify", DesktopFlow.Form(codePage, ("email", Email), ("code", real), ("returnUrl", returnUrl)));
        await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await late.Content.ReadAsStringAsync()).Contains("expired or can no longer be used");
    }

    [Test]
    public async Task EmailCode_AfterTenMinutes_Expires()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var page = await browser.GetStringAsync((await browser.GetAsync(flow.AuthorizeUrl())).Headers.Location!.OriginalString);
        var returnUrl = DesktopFlow.Hidden(page, "returnUrl")!;
        var codePage = await (await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", Email), ("returnUrl", returnUrl)))).Content.ReadAsStringAsync();
        server.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        using var late = await browser.PostAsync("/signin/verify", DesktopFlow.Form(codePage, ("email", Email), ("code", server.Mail.LastCode(Email)), ("returnUrl", returnUrl)));

        await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task EmailCode_IsStoredOnlyAsAHash()
    {
        await using var server = await TestServer.StartAsync(postgres);
        await new DesktopFlow(server).SignInAsync(Email);

        await using var db = ServerDatabase.Open(server.ConnectionString);
        var row = await db.EmailCodes.SingleAsync();

        await Assert.That(row.CodeHash).StartsWith("v1.");
        await Assert.That(row.CodeHash).DoesNotContain(server.Mail.LastCode(Email));
        await Assert.That(row.ConsumedAt).IsNotNull();
    }

    [Test]
    public async Task Refresh_RotatesTheTokenAndReuseRevokesTheFamily()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var tokens = await new DesktopFlow(server).SignInAsync(Email);
        using var http = server.NewBrowser();

        using var first = await DesktopFlow.RefreshAsync(http, tokens.RefreshToken);
        var rotated = DesktopFlow.Parse(await first.Content.ReadAsStringAsync());
        using var replay = await DesktopFlow.RefreshAsync(http, tokens.RefreshToken);
        using var afterReplay = await DesktopFlow.RefreshAsync(http, rotated.RefreshToken);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(rotated.RefreshToken).IsNotEqualTo(tokens.RefreshToken);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        // Using the old token a second time signed the whole family out, including the newest token.
        await Assert.That(afterReplay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task AccessToken_LastsFifteenMinutes()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var tokens = await new DesktopFlow(server).SignInAsync(Email);
        using var http = server.NewBrowser();
        server.Clock.Advance(TimeSpan.FromMinutes(16));

        using var response = await http.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", tokens.AccessToken));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Accounts_AreIsolated()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var alice = new DesktopFlow(server);
        var bob = new DesktopFlow(server);
        var aliceTokens = await alice.SignInAsync("alice@example.com");
        var bobTokens = await bob.SignInAsync("bob@example.com");
        using var http = server.NewBrowser();
        (await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", aliceTokens.AccessToken, Registration(alice.DeviceId, "Alice PC")))).Dispose();
        (await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", bobTokens.AccessToken, Registration(bob.DeviceId, "Bob PC")))).Dispose();

        using var bobAccount = await http.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", bobTokens.AccessToken));
        using var revokeAlices = await http.SendAsync(DesktopFlow.Api(HttpMethod.Delete, $"/v1/devices/{alice.DeviceId}", bobTokens.AccessToken));
        using var claimAlices = await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", bobTokens.AccessToken, Registration(alice.DeviceId)));
        using var aliceAccount = await http.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", aliceTokens.AccessToken));

        var bobSees = (await bobAccount.Content.ReadFromJsonAsync<AccountDto>(JsonSerializerOptions.Web))!;
        await Assert.That(bobSees.Email).IsEqualTo("bob@example.com");
        await Assert.That(bobSees.Devices.Select(d => d.Name).ToArray()).IsEquivalentTo(new[] { "Bob PC" });
        await Assert.That(revokeAlices.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(claimAlices.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var aliceSees = (await aliceAccount.Content.ReadFromJsonAsync<AccountDto>(JsonSerializerOptions.Web))!;
        await Assert.That(aliceSees.Devices.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Authorize_ForADeviceOfAnotherAccount_IsRefused()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var alice = new DesktopFlow(server);
        var aliceTokens = await alice.SignInAsync("alice@example.com");
        using var http = server.NewBrowser();
        (await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", aliceTokens.AccessToken, Registration(alice.DeviceId)))).Dispose();

        var intruder = new DesktopFlow(server, alice.DeviceId);
        var exception = await Assert.That(async () => await intruder.SignInForCodeAsync(server.NewBrowser(), "bob@example.com")).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("error=access_denied");
    }

    [Test]
    public async Task RevokedDevice_LosesApiAccessAndRefresh()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var first = new DesktopFlow(server);
        var second = new DesktopFlow(server);
        var firstTokens = await first.SignInAsync(Email);
        var secondTokens = await second.SignInAsync(Email);
        using var http = server.NewBrowser();
        (await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", firstTokens.AccessToken, Registration(first.DeviceId, "One")))).Dispose();
        (await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", secondTokens.AccessToken, Registration(second.DeviceId, "Two")))).Dispose();

        using var revoke = await http.SendAsync(DesktopFlow.Api(HttpMethod.Delete, $"/v1/devices/{second.DeviceId}", firstTokens.AccessToken));
        using var api = await http.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", secondTokens.AccessToken));
        using var refresh = await DesktopFlow.RefreshAsync(http, secondTokens.RefreshToken);
        using var stillWorks = await http.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", firstTokens.AccessToken));

        await Assert.That(revoke.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(api.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(refresh.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(stillWorks.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var listed = (await stillWorks.Content.ReadFromJsonAsync<AccountDto>(JsonSerializerOptions.Web))!;
        await Assert.That(listed.Devices.Select(d => d.Name).ToArray()).IsEquivalentTo(new[] { "One" });
    }

    [Test]
    public async Task RegisterDevice_ForAnotherDeviceId_IsForbidden()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        var tokens = await flow.SignInAsync(Email);
        using var http = server.NewBrowser();

        using var response = await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, Registration(Guid.NewGuid())));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task RegisterDevice_Twice_UpdatesTheSameDevice()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        var tokens = await flow.SignInAsync(Email);
        using var http = server.NewBrowser();

        (await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, Registration(flow.DeviceId, "Old name")))).Dispose();
        using var again = await http.SendAsync(DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, Registration(flow.DeviceId, "New name")));

        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await using var db = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(await db.Devices.CountAsync()).IsEqualTo(1);
        await Assert.That((await db.Devices.SingleAsync()).Name).IsEqualTo("New name");
        await Assert.That(await db.AuditEvents.CountAsync(a => a.Kind == AuditKinds.DeviceAdded)).IsEqualTo(1);
    }

    [Test]
    public async Task SignInPage_NeverLoadsScripts_AndOffersOnlyConfiguredProviders()
    {
        await using var plain = await TestServer.StartAsync(postgres);
        await using var withGoogle = await TestServer.StartAsync(postgres, new()
        {
            ["Providers:Google:ClientId"] = "client",
            ["Providers:Google:ClientSecret"] = "secret",
        });
        var url = (await plain.NewBrowser().GetAsync(new DesktopFlow(plain).AuthorizeUrl())).Headers.Location!.OriginalString;

        var without = await plain.NewBrowser().GetStringAsync(url);
        var with = await withGoogle.NewBrowser().GetStringAsync(url);

        await Assert.That(without).DoesNotContain("<script");
        await Assert.That(without).DoesNotContain("/signin/external/");
        await Assert.That(without).Contains("<label for=\"email\">");
        await Assert.That(with).Contains("/signin/external/google");
        await Assert.That(with).DoesNotContain("/signin/external/github");
    }

    [Test]
    public async Task ExternalSignIn_ForAProviderThatIsNotConfigured_IsNotFound()
    {
        await using var server = await TestServer.StartAsync(postgres);

        using var response = await server.NewBrowser().GetAsync("/signin/external/google");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task SignIn_RateLimitedPerEmail()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["SignIn:MaxCodesPerEmailPerHour"] = "2" });
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var page = await browser.GetStringAsync((await browser.GetAsync(flow.AuthorizeUrl())).Headers.Location!.OriginalString);
        var returnUrl = DesktopFlow.Hidden(page, "returnUrl")!;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", Email), ("returnUrl", returnUrl)));
            statuses.Add(response.StatusCode);
        }

        await Assert.That(statuses.ToArray()).IsEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests });
        await Assert.That(server.Mail.Sent.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SignIn_RateLimitedPerIp()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["SignIn:MaxAttemptsPerIpPerHour"] = "3" });
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var page = await browser.GetStringAsync((await browser.GetAsync(flow.AuthorizeUrl())).Headers.Location!.OriginalString);
        var returnUrl = DesktopFlow.Hidden(page, "returnUrl")!;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", $"person{i}@example.com"), ("returnUrl", returnUrl)));
            statuses.Add(response.StatusCode);
        }

        await Assert.That(statuses.Last()).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(statuses.Take(3).All(s => s == HttpStatusCode.OK)).IsTrue();
    }

    [Test]
    public async Task SignIn_WhenMailFails_ShowsAnErrorAndLeavesNoLiveCode()
    {
        await using var server = await TestServer.StartAsync(postgres);
        server.Mail.Fail = true;
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var page = await browser.GetStringAsync((await browser.GetAsync(flow.AuthorizeUrl())).Headers.Location!.OriginalString);

        using var response = await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", Email), ("returnUrl", DesktopFlow.Hidden(page, "returnUrl")!)));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await using var db = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(await db.EmailCodes.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Forms_WithoutAnAntiforgeryToken_AreRefused()
    {
        await using var server = await TestServer.StartAsync(postgres);

        using var response = await server.NewBrowser().PostAsync("/signin/email", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = Email }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(server.Mail.Sent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Logs_ContainNoCodesTokensOrAddresses()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var flow = new DesktopFlow(server);
        using var browser = server.NewBrowser();
        var code = await flow.SignInForCodeAsync(browser, Email);
        using var exchanged = await flow.ExchangeCodeAsync(browser, code);
        var tokens = DesktopFlow.Parse(await exchanged.Content.ReadAsStringAsync());
        using var api = await browser.SendAsync(DesktopFlow.Api(HttpMethod.Get, "/v1/account", tokens.AccessToken));
        using var refreshed = await DesktopFlow.RefreshAsync(browser, tokens.RefreshToken);
        var emailCode = server.Mail.LastCode(Email);

        string[] secrets = [emailCode, code, tokens.AccessToken, tokens.RefreshToken, flow.Verifier, Email];
        string[] lines;
        lock (server.Logs.Lines)
        {
            lines = [.. server.Logs.Lines];
        }

        await Assert.That(lines.Length).IsGreaterThan(0);
        foreach (var secret in secrets)
        {
            await Assert.That(lines.Any(l => l.Contains(secret, StringComparison.Ordinal))).IsFalse();
        }
    }
}
