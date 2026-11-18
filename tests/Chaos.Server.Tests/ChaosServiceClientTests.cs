using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Service;

namespace Chaos.Server.Tests;

internal sealed class MemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        lock (Values)
        {
            Values[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (Values)
        {
            return Task.FromResult(Values.GetValueOrDefault(key));
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        lock (Values)
        {
            Values.Remove(key);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FixedDeviceIdentity(string name = "Test PC") : IDeviceIdentity
{
    public Guid Id { get; } = Guid.NewGuid();

    public Task<DeviceIdentity> GetAsync(CancellationToken ct) => Task.FromResult(new DeviceIdentity(Id, name, "windows", "0.2.1", 1));
}

/// <summary>A browser that signs in the way a person would: the sign-in pages, an email code, then the loopback redirect.</summary>
internal sealed class FakeBrowser(Func<Uri, Task> drive) : ISystemBrowser
{
    public Task? Running { get; private set; }

    public Uri? Opened { get; private set; }

    public bool Open(Uri url)
    {
        Opened = url;
        Running = Task.Run(() => drive(url));
        return true;
    }

    /// <summary>Signs in on the server and returns the loopback redirect the server answered with.</summary>
    public static async Task<string> SignInAtServerAsync(TestServer server, Uri authorizeUrl, string email)
    {
        using var browser = server.NewBrowser();
        var first = await browser.GetAsync(authorizeUrl.PathAndQuery);
        var page = await browser.GetStringAsync(first.Headers.Location!.OriginalString);
        var returnUrl = DesktopFlow.Hidden(page, "returnUrl")!;
        var codePage = await (await browser.PostAsync("/signin/email", DesktopFlow.Form(page, ("email", email), ("returnUrl", returnUrl)))).Content.ReadAsStringAsync();
        var verified = await browser.PostAsync("/signin/verify", DesktopFlow.Form(codePage, ("email", email), ("code", server.Mail.LastCode(email)), ("returnUrl", returnUrl)));
        var back = await browser.GetAsync(verified.Headers.Location!.OriginalString);
        return back.Headers.Location!.OriginalString;
    }

    public static FakeBrowser Person(TestServer server, string email) => new(async url =>
    {
        var redirect = await SignInAtServerAsync(server, url, email);
        using var loopback = new HttpClient();
        using var response = await loopback.GetAsync(redirect);
    });
}

internal sealed class CountingHandler : DelegatingHandler
{
    private int _tokenCalls;

    public int TokenCalls => Volatile.Read(ref _tokenCalls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/connect/token")
        {
            Interlocked.Increment(ref _tokenCalls);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>PLAN2 P12 item 5/6: the desktop client signs in against the in-process server with a fake browser that follows redirects.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class ChaosServiceClientTests(PostgresServer postgres)
{
    private const string Email = "person@example.com";

    private static ChaosServiceClient NewClient(TestServer server, FakeBrowser browser, MemorySecretStore secrets, FixedDeviceIdentity identity, HttpClient? http = null) =>
        new(ChaosServiceOptions.TryCreate("http://localhost/", allowLoopbackHttp: true), browser, secrets, identity, server.Clock, http ?? server.Factory.CreateClient())
        {
            SignInTimeout = TimeSpan.FromSeconds(30),
        };

    [Test]
    public async Task SignIn_ThroughTheBrowser_RegistersTheDeviceAndKeepsOnlyTheRefreshToken()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var secrets = new MemorySecretStore();
        var identity = new FixedDeviceIdentity("Dody's laptop");
        var browser = FakeBrowser.Person(server, Email);
        using var client = NewClient(server, browser, secrets, identity);

        var account = await client.SignInAsync(CancellationToken.None);
        var session = await client.GetSessionAsync(CancellationToken.None);

        await Assert.That(account.Email).IsEqualTo(Email);
        await Assert.That(account.Devices.Count).IsEqualTo(1);
        await Assert.That(account.Devices[0].Id).IsEqualTo(identity.Id);
        await Assert.That(account.Devices[0].Name).IsEqualTo("Dody's laptop");
        await Assert.That(account.Devices[0].Current).IsTrue();
        await Assert.That(session.SignedIn).IsTrue();
        await Assert.That(session.Email).IsEqualTo(Email);
        await Assert.That(secrets.Values.Keys.Order().ToArray()).IsEquivalentTo(new[] { ChaosServiceClient.EmailKey, ChaosServiceClient.RefreshKey });
        // The browser was sent to the authorize endpoint with PKCE, a state and this device's ID.
        var opened = browser.Opened!.Query;
        await Assert.That(opened).Contains("code_challenge_method=S256");
        await Assert.That(opened).Contains("state=");
        await Assert.That(opened).Contains($"device_id={identity.Id}");
        await Assert.That(opened).DoesNotContain("code_verifier");
    }

    [Test]
    public async Task SignIn_RedirectWithTheWrongState_IsIgnored()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var secrets = new MemorySecretStore();
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var forgedStatus = 0;
        var browser = new FakeBrowser(async url =>
        {
            var redirect = await FakeBrowser.SignInAtServerAsync(server, url, Email);
            var forged = redirect.Replace("state=", "state=forged", StringComparison.Ordinal);
            using var loopback = new HttpClient();
            using var response = await loopback.GetAsync(forged);
            forgedStatus = (int)response.StatusCode;
            // The listener keeps waiting for the real redirect; stop the sign-in from here.
            await giveUp.CancelAsync();
        });
        using var client = NewClient(server, browser, secrets, new FixedDeviceIdentity());

        var cancelled = false;
        try
        {
            await client.SignInAsync(giveUp.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await browser.Running!;
        await Assert.That(forgedStatus).IsEqualTo(400);
        await Assert.That(cancelled).IsTrue();
        await Assert.That(secrets.Values.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAccessToken_ManyCallersAfterExpiry_ShareOneRefresh()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var secrets = new MemorySecretStore();
        var counter = new CountingHandler();
        using var client = NewClient(server, FakeBrowser.Person(server, Email), secrets, new FixedDeviceIdentity(), server.Factory.CreateDefaultClient(counter));
        await client.SignInAsync(CancellationToken.None);
        var before = secrets.Values[ChaosServiceClient.RefreshKey];
        var callsAfterSignIn = counter.TokenCalls;
        server.Clock.Advance(TimeSpan.FromMinutes(16));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAccessTokenAsync(CancellationToken.None)));

        await Assert.That(counter.TokenCalls - callsAfterSignIn).IsEqualTo(1);
        await Assert.That(tokens.Distinct().Count()).IsEqualTo(1);
        await Assert.That(secrets.Values[ChaosServiceClient.RefreshKey]).IsNotEqualTo(before);
        // The rotated token works, and the account is still reachable.
        await Assert.That((await client.GetAccountAsync(CancellationToken.None)).Email).IsEqualTo(Email);
    }

    [Test]
    public async Task GetAccessToken_WithAFreshToken_DoesNotCallTheServer()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var counter = new CountingHandler();
        using var client = NewClient(server, FakeBrowser.Person(server, Email), new MemorySecretStore(), new FixedDeviceIdentity(), server.Factory.CreateDefaultClient(counter));
        await client.SignInAsync(CancellationToken.None);
        var calls = counter.TokenCalls;

        await client.GetAccessTokenAsync(CancellationToken.None);
        await client.GetAccessTokenAsync(CancellationToken.None);

        await Assert.That(counter.TokenCalls).IsEqualTo(calls);
    }

    [Test]
    public async Task RemovedDevice_NextRefresh_SignsItOutHere()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var secretsA = new MemorySecretStore();
        var identityA = new FixedDeviceIdentity("A");
        using var a = NewClient(server, FakeBrowser.Person(server, Email), secretsA, identityA);
        using var b = NewClient(server, FakeBrowser.Person(server, Email), new MemorySecretStore(), new FixedDeviceIdentity("B"));
        await a.SignInAsync(CancellationToken.None);
        await b.SignInAsync(CancellationToken.None);

        await b.RevokeDeviceAsync(identityA.Id, CancellationToken.None);
        server.Clock.Advance(TimeSpan.FromMinutes(16));
        var error = await Assert.That(async () => await a.GetAccessTokenAsync(CancellationToken.None)).Throws<ChaosServiceException>();

        await Assert.That(error!.Kind).IsEqualTo(ServiceFailure.SignInAgain);
        await Assert.That(secretsA.Values.Count).IsEqualTo(0);
        await Assert.That((await a.GetSessionAsync(CancellationToken.None)).SignedIn).IsFalse();
        await Assert.That((await b.GetAccountAsync(CancellationToken.None)).Devices.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RemovingThisDevice_SignsOutLocally()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var secrets = new MemorySecretStore();
        var identity = new FixedDeviceIdentity();
        using var client = NewClient(server, FakeBrowser.Person(server, Email), secrets, identity);
        await client.SignInAsync(CancellationToken.None);

        await client.RevokeDeviceAsync(identity.Id, CancellationToken.None);

        await Assert.That(secrets.Values.Count).IsEqualTo(0);
        await Assert.That((await client.GetSessionAsync(CancellationToken.None)).SignedIn).IsFalse();
    }

    [Test]
    public async Task SignOut_ForgetsTheTokens_AndLaterCallsAskToSignInAgain()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var secrets = new MemorySecretStore();
        using var client = NewClient(server, FakeBrowser.Person(server, Email), secrets, new FixedDeviceIdentity());
        await client.SignInAsync(CancellationToken.None);

        await client.SignOutAsync(CancellationToken.None);
        var error = await Assert.That(async () => await client.GetAccountAsync(CancellationToken.None)).Throws<ChaosServiceException>();

        await Assert.That(secrets.Values.Count).IsEqualTo(0);
        await Assert.That(error!.Kind).IsEqualTo(ServiceFailure.SignInAgain);
    }

    [Test]
    public async Task Offline_IsReportedAsOffline_AndKeepsTheSignIn()
    {
        var secrets = new MemorySecretStore();
        await secrets.SetAsync(ChaosServiceClient.RefreshKey, "kept");
        using var client = new ChaosServiceClient(ChaosServiceOptions.TryCreate("http://127.0.0.1:1/", true), new FakeBrowser(_ => Task.CompletedTask), secrets, new FixedDeviceIdentity());

        var error = await Assert.That(async () => await client.GetAccessTokenAsync(CancellationToken.None)).Throws<ChaosServiceException>();

        await Assert.That(error!.Kind).IsEqualTo(ServiceFailure.Offline);
        await Assert.That(secrets.Values[ChaosServiceClient.RefreshKey]).IsEqualTo("kept");
    }

    [Test]
    public async Task NotConfigured_OffersNothing()
    {
        using var client = new ChaosServiceClient((ChaosServiceOptions?)null, new FakeBrowser(_ => Task.CompletedTask), new MemorySecretStore(), new FixedDeviceIdentity());

        var session = await client.GetSessionAsync(CancellationToken.None);
        var error = await Assert.That(async () => await client.SignInAsync(CancellationToken.None)).Throws<ChaosServiceException>();

        await Assert.That(session.Configured).IsFalse();
        await Assert.That(error!.Kind).IsEqualTo(ServiceFailure.NotConfigured);
    }

    [Test]
    [Arguments("https://sync.example.com", false, "https://sync.example.com/")]
    [Arguments("https://sync.example.com/app/", false, "https://sync.example.com/app/")]
    [Arguments("http://127.0.0.1:5080", true, "http://127.0.0.1:5080/")]
    [Arguments("http://localhost:5080", true, "http://localhost:5080/")]
    [Arguments("http://127.0.0.1:5080", false, null)]
    [Arguments("http://sync.example.com", true, null)]
    [Arguments("ftp://sync.example.com", true, null)]
    [Arguments("https://user:pw@sync.example.com", false, null)]
    [Arguments("https://sync.example.com/?x=1", false, null)]
    [Arguments("not a url", true, null)]
    [Arguments("", true, null)]
    public async Task ServiceAddress_MustBeHttps_ExceptLoopbackInDevelopment(string address, bool allowLoopbackHttp, string? expected)
    {
        var options = ChaosServiceOptions.TryCreate(address, allowLoopbackHttp);

        await Assert.That(options?.BaseAddress.ToString()).IsEqualTo(expected);
    }
}
