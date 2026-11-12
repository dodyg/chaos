using Chaos.Application.Abstractions;
using Chaos.Infrastructure.GitHub;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubCliCredentialSourceTests
{
    private sealed class Runner(string output, string version = "gh version 2.103.3 (fixture)") : IGitHubCliRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(arguments[0] == "--version" ? version : output);
        }
    }

    [Test]
    public async Task ReadAsync_SelectedAccount_UsesExplicitHostAndUser()
    {
        var runner = new Runner("fixture-a\r\n");
        var credential = await new GitHubCliCredentialSource(runner).ReadAsync("person", CancellationToken.None);
        await Assert.That(credential).IsEqualTo("fixture-a");
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
        await Assert.That(runner.Calls[1].SequenceEqual(new[] { "auth", "token", "--hostname", "github.com", "--user=person", "--no-refresh" })).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("fixture-a\nfixture-b")]
    [Arguments("fixture-a\r\nHeader: bad")]
    public async Task ReadAsync_AmbiguousOrMalformedOutput_IsRejected(string output)
    {
        await Assert.That(async () => await new GitHubCliCredentialSource(new Runner(output)).ReadAsync("person", CancellationToken.None)).Throws<GitHubApiException>();
    }

    [Test]
    public async Task ReadAsync_UnsafeLogin_DoesNotStartProcess()
    {
        var runner = new Runner("fixture-a");
        await Assert.That(async () => await new GitHubCliCredentialSource(runner).ReadAsync("--show-token", CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReadAsync_UnsupportedVersion_DoesNotRetrieveCredential()
    {
        var runner = new Runner("fixture-a", "gh version 2.0.0");
        await Assert.That(async () => await new GitHubCliCredentialSource(runner).ReadAsync("person", CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetActiveLoginAsync_WhitelistedMetadata_DoesNotRetrieveCredential()
    {
        var runner = new Runner("person\n");
        var login = await new GitHubCliCredentialSource(runner).GetActiveLoginAsync(CancellationToken.None);
        await Assert.That(login).IsEqualTo("person");
        await Assert.That(runner.Calls[1].SequenceEqual(new[] { "config", "get", "user", "--host", "github.com" })).IsTrue();
        await Assert.That(runner.Calls.Any(call => call.Contains("token"))).IsFalse();
    }
}
