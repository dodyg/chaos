using System.Net;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.GitHub;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubAccountTransportTests
{
    private sealed class Credentials : IGitHubCredentialSource
    {
        public int Calls { get; private set; }
        public string Current { get; set; } = "fixture-a";
        public Task<string> ReadAsync(string login, CancellationToken ct) { Calls++; return Task.FromResult(Current); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Test]
    public async Task GetAccountAsync_SelectedLogin_VerifiesIdentityBeforeConnect()
    {
        using var transport = new GitHubAccountTransport(new Credentials(), new Handler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1,\"login\":\"person\"}") }));
        await Assert.That(await transport.GetAccountAsync("person", CancellationToken.None)).IsEqualTo(new GitHubAccount(1, "person"));
        await Assert.That(async () => await transport.GetAccountAsync("another", CancellationToken.None)).Throws<GitHubApiException>();
    }

    [Test]
    public async Task SendAsync_IdentityCheckExhaustsQuota_DoesNotSendIssueRequest()
    {
        var calls = 0;
        using var transport = new GitHubAccountTransport(new Credentials(), new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1}") };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1800000000");
            return response;
        }));
        await Assert.That(async () => await transport.SendAsync(new(1, "person"), HttpMethod.Patch, new("https://api.github.com/repos/person/repo/issues/1"), "{}", null, CancellationToken.None))
            .Throws<GitHubApiException>().And.HasMessageContaining("RateLimited");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task SendAsync_OversizedResponse_IsRejectedWithoutExposingContent()
    {
        var calls = 0;
        using var transport = new GitHubAccountTransport(new Credentials(), new Handler(_ =>
        {
            var content = new StringContent(++calls == 1 ? "{\"id\":1}" : "private issue text");
            if (calls == 2) content.Headers.ContentLength = 17 * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.That(async () => await transport.SendAsync(new(1, "person"), HttpMethod.Get, new("https://api.github.com/repos/person/repo"), null, null, CancellationToken.None)).Throws<GitHubApiException>().And.HasMessageContaining("Server");
    }

    [Test]
    public async Task SendAsync_AccountSwitchDuringIdentityCheck_UsesSameSnapshot()
    {
        var credentials = new Credentials();
        var sent = new List<string>();
        using var transport = new GitHubAccountTransport(credentials, new Handler(request =>
        {
            sent.Add(request.Headers.Authorization!.Parameter!);
            credentials.Current = "fixture-b";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1}") };
        }));
        using var response = await transport.SendAsync(new(1, "person"), HttpMethod.Patch, new("https://api.github.com/repos/person/repo/issues/1"), "{}", null, CancellationToken.None);
        await Assert.That(credentials.Calls).IsEqualTo(1);
        await Assert.That(sent.SequenceEqual(new[] { "fixture-a", "fixture-a" })).IsTrue();
    }

    [Test]
    public async Task SendAsync_WrongAccount_DoesNotSendIssueBody()
    {
        var calls = 0;
        using var transport = new GitHubAccountTransport(new Credentials(), new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":2}") };
        }));
        await Assert.That(async () => await transport.SendAsync(new(1, "person"), HttpMethod.Patch,
            new("https://api.github.com/repos/person/repo/issues/1"), "private issue text", null, CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("http://api.github.com/user")]
    [Arguments("https://api.github.com.evil.test/user")]
    [Arguments("https://api.github.com:444/user")]
    [Arguments("https://user@api.github.com/user")]
    public async Task SendAsync_UnsafeDestination_RejectsBeforeCredentialLookup(string url)
    {
        var credentials = new Credentials();
        using var transport = new GitHubAccountTransport(credentials, new Handler(_ => throw new InvalidOperationException("Should not send")));
        await Assert.That(async () => await transport.SendAsync(new(1, "person"), HttpMethod.Get, new(url), null, null, CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(credentials.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(301, GitHubErrorKind.Moved)]
    [Arguments(401, GitHubErrorKind.Unauthorized)]
    [Arguments(403, GitHubErrorKind.Forbidden)]
    [Arguments(404, GitHubErrorKind.NotFound)]
    [Arguments(410, GitHubErrorKind.Gone)]
    [Arguments(422, GitHubErrorKind.Validation)]
    [Arguments(429, GitHubErrorKind.RateLimited)]
    [Arguments(500, GitHubErrorKind.Server)]
    public async Task SendAsync_ApiFailure_IsClassifiedWithoutLeakingResponse(int status, GitHubErrorKind expected)
    {
        var calls = 0;
        using var transport = new GitHubAccountTransport(new Credentials(), new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(calls == 1 ? HttpStatusCode.OK : (HttpStatusCode)status)
            { Content = new StringContent(calls == 1 ? "{\"id\":1}" : "private issue text fixture-a") };
        }));
        GitHubApiException? caught = null;
        try { using var response = await transport.SendAsync(new(1, "person"), HttpMethod.Get, new("https://api.github.com/repos/person/repo"), null, null, CancellationToken.None); }
        catch (GitHubApiException error) { caught = error; }
        await Assert.That(caught!.Kind).IsEqualTo(expected);
        await Assert.That(caught.Message.Contains("private", StringComparison.Ordinal)).IsFalse();
        await Assert.That(caught.Message.Contains("fixture-a", StringComparison.Ordinal)).IsFalse();
        await Assert.That(calls).IsEqualTo(2);
    }
}
