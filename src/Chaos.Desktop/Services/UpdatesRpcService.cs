using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record UpdateCheckRequest();
/// <param name="Status">UpToDate, Available, NoReleases or Failed.</param>
public sealed record UpdateCheckResponse(bool Ok, string Status, string CurrentVersion, string? LatestVersion, string? PublishedAtUtc, string? Message);
public sealed record OpenReleasesRequest();
public sealed record OpenReleasesResponse(bool Ok);

/// <summary>Settings → This computer → Check for updates: compares this version with the newest GitHub release.</summary>
[NeoRpcService("updates", Version = 1)]
public sealed class UpdatesRpcService(IReleaseFeed feed, ISystemBrowser browser, string appVersion)
{
    public static readonly Uri ReleasesPage = new("https://github.com/dodyg/chaos/releases");

    [NeoRpcMethod("check")]
    public async ValueTask<UpdateCheckResponse> CheckAsync(UpdateCheckRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("updates.check");
        try
        {
            var latest = await feed.LatestAsync(ct);
            if (latest is null)
            {
                return new(true, "NoReleases", appVersion, null, null, null);
            }

            var status = ReleaseVersions.IsNewer(appVersion, latest.Version) ? "Available" : "UpToDate";
            return new(true, status, appVersion, latest.Version, latest.PublishedAtUtc, null);
        }
        catch (UpdateCheckException error)
        {
            return new(false, "Failed", appVersion, null, null, error.Message);
        }
    }

    /// <summary>Opens the fixed releases page; the frontend never supplies an address.</summary>
    [NeoRpcMethod("openReleases")]
    public ValueTask<OpenReleasesResponse> OpenReleasesAsync(OpenReleasesRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("updates.openReleases");
        return ValueTask.FromResult(new OpenReleasesResponse(browser.Open(ReleasesPage)));
    }
}
