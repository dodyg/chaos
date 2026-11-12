using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.GitHub;

/// <summary>CLI owns persistent authentication. This source returns only a short-lived in-memory snapshot.</summary>
public sealed class GitHubCliCredentialSource(IGitHubCliRunner runner) : IGitHubCredentialSource
{
    private static readonly Regex Login = new("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    public static readonly Version MinimumVersion = new(2, 103, 3);

    public async Task<string> GetActiveLoginAsync(CancellationToken ct)
    {
        await VerifyVersionAsync(ct);
        var login = (await runner.RunAsync(["config", "get", "user", "--host", "github.com"], ct)).Trim();
        if (!Login.IsMatch(login)) throw new GitHubApiException(GitHubErrorKind.Unauthorized);
        return login;
    }

    public async Task<string> ReadAsync(string login, CancellationToken ct)
    {
        if (!Login.IsMatch(login)) throw new GitHubApiException(GitHubErrorKind.AccountMismatch);
        await VerifyVersionAsync(ct);
        var snapshot = (await runner.RunAsync(["auth", "token", "--hostname", "github.com", "--user=" + login, "--no-refresh"], ct)).TrimEnd('\r', '\n');
        if (snapshot.Length == 0 || snapshot.Length > 16384 || snapshot.Any(character => character <= 32 || character >= 127))
            throw new GitHubApiException(GitHubErrorKind.Unauthorized);
        return snapshot;
    }

    private async Task VerifyVersionAsync(CancellationToken ct)
    {
        var output = await runner.RunAsync(["--version"], ct);
        var words = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || words[0] != "gh" || words[1] != "version" || !Version.TryParse(words[2], out var version) || version < MinimumVersion)
            throw new GitHubApiException(GitHubErrorKind.CliUnavailable);
    }
}
