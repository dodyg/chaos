namespace Chaos.Application.Abstractions;

/// <summary>The newest published release: its tag (such as v1.2.3), the version inside it and its page.</summary>
public sealed record ReleaseInfo(string Tag, string Version, string Url, string? PublishedAtUtc);

/// <summary>A failure the person can read: offline, rate limited or an unexpected answer.</summary>
public sealed class UpdateCheckException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Finds the newest published release. Null when none has been published yet.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> LatestAsync(CancellationToken ct);
}

public static class ReleaseVersions
{
    /// <summary>Reads "v1.2.3", "1.2" or "1.2.3-beta" as a version; false when the text is not one.</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            core = core[..cut];
        }

        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    public static bool IsNewer(string current, string latest) =>
        TryParse(current, out var have) && TryParse(latest, out var want) && want > have;
}
