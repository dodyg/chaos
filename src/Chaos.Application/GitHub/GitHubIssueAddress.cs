using System.Globalization;
using System.Text.RegularExpressions;

namespace Chaos.Application.GitHub;

/// <summary>Recognizes source identities without following a URL or accepting credentials/query strings.</summary>
public sealed partial record GitHubIssueAddress(string Owner, string Repository, long Number)
{
    public string WebUrl => $"https://github.com/{Owner}/{Repository}/issues/{Number.ToString(CultureInfo.InvariantCulture)}";
    public static bool TryParse(string? url, out GitHubIssueAddress address)
    {
        address = null!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var offset = uri.Host == "api.github.com" && parts.Length == 5 && parts[0] == "repos" ? 1 : uri.Host == "github.com" && parts.Length == 4 ? 0 : -1;
        if (offset < 0 || !Segment(parts[offset]) || !Segment(parts[offset + 1]) || parts[offset + 2] != "issues" || !long.TryParse(parts[offset + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return false;
        address = new(parts[offset], parts[offset + 1], number); return true;
    }
    public bool IsIn(string owner, string repository) => Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) && Repository.Equals(repository, StringComparison.OrdinalIgnoreCase);
    public static bool Segment(string value) => value is not "." and not ".." && SegmentPattern().IsMatch(value);
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)] private static partial Regex SegmentPattern();
}
