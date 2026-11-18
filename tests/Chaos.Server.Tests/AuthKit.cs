using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chaos.Server.Tests;

/// <summary>An email sender that keeps what was sent, so a test can read the code.</summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public bool Fail { get; set; }

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (Fail)
        {
            throw new InvalidOperationException("mail is down");
        }

        lock (Sent)
        {
            Sent.Add((to, subject, body));
        }

        return Task.CompletedTask;
    }

    public string LastCode(string to)
    {
        lock (Sent)
        {
            var body = Sent.Last(m => m.To == to).Body;
            return Regex.Match(body, @"\b\d{6}\b").Value;
        }
    }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines)
            {
                owner.Lines.Add($"{category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}

/// <summary>A hosted server on a fresh database with a capturing mailer, a movable clock and a log capture.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    public required WebApplicationFactory<Program> Factory { get; init; }
    public required CapturingEmailSender Mail { get; init; }
    public required MutableTimeProvider Clock { get; init; }
    public required CapturingLoggerProvider Logs { get; init; }
    public required string ConnectionString { get; init; }

    public static async Task<TestServer> StartAsync(PostgresServer postgres, Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var connectionString = await ServerDatabase.CreateMigratedAsync(postgres);
        var mail = new CapturingEmailSender();
        var clock = new MutableTimeProvider();
        var logs = new CapturingLoggerProvider();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting($"ConnectionStrings:{ServerResources.DatabaseName}", connectionString);
            foreach (var (key, value) in settings ?? [])
            {
                host.UseSetting(key, value);
            }

            host.ConfigureServices(services =>
            {
                services.AddLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(logs);
                });
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(mail);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                configure?.Invoke(services);
            });
        });
        return new TestServer { Factory = factory, Mail = mail, Clock = clock, Logs = logs, ConnectionString = connectionString };
    }

    public HttpClient NewBrowser() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

public sealed record Tokens(string AccessToken, string RefreshToken);

/// <summary>What a desktop does against the server (PKCE, a loopback redirect, the sign-in pages), written as raw HTTP.</summary>
internal sealed class DesktopFlow(TestServer server, Guid? deviceId = null)
{
    public const string Redirect = "http://127.0.0.1:53211/callback";

    public Guid DeviceId { get; } = deviceId ?? Guid.NewGuid();
    public string Verifier { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));
    public string State { get; } = Base64Url(RandomNumberGenerator.GetBytes(12));
    public string Challenge => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    public string AuthorizeUrl(bool pkce = true) =>
        "/connect/authorize?client_id=chaos-desktop&response_type=code&scope=offline_access%20chaos"
        + $"&redirect_uri={Uri.EscapeDataString(Redirect)}&state={State}&device_id={DeviceId}"
        + (pkce ? $"&code_challenge={Challenge}&code_challenge_method=S256" : string.Empty);

    /// <summary>Signs in with an email code and returns the authorization code the loopback would receive.</summary>
    public async Task<string> SignInForCodeAsync(HttpClient browser, string email)
    {
        var authorize = await browser.GetAsync(AuthorizeUrl());
        var signIn = authorize.Headers.Location!.OriginalString;
        var page = await browser.GetStringAsync(signIn);
        var returnUrl = Hidden(page, "returnUrl")!;

        var codePage = await browser.PostAsync("/signin/email", Form(page, ("email", email), ("returnUrl", returnUrl)));
        var codeHtml = await codePage.Content.ReadAsStringAsync();
        var verified = await browser.PostAsync("/signin/verify", Form(codeHtml, ("email", email), ("code", server.Mail.LastCode(email)), ("returnUrl", returnUrl)));
        if (verified.StatusCode != HttpStatusCode.Redirect)
        {
            throw new InvalidOperationException($"Sign-in did not redirect: {(int)verified.StatusCode}");
        }

        var back = await browser.GetAsync(verified.Headers.Location!.OriginalString);
        var location = new Uri(back.Headers.Location!.OriginalString, UriKind.RelativeOrAbsolute);
        var query = Query(location.OriginalString);
        if (query.GetValueOrDefault("state") != State || !query.TryGetValue("code", out var code))
        {
            throw new InvalidOperationException("The redirect had no code: " + location.OriginalString);
        }

        return code;
    }

    public async Task<Tokens> SignInAsync(string email, HttpClient? browser = null)
    {
        browser ??= server.NewBrowser();
        var code = await SignInForCodeAsync(browser, email);
        var response = await ExchangeCodeAsync(browser, code);
        return Parse(await response.Content.ReadAsStringAsync());
    }

    public Task<HttpResponseMessage> ExchangeCodeAsync(HttpClient http, string code, string? verifier = null) =>
        http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "chaos-desktop", ["code"] = code,
            ["redirect_uri"] = Redirect, ["code_verifier"] = verifier ?? Verifier,
        }));

    public static Task<HttpResponseMessage> RefreshAsync(HttpClient http, string refreshToken) =>
        http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = "chaos-desktop", ["refresh_token"] = refreshToken,
        }));

    public static Tokens Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new Tokens(document.RootElement.GetProperty("access_token").GetString()!, document.RootElement.GetProperty("refresh_token").GetString()!);
    }

    public static HttpRequestMessage Api(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public static FormUrlEncodedContent Form(string page, params (string Key, string Value)[] fields)
    {
        var values = fields.ToDictionary(f => f.Key, f => f.Value);
        foreach (Match match in Regex.Matches(page, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">"))
        {
            var name = match.Groups[1].Value;
            if (name != "returnUrl" && name != "email")
            {
                values[name] = WebUtility.HtmlDecode(match.Groups[2].Value);
            }
        }

        return new FormUrlEncodedContent(values);
    }

    public static string? Hidden(string page, string name)
    {
        var match = Regex.Match(page, $"<input type=\"hidden\" name=\"{name}\" value=\"([^\"]*)\">");
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    public static Dictionary<string, string> Query(string url)
    {
        var at = url.IndexOf('?', StringComparison.Ordinal);
        var result = new Dictionary<string, string>();
        foreach (var pair in (at < 0 ? string.Empty : url[(at + 1)..]).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? string.Empty);
        }

        return result;
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
