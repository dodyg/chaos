namespace Chaos.Application.GitHub;

/// <summary>
/// A GitHub login as GitHub allows it: 1–39 ASCII letters, digits or hyphens, not beginning or ending with a hyphen.
/// Logins are matched without regard to case, as GitHub does. An optional leading <c>@</c> is accepted on input.
/// </summary>
public static class GitHubLogin
{
    public static bool IsValid(string? login) =>
        login is { Length: > 0 and <= 39 } && !login.StartsWith('-') && !login.EndsWith('-')
        && login.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    /// <summary>The login as stored, or null when the input is blank (which clears a member's login).</summary>
    public static string? Normalize(string? login)
    {
        var trimmed = login?.Trim().TrimStart('@');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static bool Matches(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
